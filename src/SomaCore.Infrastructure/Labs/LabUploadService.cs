using System.Diagnostics;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SomaCore.Domain.Agent;
using SomaCore.Domain.Common;
using SomaCore.Domain.LabUploads;
using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Infrastructure.Labs;

public interface ILabUploadService
{
    /// <summary>
    /// Stores the PDF, extracts biomarkers via Anthropic (the file is
    /// transmitted once, at parse time — privacy Section F.3), validates
    /// against the taxonomy, and persists rows with status 'parsed'.
    /// The coach reads NOTHING until <see cref="ConfirmAsync"/>.
    /// </summary>
    Task<Result<LabUpload>> UploadAsync(
        Guid userId, string fileName, byte[] bytes, CancellationToken ct);

    /// <summary>The user attests the extraction is correct; the upload becomes coach-readable.</summary>
    Task<Result<bool>> ConfirmAsync(Guid userId, Guid uploadId, CancellationToken ct);

    Task<Result<bool>> DeleteAsync(Guid userId, Guid uploadId, CancellationToken ct);
}

public sealed class LabUploadService : ILabUploadService
{
    private const string FunctionSource = "function_health";

    private readonly SomaCoreDbContext _db;
    private readonly LabsOptions _options;
    private readonly AnthropicOptions _anthropicOptions;
    private readonly ILogger<LabUploadService> _logger;
    private readonly AnthropicMessagesClient? _client;

    public LabUploadService(
        SomaCoreDbContext db,
        IOptions<LabsOptions> options,
        IOptions<AnthropicOptions> anthropicOptions,
        ILogger<LabUploadService> logger,
        AnthropicMessagesClient? client = null)
    {
        _db = db;
        _options = options.Value;
        _anthropicOptions = anthropicOptions.Value;
        _logger = logger;
        _client = client;
    }

    public async Task<Result<LabUpload>> UploadAsync(
        Guid userId, string fileName, byte[] bytes, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return Result<LabUpload>.Failure("Lab uploads are not enabled.");
        }
        if (_client is null || !_anthropicOptions.Enabled)
        {
            return Result<LabUpload>.Failure("Lab extraction requires the Anthropic client.");
        }
        if (bytes.Length == 0 || bytes.Length > _options.MaxUploadBytes)
        {
            return Result<LabUpload>.Failure("PDFs up to 10 MB are supported.");
        }
        // PDF magic bytes — extension lies, headers don't.
        if (bytes.Length < 5 || bytes[0] != 0x25 || bytes[1] != 0x50 || bytes[2] != 0x44 || bytes[3] != 0x46)
        {
            return Result<LabUpload>.Failure("That doesn't look like a PDF.");
        }

        var invocationId = Guid7Generator.NewId();
        var stopwatch = Stopwatch.StartNew();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        AnthropicMessageResponse? response = null;
        Result<LabPanelDraft> panel;
        try
        {
            response = await _client.SendAsync(BuildRequest(bytes, invocationId), ct);
            panel = LabPanelResponseValidator.Validate(response, today);
        }
        catch (Exception ex)
        {
            panel = Result<LabPanelDraft>.Failure($"Extraction call failed: {ex.Message}");
        }
        stopwatch.Stop();
        await LogInvocationAsync(invocationId, userId, response, stopwatch,
            panel.IsSuccess ? null : panel.Error, ct);

        var upload = new LabUpload
        {
            UserId = userId,
            Source = FunctionSource,
            UploadedAt = DateTimeOffset.UtcNow,
            CollectedAt = panel.IsSuccess ? panel.Value!.CollectedAt : null,
            FileName = Path.GetFileName(fileName),
            FileBytes = bytes,
            FileSize = bytes.Length,
            ParseStatus = panel.IsSuccess ? "parsed" : "failed",
            ParseError = panel.IsSuccess ? null : Truncate(panel.Error!, 2000),
            ParsedAt = panel.IsSuccess ? DateTimeOffset.UtcNow : null,
            TraceId = Activity.Current?.TraceId.ToString(),
        };

        if (panel.IsSuccess)
        {
            // Same panel re-uploaded (user, source, collection date) replaces
            // the previous rows wholesale — improved extraction, not duplicates.
            var previous = await _db.LabUploads
                .Where(u => u.UserId == userId
                         && u.Source == FunctionSource
                         && u.CollectedAt == panel.Value!.CollectedAt)
                .FirstOrDefaultAsync(ct);
            if (previous is not null)
            {
                _db.LabUploads.Remove(previous); // biomarkers cascade
            }

            foreach (var b in panel.Value!.Biomarkers)
            {
                upload.Biomarkers.Add(new LabBiomarker
                {
                    UserId = userId,
                    BiomarkerName = b.BiomarkerName,
                    DisplayName = b.DisplayName,
                    Category = b.Category,
                    NumericValue = b.NumericValue,
                    StringValue = b.StringValue,
                    Unit = b.Unit,
                    ReferenceLow = b.ReferenceLow,
                    ReferenceHigh = b.ReferenceHigh,
                    ReferenceString = b.ReferenceString,
                    CollectedAt = panel.Value.CollectedAt,
                    Flagged = LabPanelResponseValidator.ComputeFlag(b),
                });
            }
        }

        _db.LabUploads.Add(upload);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Lab upload for user {UserId}: {FileName} status={ParseStatus} biomarkers={Count}",
            userId, upload.FileName, upload.ParseStatus, upload.Biomarkers.Count);

        return panel.IsSuccess
            ? Result<LabUpload>.Success(upload)
            : Result<LabUpload>.Failure(
                "The panel couldn't be read reliably, so nothing was extracted. It's saved for review — try re-downloading the PDF from Function and uploading again.");
    }

    public async Task<Result<bool>> ConfirmAsync(Guid userId, Guid uploadId, CancellationToken ct)
    {
        var upload = await _db.LabUploads
            .FirstOrDefaultAsync(u => u.Id == uploadId && u.UserId == userId, ct);
        if (upload is null)
        {
            return Result<bool>.Failure("Upload not found.");
        }
        if (upload.ParseStatus == "confirmed")
        {
            return Result<bool>.Success(true);
        }
        if (upload.ParseStatus != "parsed")
        {
            return Result<bool>.Failure("Only a successfully parsed upload can be confirmed.");
        }

        upload.ParseStatus = "confirmed";
        upload.ConfirmedBy = userId;
        upload.ConfirmedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Result<bool>.Success(true);
    }

    public async Task<Result<bool>> DeleteAsync(Guid userId, Guid uploadId, CancellationToken ct)
    {
        var deleted = await _db.LabUploads
            .Where(u => u.Id == uploadId && u.UserId == userId)
            .ExecuteDeleteAsync(ct); // biomarkers cascade
        return deleted > 0
            ? Result<bool>.Success(true)
            : Result<bool>.Failure("Nothing deleted.");
    }

    private AnthropicMessageRequest BuildRequest(byte[] bytes, Guid invocationId)
    {
        var categories = string.Join("|", FunctionBiomarkerTaxonomy.KnownCategories);
        var names = string.Join(", ", FunctionBiomarkerTaxonomy.KnownNames.OrderBy(n => n));

        var inputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "panel_json": {
                  "type": "string",
                  "description": "JSON object: {collected_at: 'YYYY-MM-DD', biomarkers: [{biomarker_name, display_name, category, numeric_value?, string_value?, unit?, reference_low?, reference_high?, reference_string?}]}. See the system prompt for the canonical name list."
                }
              },
              "required": ["panel_json"]
            }
            """).RootElement.Clone();

        var system =
$@"You extract structured biomarker data from a lab results PDF (Function Health panel).
Call `{LabPanelResponseValidator.ToolName}` exactly once.

Rules:
- Extract ONLY values printed in the document. Never infer, estimate, or fill gaps.
- biomarker_name MUST be one of these canonical names (skip any marker on the
  PDF that has no canonical name here — do not invent names):
{names}
- category MUST be one of: {categories}
- display_name is the marker's name exactly as printed.
- numeric_value for numeric results; string_value for qualitative ones
  ('Negative', 'Within range'). At least one of the two.
- reference_low/reference_high for numeric ranges; reference_string for
  non-numeric ones ('< 130').
- collected_at is the specimen collection date printed on the report.
- Skip personal identifiers entirely: patient name, DOB, MRN, requisition
  numbers, clinician names. They must not appear anywhere in your output.";

        return new AnthropicMessageRequest(
            Model: _anthropicOptions.ModelId,
            MaxTokens: 8192,
            System: system,
            Messages: new[]
            {
                new AnthropicMessage("user", new object[]
                {
                    AnthropicDocumentBlock.Pdf(bytes),
                    AnthropicTextBlock.Of("Extract the biomarker panel."),
                }),
            },
            Tools: new[]
            {
                new AnthropicTool(LabPanelResponseValidator.ToolName,
                    "Submit the extracted panel. Call exactly once.", inputSchema),
            },
            ToolChoice: new AnthropicToolChoice("tool", LabPanelResponseValidator.ToolName),
            Metadata: new AnthropicMessageMetadata(invocationId.ToString("N")),
            Temperature: 0);
    }

    private async Task LogInvocationAsync(
        Guid invocationId, Guid userId, AnthropicMessageResponse? response,
        Stopwatch stopwatch, string? error, CancellationToken ct)
    {
        _db.AgentInvocations.Add(new AgentInvocation
        {
            Id = invocationId,
            UserId = userId,
            Kind = AgentInvocationKinds.LabExtraction,
            // Neither the PDF nor the extracted values are logged here — the
            // values live in lab_biomarkers where the user can delete them.
            InputSnapshot = JsonDocument.Parse("""{"note":"lab pdf extraction; file bytes and values not logged"}"""),
            TodaysRead = "",
            ActionsJson = JsonDocument.Parse("[]"),
            ModelId = response?.Model ?? "",
            InputTokens = response?.Usage?.InputTokens,
            CachedInputTokens = response?.Usage?.CacheReadInputTokens ?? response?.Usage?.CacheCreationInputTokens,
            OutputTokens = response?.Usage?.OutputTokens,
            DurationMs = (int)stopwatch.Elapsed.TotalMilliseconds,
            ErrorMessage = error is { Length: > 2000 } e ? e[..2000] : error,
            TraceId = Activity.Current?.TraceId.ToString(),
        });
        await _db.SaveChangesAsync(ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

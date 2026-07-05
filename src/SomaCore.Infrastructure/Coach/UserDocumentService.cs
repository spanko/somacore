using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SomaCore.Domain.Agent;
using SomaCore.Domain.Common;
using SomaCore.Domain.UserDocuments;
using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Infrastructure.Coach;

public interface IUserDocumentService
{
    /// <summary>
    /// Stores + extracts a document in one pass. Text formats read
    /// directly; PDFs go to Anthropic ONCE for text extraction (the same
    /// posture as privacy draft Part 1's F.3 — the file itself is
    /// transmitted at parse time, never again after).
    /// </summary>
    Task<Result<UserDocument>> UploadAsync(
        Guid userId, string fileName, string contentType, byte[] bytes, CancellationToken ct);

    Task<Result<bool>> DeleteAsync(Guid userId, Guid documentId, CancellationToken ct);
}

public sealed class UserDocumentService : IUserDocumentService
{
    private const string ExtractionToolName = "submit_document_text";

    private static readonly string[] TextContentTypes =
        { "text/plain", "text/csv", "text/markdown", "application/csv" };

    private static readonly string[] TextExtensions = { ".txt", ".csv", ".md", ".tsv" };

    private readonly SomaCoreDbContext _db;
    private readonly CoachChatOptions _options;
    private readonly AnthropicOptions _anthropicOptions;
    private readonly ILogger<UserDocumentService> _logger;
    private readonly AnthropicMessagesClient? _client;

    public UserDocumentService(
        SomaCoreDbContext db,
        IOptions<CoachChatOptions> options,
        IOptions<AnthropicOptions> anthropicOptions,
        ILogger<UserDocumentService> logger,
        AnthropicMessagesClient? client = null)
    {
        _db = db;
        _options = options.Value;
        _anthropicOptions = anthropicOptions.Value;
        _logger = logger;
        _client = client;
    }

    public async Task<Result<UserDocument>> UploadAsync(
        Guid userId, string fileName, string contentType, byte[] bytes, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return Result<UserDocument>.Failure("Document upload is not enabled.");
        }
        if (bytes.Length == 0)
        {
            return Result<UserDocument>.Failure("The file is empty.");
        }
        if (bytes.Length > _options.MaxDocumentBytes)
        {
            return Result<UserDocument>.Failure("Files up to 10 MB are supported.");
        }

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var isPdf = contentType == "application/pdf" || extension == ".pdf";
        var isText = TextContentTypes.Contains(contentType) || TextExtensions.Contains(extension);

        if (!isPdf && !isText)
        {
            return Result<UserDocument>.Failure(
                "Supported formats: PDF, CSV, TXT, Markdown.");
        }

        string? extractedText;
        string? summary;
        string parseStatus;
        string? parseError = null;

        if (isText)
        {
            extractedText = Truncate(Encoding.UTF8.GetString(bytes), _options.MaxDocumentChars);
            summary = BuildTextSummary(fileName, extractedText);
            parseStatus = "parsed";
        }
        else
        {
            var extraction = await ExtractPdfAsync(userId, bytes, ct);
            if (extraction.IsSuccess)
            {
                (extractedText, summary) = extraction.Value;
                parseStatus = "parsed";
            }
            else
            {
                extractedText = null;
                summary = null;
                parseStatus = "failed";
                parseError = extraction.Error;
            }
        }

        var document = new UserDocument
        {
            UserId = userId,
            FileName = Path.GetFileName(fileName),
            ContentType = isPdf ? "application/pdf" : "text/plain",
            FileBytes = bytes,
            FileSize = bytes.Length,
            ParseStatus = parseStatus,
            ParseError = parseError,
            Summary = summary,
            ExtractedText = extractedText,
            UploadedAt = DateTimeOffset.UtcNow,
            TraceId = Activity.Current?.TraceId.ToString(),
        };
        _db.UserDocuments.Add(document);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Document uploaded for user {UserId}: {FileName} ({FileSize} bytes, status={ParseStatus})",
            userId, document.FileName, document.FileSize, parseStatus);

        return parseStatus == "parsed"
            ? Result<UserDocument>.Success(document)
            : Result<UserDocument>.Failure(
                "The file was saved but couldn't be read — the coach won't be able to discuss it. Try re-exporting it.");
    }

    private async Task<Result<(string Text, string Summary)>> ExtractPdfAsync(
        Guid userId, byte[] bytes, CancellationToken ct)
    {
        if (_client is null || !_anthropicOptions.Enabled)
        {
            return Result<(string, string)>.Failure("PDF extraction requires the Anthropic client.");
        }

        var invocationId = Guid7Generator.NewId();
        var stopwatch = Stopwatch.StartNew();

        var inputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "summary": { "type": "string", "description": "One line, max 200 chars: what this document is and its key contents." },
                "text": { "type": "string", "description": "The document's full text content, faithfully transcribed. Tables as aligned plain text. No commentary." }
              },
              "required": ["summary", "text"]
            }
            """).RootElement.Clone();

        var request = new AnthropicMessageRequest(
            Model: _anthropicOptions.ModelId,
            MaxTokens: 8192,
            System: "You transcribe documents. Extract the full text content faithfully — no interpretation, no advice, no commentary. Call the tool exactly once.",
            Messages: new[]
            {
                new AnthropicMessage("user", new object[]
                {
                    AnthropicDocumentBlock.Pdf(bytes),
                    AnthropicTextBlock.Of("Transcribe this document."),
                }),
            },
            Tools: new[]
            {
                new AnthropicTool(ExtractionToolName, "Submit the transcribed document.", inputSchema),
            },
            ToolChoice: new AnthropicToolChoice("tool", ExtractionToolName),
            Metadata: new AnthropicMessageMetadata(invocationId.ToString("N")),
            Temperature: 0);

        try
        {
            var response = await _client.SendAsync(request, ct);
            stopwatch.Stop();

            var block = response.Content.FirstOrDefault(
                c => c.Type == "tool_use" && c.Name == ExtractionToolName);
            if (block?.Input is not JsonElement input
                || !input.TryGetProperty("text", out var textEl)
                || textEl.ValueKind != JsonValueKind.String)
            {
                await LogInvocationAsync(invocationId, userId, response, stopwatch,
                    "PDF extraction returned no text.", ct);
                return Result<(string, string)>.Failure("Couldn't read the PDF's contents.");
            }

            var summary = input.TryGetProperty("summary", out var sEl) && sEl.ValueKind == JsonValueKind.String
                ? Truncate(sEl.GetString()!, 300)
                : "PDF document";
            var text = Truncate(textEl.GetString()!, _options.MaxDocumentChars);

            await LogInvocationAsync(invocationId, userId, response, stopwatch, null, ct);
            return Result<(string, string)>.Success((text, summary));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await LogInvocationAsync(invocationId, userId, null, stopwatch, ex.Message, ct);
            _logger.LogWarning(ex, "PDF extraction failed for user {UserId}", userId);
            return Result<(string, string)>.Failure("PDF extraction failed — try again.");
        }
    }

    private async Task LogInvocationAsync(
        Guid invocationId, Guid userId, AnthropicMessageResponse? response,
        Stopwatch stopwatch, string? error, CancellationToken ct)
    {
        _db.AgentInvocations.Add(new AgentInvocation
        {
            Id = invocationId,
            UserId = userId,
            Kind = AgentInvocationKinds.DocumentExtraction,
            // The PDF bytes are NOT logged — only the fact of the extraction.
            InputSnapshot = JsonDocument.Parse("""{"note":"pdf extraction; file bytes not logged"}"""),
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

    public async Task<Result<bool>> DeleteAsync(Guid userId, Guid documentId, CancellationToken ct)
    {
        var deleted = await _db.UserDocuments
            .Where(d => d.Id == documentId && d.UserId == userId)
            .ExecuteDeleteAsync(ct);
        return deleted > 0
            ? Result<bool>.Success(true)
            : Result<bool>.Failure("Nothing deleted.");
    }

    private static string BuildTextSummary(string fileName, string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return Truncate($"{Path.GetFileName(fileName)} — {lines.Length} lines", 300);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];
}

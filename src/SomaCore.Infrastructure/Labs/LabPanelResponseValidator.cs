using System.Text.Json;

using SomaCore.Domain.Common;
using SomaCore.Infrastructure.Agent;

namespace SomaCore.Infrastructure.Labs;

/// <summary>One extracted biomarker awaiting persistence.</summary>
public sealed record BiomarkerDraft(
    string BiomarkerName,
    string DisplayName,
    string Category,
    decimal? NumericValue,
    string? StringValue,
    string? Unit,
    decimal? ReferenceLow,
    decimal? ReferenceHigh,
    string? ReferenceString);

/// <summary>The validated extraction of one Function panel PDF.</summary>
public sealed record LabPanelDraft(
    DateOnly CollectedAt,
    IReadOnlyList<BiomarkerDraft> Biomarkers);

/// <summary>
/// Mechanical guard on the model's lab-extraction tool response. Two
/// layers per the session brief: shape validation, then the taxonomy
/// checksum — any biomarker name outside
/// <see cref="FunctionBiomarkerTaxonomy.KnownNames"/> fails the WHOLE
/// parse for admin review. A panel with one hallucinated marker is not
/// a panel minus one marker; it's an untrusted extraction.
/// </summary>
internal static class LabPanelResponseValidator
{
    public const string ToolName = "submit_lab_panel";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed record PanelPayload(
        string? CollectedAt,
        List<BiomarkerDraft>? Biomarkers);

    public static Result<LabPanelDraft> Validate(AnthropicMessageResponse response, DateOnly today)
    {
        var block = response.Content.FirstOrDefault(
            c => c.Type == "tool_use" && c.Name == ToolName);
        if (block?.Input is not JsonElement input
            || !input.TryGetProperty("panel_json", out var panelEl)
            || panelEl.ValueKind != JsonValueKind.String)
        {
            return Result<LabPanelDraft>.Failure($"Model response contained no {ToolName} panel_json.");
        }

        PanelPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PanelPayload>(panelEl.GetString()!, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Result<LabPanelDraft>.Failure($"panel_json failed to parse: {ex.Message}");
        }

        if (payload?.Biomarkers is not { Count: > 0 } biomarkers)
        {
            return Result<LabPanelDraft>.Failure("No biomarkers extracted.");
        }
        if (biomarkers.Count > 250)
        {
            return Result<LabPanelDraft>.Failure($"Implausible biomarker count ({biomarkers.Count}).");
        }

        if (!DateOnly.TryParse(payload.CollectedAt, out var collectedAt))
        {
            return Result<LabPanelDraft>.Failure("Missing or unparseable collected_at date.");
        }
        if (collectedAt > today || collectedAt < today.AddYears(-5))
        {
            return Result<LabPanelDraft>.Failure($"collected_at {collectedAt:yyyy-MM-dd} is outside the plausible window.");
        }

        var unknown = new List<string>();
        foreach (var b in biomarkers)
        {
            if (string.IsNullOrWhiteSpace(b.BiomarkerName)
                || !FunctionBiomarkerTaxonomy.KnownNames.Contains(b.BiomarkerName))
            {
                unknown.Add(b.BiomarkerName ?? "(null)");
                continue;
            }
            if (string.IsNullOrWhiteSpace(b.DisplayName))
            {
                return Result<LabPanelDraft>.Failure($"{b.BiomarkerName}: display_name missing.");
            }
            if (!FunctionBiomarkerTaxonomy.KnownCategories.Contains(b.Category ?? ""))
            {
                return Result<LabPanelDraft>.Failure($"{b.BiomarkerName}: unknown category '{b.Category}'.");
            }
            if (b.NumericValue is null && string.IsNullOrWhiteSpace(b.StringValue))
            {
                return Result<LabPanelDraft>.Failure($"{b.BiomarkerName}: neither numeric nor string value present.");
            }
            if (b.ReferenceLow is { } lo && b.ReferenceHigh is { } hi && lo > hi)
            {
                return Result<LabPanelDraft>.Failure($"{b.BiomarkerName}: reference_low > reference_high.");
            }
        }

        if (unknown.Count > 0)
        {
            // Taxonomy checksum failure — the whole panel goes to admin
            // review rather than silently dropping the unknowns.
            return Result<LabPanelDraft>.Failure(
                $"Unrecognized biomarker name(s): {string.Join(", ", unknown.Take(10))}. " +
                "Panel held for review — extend FunctionBiomarkerTaxonomy if these are legitimate.");
        }

        var duplicates = biomarkers
            .GroupBy(b => b.BiomarkerName)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            return Result<LabPanelDraft>.Failure(
                $"Duplicate biomarker(s) in one panel: {string.Join(", ", duplicates)}.");
        }

        return Result<LabPanelDraft>.Success(new LabPanelDraft(collectedAt, biomarkers));
    }

    /// <summary>
    /// Server-side flag computation — we don't trust the model's in/out-of-range
    /// judgment when we can derive it from the numbers it extracted.
    /// </summary>
    public static string ComputeFlag(BiomarkerDraft b)
    {
        if (b.NumericValue is not { } v)
        {
            return "unknown";
        }
        if (b.ReferenceLow is { } lo && v < lo)
        {
            return "low";
        }
        if (b.ReferenceHigh is { } hi && v > hi)
        {
            return "high";
        }
        if (b.ReferenceLow is null && b.ReferenceHigh is null)
        {
            return "unknown";
        }
        return "in_range";
    }
}

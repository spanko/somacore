using SomaCore.Domain.Common;
using SomaCore.Domain.Users;

namespace SomaCore.Domain.LabUploads;

/// <summary>
/// One user-uploaded lab results document (Function Health panel in phase 1;
/// <see cref="Source"/> is extensible). The coach NEVER reads biomarkers from
/// an upload the user hasn't confirmed — extraction can be wrong, and the
/// review-and-confirm step is the mechanical safeguard
/// (session-function-health-integration.md §1.2).
/// </summary>
public class LabUpload : IHasTimestamps
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>'function_health' (extensible: quest, labcorp, ...).</summary>
    public string Source { get; set; } = string.Empty;

    public DateTimeOffset UploadedAt { get; set; }

    /// <summary>The panel's collection date, extracted from the PDF.</summary>
    public DateOnly? CollectedAt { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>Phase 1: bytes in Postgres. Move to Blob when volume warrants.</summary>
    public byte[] FileBytes { get; set; } = Array.Empty<byte>();

    public int FileSize { get; set; }

    /// <summary>'parsed' / 'failed' / 'confirmed'. Extraction is synchronous, so there's no pending state at rest.</summary>
    public string ParseStatus { get; set; } = string.Empty;

    /// <summary>Capped + redacted; admin-facing only.</summary>
    public string? ParseError { get; set; }

    public DateTimeOffset? ParsedAt { get; set; }

    /// <summary>The user who clicked Confirm on the review surface.</summary>
    public Guid? ConfirmedBy { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }

    public string? TraceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User? User { get; set; }

    public List<LabBiomarker> Biomarkers { get; set; } = new();
}

/// <summary>
/// One extracted biomarker value. Cascades on both user deletion and
/// upload deletion (re-upload replaces the rows wholesale).
/// </summary>
public class LabBiomarker : IHasTimestamps
{
    public Guid Id { get; set; }

    public Guid LabUploadId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Canonical name from the Function taxonomy, e.g. 'vitamin_d_25_hydroxy'.</summary>
    public string BiomarkerName { get; set; } = string.Empty;

    /// <summary>As printed on the PDF.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>'nutrients' / 'heart' / 'metabolic' / 'hormones' / 'thyroid' / ...</summary>
    public string Category { get; set; } = string.Empty;

    public decimal? NumericValue { get; set; }

    /// <summary>For qualitative results ('Negative', 'Within reference range').</summary>
    public string? StringValue { get; set; }

    public string? Unit { get; set; }

    public decimal? ReferenceLow { get; set; }

    public decimal? ReferenceHigh { get; set; }

    /// <summary>Non-numeric reference descriptions ('&lt; 130', 'Negative').</summary>
    public string? ReferenceString { get; set; }

    public DateOnly CollectedAt { get; set; }

    /// <summary>'in_range' / 'low' / 'high' / 'unknown'. Computed server-side when value + range are numeric.</summary>
    public string Flagged { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public LabUpload? LabUpload { get; set; }

    public User? User { get; set; }
}

namespace SomaCore.Infrastructure.Labs;

/// <summary>
/// Configuration for the lab upload surface (/me/labs). Defaults OFF —
/// gated on Tai's privacy Part 1 / Section F sign-off. Note the parse
/// itself transmits the PDF to Anthropic once (Part 1, F.3).
/// </summary>
public sealed class LabsOptions
{
    public const string SectionName = "Labs";

    public bool Enabled { get; init; }

    /// <summary>Upload cap in bytes (10 MB — Function panels are typically 1-3 MB).</summary>
    public int MaxUploadBytes { get; init; } = 10_485_760;

    /// <summary>Biomarkers carried into the card snapshot (Function panels run 100+; the card doesn't need all).</summary>
    public int SnapshotBiomarkerCap { get; init; } = 60;

    /// <summary>Only biomarkers collected within this window enter the snapshot.</summary>
    public int SnapshotWindowDays { get; init; } = 365;
}

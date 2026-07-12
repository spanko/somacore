namespace SomaCore.Infrastructure.Mfp;

/// <summary>
/// Configuration for the MyFitnessPal integration surfaces
/// (session-myfitnesspal-integration.md). CSV upload defaults OFF — the
/// flag flips at deploy time after Adam's real MFP export passes the
/// acceptance gate (integrations-INBOX pre-seeded note).
/// </summary>
public sealed class MfpOptions
{
    public const string SectionName = "Mfp";

    public bool CsvUploadEnabled { get; init; }

    /// <summary>Upload cap (50 MB — MFP export ZIPs are typically 1-10 MB, brief §1.3).</summary>
    public long MaxUploadBytes { get; init; } = 52_428_800;
}

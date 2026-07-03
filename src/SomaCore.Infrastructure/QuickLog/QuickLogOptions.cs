namespace SomaCore.Infrastructure.QuickLog;

/// <summary>
/// Configuration for the quick-log surface (session-quick-log.md).
///
/// <see cref="Enabled"/> defaults to FALSE and stays false until Tai signs
/// privacy draft Part 4 — the extraction call sends user-typed free text to
/// Anthropic, a surface not covered by the original Section D sign-off.
/// Scaffolding ships before signoff; the network call does not (ADR 0012
/// precedent). Quick-log additionally requires Anthropic:Enabled + ApiKey,
/// since extraction rides the same client as the daily card.
/// </summary>
public sealed class QuickLogOptions
{
    public const string SectionName = "QuickLog";

    /// <summary>Master switch for the quick-log surface. OFF until Tai signs privacy Part 4.</summary>
    public bool Enabled { get; init; }

    /// <summary>Extraction invocations per user per UTC day. Generous for a three-user alpha.</summary>
    public int DailyCap { get; init; } = 20;

    /// <summary>Max characters accepted in the input box.</summary>
    public int MaxInputChars { get; init; } = 500;
}

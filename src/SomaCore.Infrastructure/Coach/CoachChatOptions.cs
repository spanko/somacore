namespace SomaCore.Infrastructure.Coach;

/// <summary>
/// Configuration for the coach conversation + document surfaces
/// (/me/coach). Defaults OFF: both send new categories of user content to
/// Anthropic (document contents at extraction; free-form conversation
/// turns) that need their own privacy-draft part. Adam enabled in dev
/// 2026-07-05 for the internal alpha; the privacy write-up (Part 5) is
/// owed to Tai regardless.
/// </summary>
public sealed class CoachChatOptions
{
    public const string SectionName = "CoachChat";

    public bool Enabled { get; init; }

    /// <summary>User turns allowed per thread — a conversation, not a chatbot.</summary>
    public int MaxUserTurnsPerThread { get; init; } = 10;

    /// <summary>Coach messages per user per UTC day across all threads.</summary>
    public int DailyMessageCap { get; init; } = 40;

    /// <summary>Max characters of a user chat turn.</summary>
    public int MaxTurnChars { get; init; } = 1000;

    /// <summary>Max characters of extracted document text carried into context.</summary>
    public int MaxDocumentChars { get; init; } = 50_000;

    /// <summary>Upload cap in bytes (10 MB — matches the DB check constraint).</summary>
    public int MaxDocumentBytes { get; init; } = 10_485_760;
}

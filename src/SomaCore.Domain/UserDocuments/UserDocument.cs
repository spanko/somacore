using SomaCore.Domain.Common;
using SomaCore.Domain.Users;

namespace SomaCore.Domain.UserDocuments;

/// <summary>
/// A document the user handed the coach — nutrition export, training plan,
/// lab summary, anything the coach should be able to discuss. File bytes
/// stay in Postgres (same phase-1 posture as the Function Health plan);
/// <see cref="ExtractedText"/> is the conversation-ready content pulled at
/// upload time. The raw file is never forwarded to Anthropic after the
/// one-time extraction pass.
/// </summary>
public class UserDocument : IHasTimestamps
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public byte[] FileBytes { get; set; } = Array.Empty<byte>();

    public int FileSize { get; set; }

    /// <summary>'parsed' / 'failed'. No pending state — extraction is synchronous at upload.</summary>
    public string ParseStatus { get; set; } = string.Empty;

    public string? ParseError { get; set; }

    /// <summary>One-line description for the evidence chip ("Week 3 training plan — 5 sessions").</summary>
    public string? Summary { get; set; }

    /// <summary>
    /// Conversation-ready plain text. Direct read for text formats; Anthropic
    /// single-pass extraction for PDFs. Capped server-side.
    /// </summary>
    public string? ExtractedText { get; set; }

    public DateTimeOffset UploadedAt { get; set; }

    public string? TraceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User? User { get; set; }
}

using SomaCore.Domain.Common;
using SomaCore.Domain.Users;

namespace SomaCore.Domain.UserNotes;

/// <summary>
/// A user-provided piece of context the coach can't see in any data feed —
/// "knee pain", "traveling until Friday". This is deliberately VISIBLE
/// memory: every active note renders on /me with a delete button, and
/// deleting one removes it from all future input snapshots. Nothing is
/// silently remembered (privacy draft Part 4c).
/// </summary>
public class UserNote : IHasTimestamps
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>'quick_log' now; 'conversation' when Phase 2 ships.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>'symptom' / 'schedule' / 'context' / null when unclassified.</summary>
    public string? Category { get; set; }

    /// <summary>The note in the user's words, as they confirmed it.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>
    /// Null = active until deleted. A date = the note self-expires from the
    /// input snapshot after this day ("traveling until Friday").
    /// </summary>
    public DateOnly? ActiveUntil { get; set; }

    public string? TraceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User? User { get; set; }
}

/// <summary>Allowed values for <see cref="UserNote.Category"/>.</summary>
public static class UserNoteCategory
{
    public const string Symptom = "symptom";
    public const string Schedule = "schedule";
    public const string Context = "context";

    public static readonly IReadOnlyList<string> All =
        new[] { Symptom, Schedule, Context };
}

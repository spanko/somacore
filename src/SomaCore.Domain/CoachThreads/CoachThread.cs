using SomaCore.Domain.Common;
using SomaCore.Domain.Users;

namespace SomaCore.Domain.CoachThreads;

/// <summary>
/// One conversation with the coach, anchored to a subject: a document, a
/// logged meal/workout/note, or nothing ("general"). The subject is what
/// the conversation is ABOUT — its content rides in every model call's
/// context and its chip renders pinned at the top of the thread.
/// </summary>
public class CoachThread : IHasTimestamps
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>'document' / 'meal' / 'workout' / 'note' / 'general'.</summary>
    public string SubjectType { get; set; } = string.Empty;

    /// <summary>Row id in the subject's table; null for 'general'.</summary>
    public Guid? SubjectId { get; set; }

    /// <summary>Chip-style title shown in the thread list ("MEAL Jul 5 lunch — 50g protein").</summary>
    public string Title { get; set; } = string.Empty;

    public DateTimeOffset LastMessageAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User? User { get; set; }

    public List<CoachMessage> Messages { get; set; } = new();
}

/// <summary>One turn in a coach thread.</summary>
public class CoachMessage : IHasTimestamps
{
    public Guid Id { get; set; }

    public Guid ThreadId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>'user' / 'coach'.</summary>
    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    /// <summary>True when this coach turn was a bounds refusal.</summary>
    public bool Refusal { get; set; }

    /// <summary>Links coach turns to their agent_invocations row (cost/audit).</summary>
    public Guid? InvocationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public CoachThread? Thread { get; set; }

    public User? User { get; set; }
}

public static class CoachThreadSubjectType
{
    public const string Document = "document";
    public const string Meal = "meal";
    public const string Workout = "workout";
    public const string Note = "note";
    public const string General = "general";

    public static readonly IReadOnlyList<string> All =
        new[] { Document, Meal, Workout, Note, General };
}

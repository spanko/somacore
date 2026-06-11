namespace SomaCore.Domain.Agent;

/// <summary>
/// One ranked recommendation the agent surfaces on the /me daily card.
/// Persisted inside <see cref="AgentInvocation.ActionsJson"/>.
///
/// Categories are bounded by what Tai approves in the in-bounds list — the
/// model can only return a category from that list, enforced as a hard
/// refusal guard on the response, not as a polite suggestion.
/// </summary>
public sealed record AgentAction(
    /// <summary>One-line action, in the agent's voice, e.g. "Drink 24 oz of water before your first meeting."</summary>
    string Title,
    /// <summary>Why this, in 1-2 sentences. Renders behind a click on the card.</summary>
    string Why,
    /// <summary>Bucket like "hydration", "caffeine_timing", "workout_intensity". Bounded by the approved list.</summary>
    string Category,
    /// <summary>1 = most important. We render in this order. Ties allowed.</summary>
    int Rank);

public static class AgentActionCategory
{
    public const string Hydration         = "hydration";
    public const string CaffeineTiming    = "caffeine_timing";
    public const string WorkoutIntensity  = "workout_intensity";
    public const string SleepTiming       = "sleep_timing";
    public const string Stress            = "stress";
    public const string MealTiming        = "meal_timing";

    /// <summary>
    /// The complete in-bounds list. The agent response is rejected if it
    /// emits a category outside this set. Tai's bounds doc is the source of
    /// truth for what belongs here.
    /// </summary>
    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        Hydration,
        CaffeineTiming,
        WorkoutIntensity,
        SleepTiming,
        Stress,
        MealTiming,
    };
}

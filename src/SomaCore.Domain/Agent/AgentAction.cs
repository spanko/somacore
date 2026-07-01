namespace SomaCore.Domain.Agent;

/// <summary>
/// One ranked recommendation the agent surfaces on the /me daily card.
/// Persisted inside <see cref="AgentInvocation.ActionsJson"/>.
///
/// Categories are bounded by what Tai approved in <c>docs/agent-bounds.md</c>.
/// The agent's response is rejected server-side if it emits a category
/// outside <see cref="AgentActionCategory.All"/>. Enforced as a hard refusal
/// guard, not a polite suggestion to the model.
/// </summary>
public sealed record AgentAction(
    /// <summary>One-line action, in the agent's voice, e.g. "Drink 24 oz of water before your first meeting."</summary>
    string Title,
    /// <summary>Why this, in 1-2 sentences. Renders behind a click on the card.</summary>
    string Why,
    /// <summary>Bucket from <see cref="AgentActionCategory.All"/>. Bounded by the approved list.</summary>
    string Category,
    /// <summary>1 = most important. We render in this order. Ties allowed.</summary>
    int Rank,
    /// <summary>
    /// Provenance per the voice doc: where the recommendation comes from.
    /// One of <see cref="AgentActionSource.ProtocolBased"/>,
    /// <see cref="AgentActionSource.UserDataInformed"/>, or a reference to a
    /// user-uploaded lab document. Null acceptable on the stub path; required
    /// for any live model output (the validator enforces).
    /// </summary>
    string? Source = null);

/// <summary>
/// The complete IN BOUNDS category list from <c>docs/agent-bounds.md</c>.
/// Mirrors Tai's authored list exactly — when the doc changes, this set
/// changes to match. The agent response validator rejects any category
/// outside this set.
/// </summary>
public static class AgentActionCategory
{
    /// <summary>Training type and intensity (zone 2 vs heavy lift vs HIIT, etc.).</summary>
    public const string TrainingIntensity = "training_intensity";
    /// <summary>Workout duration and structure (e.g. "35 min easy + 5 strides").</summary>
    public const string WorkoutStructure = "workout_structure";
    /// <summary>Fueling and meal timing — pre/post-workout windows, fasting windows.</summary>
    public const string MealTiming = "meal_timing";
    /// <summary>Macro targets — grams of protein, carbs, fat for the day or a specific window.</summary>
    public const string Macros = "macros";
    /// <summary>Hydration — volume + electrolyte adjustments.</summary>
    public const string Hydration = "hydration";
    /// <summary>Caffeine timing — when to take it, when to cut it off.</summary>
    public const string CaffeineTiming = "caffeine_timing";
    /// <summary>Sleep timing and pressure — bedtime, wake target, nap windows.</summary>
    public const string SleepTiming = "sleep_timing";
    /// <summary>Recovery protocols — sauna, cold, light movement, breath work.</summary>
    public const string RecoveryProtocols = "recovery_protocols";
    /// <summary>Stress and readiness signals — pacing the day around HRV/RHR signal.</summary>
    public const string Stress = "stress";
    /// <summary>Symptom-informed plan adjustments — modifying the plan when subjective symptoms diverge from the data.</summary>
    public const string SymptomAdjustment = "symptom_adjustment";
    /// <summary>
    /// Supplement reminders and food guidance sourced directly from a
    /// user-uploaded lab result. Requires a non-null <see cref="AgentAction.Source"/>
    /// referencing the lab document. Validator enforces.
    /// </summary>
    public const string SupplementsFromLabs = "supplements_from_labs";

    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        TrainingIntensity,
        WorkoutStructure,
        MealTiming,
        Macros,
        Hydration,
        CaffeineTiming,
        SleepTiming,
        RecoveryProtocols,
        Stress,
        SymptomAdjustment,
        SupplementsFromLabs,
    };
}

/// <summary>
/// Provenance tags per <c>docs/agent-voice-and-persona.md</c>: every
/// recommendation must be traceable to one of these sources.
/// </summary>
public static class AgentActionSource
{
    /// <summary>Generic best-practice protocol (zone 2 on low recovery, etc.).</summary>
    public const string ProtocolBased = "protocol_based";
    /// <summary>Tailored to this user's WHOOP signal (their baseline, their trend).</summary>
    public const string UserDataInformed = "user_data_informed";
    // User-uploaded lab references look like "lab:<document-id>" or similar
    // when the lab ingestion surface lands. Until then, ProtocolBased and
    // UserDataInformed are the only two values the validator will accept.
}

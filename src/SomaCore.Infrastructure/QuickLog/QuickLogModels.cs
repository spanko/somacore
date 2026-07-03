namespace SomaCore.Infrastructure.QuickLog;

/// <summary>
/// The structured result of extracting one user-typed quick-log line.
/// Exactly one of <see cref="Meal"/> / <see cref="Workout"/> / <see cref="Note"/>
/// is populated when <see cref="EntryType"/> names it; all are null for
/// 'unclassified', in which case <see cref="Message"/> carries the model's
/// clarification for the user.
/// </summary>
public sealed record QuickLogExtraction(
    string EntryType,
    MealDraft? Meal,
    WorkoutDraft? Workout,
    NoteDraft? Note,
    string? Message);

public static class QuickLogEntryType
{
    public const string Meal = "meal";
    public const string Workout = "workout";
    public const string Note = "note";
    public const string Unclassified = "unclassified";

    public static readonly IReadOnlyList<string> All =
        new[] { Meal, Workout, Note, Unclassified };
}

public sealed record MealDraft(
    string MealSlot,
    DateOnly MealDate,
    decimal? Calories,
    decimal? ProteinG,
    decimal? CarbsG,
    decimal? FatG,
    decimal? FiberG,
    decimal? SugarG,
    decimal? SodiumMg,
    IReadOnlyList<FoodItemDraft> FoodItems);

public sealed record FoodItemDraft(string Name, string? Amount);

public sealed record WorkoutDraft(
    string WorkoutType,
    DateTimeOffset StartedAt,
    int ElapsedSeconds,
    string? Intensity,
    decimal? TotalEnergyKcal,
    decimal? TotalDistanceM,
    int? AverageHr);

public sealed record NoteDraft(
    string? Category,
    string Note,
    DateOnly? ActiveUntil);

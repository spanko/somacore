using System.Text.Json;

using SomaCore.Domain.Common;
using SomaCore.Domain.FoodEntries;
using SomaCore.Domain.UserNotes;
using SomaCore.Infrastructure.Agent;

namespace SomaCore.Infrastructure.QuickLog;

/// <summary>
/// Parses + validates the model's tool response for a quick-log extraction.
/// Same philosophy as <see cref="AgentResponseValidator"/>: the model is
/// told the shape in the system prompt; this is the mechanical guard that
/// rejects drift. Also validates user-edited confirm payloads — the same
/// range rules apply whether a value came from the model or the user's
/// edit on the confirm card.
/// </summary>
internal static class QuickLogExtractionValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public const string ToolName = "submit_quick_log_entry";

    public static Result<QuickLogExtraction> Validate(
        AnthropicMessageResponse response,
        DateOnly localToday)
    {
        var toolBlock = response.Content.FirstOrDefault(
            c => c.Type == "tool_use" && c.Name == ToolName);
        if (toolBlock?.Input is not JsonElement input)
        {
            return Result<QuickLogExtraction>.Failure(
                $"Model response contained no {ToolName} tool call.");
        }

        if (!input.TryGetProperty("entry_json", out var entryJsonEl)
            || entryJsonEl.ValueKind != JsonValueKind.String)
        {
            return Result<QuickLogExtraction>.Failure(
                "Tool input missing entry_json string field.");
        }

        QuickLogExtraction? extraction;
        try
        {
            extraction = JsonSerializer.Deserialize<QuickLogExtraction>(
                entryJsonEl.GetString()!, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Result<QuickLogExtraction>.Failure(
                $"entry_json failed to parse: {ex.Message}");
        }

        if (extraction is null)
        {
            return Result<QuickLogExtraction>.Failure("entry_json parsed to null.");
        }

        return ValidateShape(extraction, localToday);
    }

    public static Result<QuickLogExtraction> ValidateShape(
        QuickLogExtraction extraction,
        DateOnly localToday)
    {
        // The model may omit food_items entirely; the record deserializes
        // with a null list. Normalize before validation so downstream code
        // never sees null.
        if (extraction.Meal is { FoodItems: null } bareMeal)
        {
            extraction = extraction with
            {
                Meal = bareMeal with { FoodItems = Array.Empty<FoodItemDraft>() },
            };
        }

        if (!QuickLogEntryType.All.Contains(extraction.EntryType))
        {
            return Result<QuickLogExtraction>.Failure(
                $"Unknown entry_type '{extraction.EntryType}'.");
        }

        switch (extraction.EntryType)
        {
            case QuickLogEntryType.Meal when extraction.Meal is null:
            case QuickLogEntryType.Workout when extraction.Workout is null:
            case QuickLogEntryType.Note when extraction.Note is null:
                return Result<QuickLogExtraction>.Failure(
                    $"entry_type '{extraction.EntryType}' without a matching payload.");
        }

        if (extraction.Meal is { } meal)
        {
            var mealCheck = ValidateMeal(meal, localToday);
            if (!mealCheck.IsSuccess)
            {
                return Result<QuickLogExtraction>.Failure(mealCheck.Error!);
            }
        }
        if (extraction.Workout is { } workout)
        {
            var workoutCheck = ValidateWorkout(workout, localToday);
            if (!workoutCheck.IsSuccess)
            {
                return Result<QuickLogExtraction>.Failure(workoutCheck.Error!);
            }
        }
        if (extraction.Note is { } note)
        {
            var noteCheck = ValidateNote(note, localToday);
            if (!noteCheck.IsSuccess)
            {
                return Result<QuickLogExtraction>.Failure(noteCheck.Error!);
            }
        }

        return Result<QuickLogExtraction>.Success(extraction);
    }

    public static Result<bool> ValidateMeal(MealDraft meal, DateOnly localToday)
    {
        if (!MealSlot.All.Contains(meal.MealSlot))
        {
            return Result<bool>.Failure($"Unknown meal_slot '{meal.MealSlot}'.");
        }
        if (meal.MealDate < localToday.AddDays(-7) || meal.MealDate > localToday.AddDays(1))
        {
            return Result<bool>.Failure(
                "meal_date must be within the last 7 days (quick-log is for recent meals; bulk history arrives via data export).");
        }
        if (IsOutOfRange(meal.Calories, 0, 20000)
            || IsOutOfRange(meal.ProteinG, 0, 1000)
            || IsOutOfRange(meal.CarbsG, 0, 2000)
            || IsOutOfRange(meal.FatG, 0, 1000)
            || IsOutOfRange(meal.FiberG, 0, 500)
            || IsOutOfRange(meal.SugarG, 0, 2000)
            || IsOutOfRange(meal.SodiumMg, 0, 50000))
        {
            return Result<bool>.Failure("A meal nutrient value is outside its plausible range.");
        }
        if (meal.FoodItems.Count > 30 || meal.FoodItems.Any(i => string.IsNullOrWhiteSpace(i.Name)))
        {
            return Result<bool>.Failure("food_items must have non-empty names (max 30 items).");
        }
        return Result<bool>.Success(true);
    }

    public static Result<bool> ValidateWorkout(WorkoutDraft workout, DateOnly localToday)
    {
        if (string.IsNullOrWhiteSpace(workout.WorkoutType) || workout.WorkoutType.Length > 60)
        {
            return Result<bool>.Failure("workout_type must be a short non-empty string.");
        }
        if (workout.ElapsedSeconds is < 60 or > 86_400)
        {
            return Result<bool>.Failure("elapsed_seconds must be between 1 minute and 24 hours.");
        }
        var startedDate = DateOnly.FromDateTime(workout.StartedAt.UtcDateTime);
        if (startedDate < localToday.AddDays(-7) || startedDate > localToday.AddDays(1))
        {
            return Result<bool>.Failure("started_at must be within the last 7 days.");
        }
        if (workout.AverageHr is < 0 or > 300)
        {
            return Result<bool>.Failure("average_hr out of range.");
        }
        if (IsOutOfRange(workout.TotalEnergyKcal, 0, 20000)
            || IsOutOfRange(workout.TotalDistanceM, 0, 1_000_000))
        {
            return Result<bool>.Failure("A workout value is outside its plausible range.");
        }
        return Result<bool>.Success(true);
    }

    public static Result<bool> ValidateNote(NoteDraft note, DateOnly localToday)
    {
        if (string.IsNullOrWhiteSpace(note.Note) || note.Note.Length > 2000)
        {
            return Result<bool>.Failure("note must be non-empty and at most 2000 characters.");
        }
        if (note.Category is not null && !UserNoteCategory.All.Contains(note.Category))
        {
            return Result<bool>.Failure($"Unknown note category '{note.Category}'.");
        }
        if (note.ActiveUntil is { } until
            && (until < localToday || until > localToday.AddDays(366)))
        {
            return Result<bool>.Failure("active_until must be between today and one year out.");
        }
        return Result<bool>.Success(true);
    }

    private static bool IsOutOfRange(decimal? value, decimal min, decimal max)
        => value is { } v && (v < min || v > max);
}

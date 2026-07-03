using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using SomaCore.Domain.Common;
using SomaCore.Domain.FoodEntries;
using SomaCore.Domain.HealthKitWorkouts;
using SomaCore.Domain.UserNotes;
using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Infrastructure.QuickLog;

public interface IQuickLogEntryService
{
    /// <summary>Persists a user-CONFIRMED draft. Re-validates ranges (the user can edit values on the confirm card).</summary>
    Task<Result<string>> ConfirmAsync(
        Guid userId, QuickLogExtraction draft, string? traceId, CancellationToken ct);

    Task<IReadOnlyList<LoggedItem>> GetLoggedItemsAsync(Guid userId, CancellationToken ct);

    Task<Result<bool>> DeleteAsync(Guid userId, string itemType, Guid itemId, CancellationToken ct);
}

/// <summary>One row in the "Logged by you" list on /me.</summary>
public sealed record LoggedItem(
    string ItemType,          // 'meal' / 'workout' / 'note'
    Guid Id,
    string Summary,
    DateTimeOffset LoggedAt);

/// <summary>
/// Persistence for confirmed quick-log drafts. Nothing in here talks to
/// Anthropic — by the time a draft reaches this service the user has seen
/// it and clicked Confirm (the no-autonomous-action commitment is enforced
/// by this separation, not by convention).
/// </summary>
public sealed class QuickLogEntryService : IQuickLogEntryService
{
    private const string ManualSource = "manual";
    private const string QuickLogVia = "quick_log";

    private readonly SomaCoreDbContext _db;
    private readonly ILogger<QuickLogEntryService> _logger;

    public QuickLogEntryService(SomaCoreDbContext db, ILogger<QuickLogEntryService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<string>> ConfirmAsync(
        Guid userId, QuickLogExtraction draft, string? traceId, CancellationToken ct)
    {
        var localToday = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var shape = QuickLogExtractionValidator.ValidateShape(draft, localToday);
        if (!shape.IsSuccess)
        {
            return Result<string>.Failure(shape.Error!);
        }

        switch (draft.EntryType)
        {
            case QuickLogEntryType.Meal:
                await UpsertMealAsync(userId, draft.Meal!, traceId, ct);
                return Result<string>.Success("Meal logged.");

            case QuickLogEntryType.Workout:
                await InsertWorkoutAsync(userId, draft.Workout!, traceId, ct);
                return Result<string>.Success("Workout logged.");

            case QuickLogEntryType.Note:
                await InsertNoteAsync(userId, draft.Note!, traceId, ct);
                return Result<string>.Success("Noted — the coach will see it.");

            default:
                return Result<string>.Failure("Nothing to confirm for an unclassified entry.");
        }
    }

    /// <summary>
    /// A food entry row is a per-slot rollup: logging a second item into an
    /// existing (user, date, slot, manual) row merges — nullable macros sum
    /// where both sides have values, fill where only one does; food items
    /// append. This is what makes "also had a cookie at lunch" work.
    /// </summary>
    private async Task UpsertMealAsync(Guid userId, MealDraft meal, string? traceId, CancellationToken ct)
    {
        var existing = await _db.FoodEntries
            .FirstOrDefaultAsync(f => f.UserId == userId
                                   && f.MealDate == meal.MealDate
                                   && f.MealSlot == meal.MealSlot
                                   && f.Source == ManualSource, ct);

        var itemsJson = JsonSerializer.Serialize(
            meal.FoodItems.Select(i => new { name = i.Name, amount = i.Amount }));

        if (existing is null)
        {
            _db.FoodEntries.Add(new FoodEntry
            {
                UserId = userId,
                Source = ManualSource,
                MealDate = meal.MealDate,
                MealSlot = meal.MealSlot,
                LoggedAt = DateTimeOffset.UtcNow,
                Calories = meal.Calories,
                ProteinG = meal.ProteinG,
                CarbsG = meal.CarbsG,
                FatG = meal.FatG,
                FiberG = meal.FiberG,
                SugarG = meal.SugarG,
                SodiumMg = meal.SodiumMg,
                FoodItems = JsonDocument.Parse(itemsJson),
                IngestedVia = QuickLogVia,
                IngestedAt = DateTimeOffset.UtcNow,
                TraceId = traceId,
            });
        }
        else
        {
            existing.Calories = AddNullable(existing.Calories, meal.Calories);
            existing.ProteinG = AddNullable(existing.ProteinG, meal.ProteinG);
            existing.CarbsG = AddNullable(existing.CarbsG, meal.CarbsG);
            existing.FatG = AddNullable(existing.FatG, meal.FatG);
            existing.FiberG = AddNullable(existing.FiberG, meal.FiberG);
            existing.SugarG = AddNullable(existing.SugarG, meal.SugarG);
            existing.SodiumMg = AddNullable(existing.SodiumMg, meal.SodiumMg);
            existing.LoggedAt = DateTimeOffset.UtcNow;
            existing.TraceId = traceId;

            var merged = existing.FoodItems.RootElement
                .EnumerateArray()
                .Select(e => e.Clone())
                .ToList();
            using var incoming = JsonDocument.Parse(itemsJson);
            merged.AddRange(incoming.RootElement.EnumerateArray().Select(e => e.Clone()));
            existing.FoodItems = JsonDocument.Parse(
                JsonSerializer.Serialize(merged));
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Quick-log meal confirmed for user {UserId}: {MealDate} {MealSlot} (merged={Merged})",
            userId, meal.MealDate, meal.MealSlot, existing is not null);
    }

    private async Task InsertWorkoutAsync(Guid userId, WorkoutDraft workout, string? traceId, CancellationToken ct)
    {
        var metadata = JsonSerializer.Serialize(new { intensity = workout.Intensity });
        _db.HealthKitWorkouts.Add(new HealthKitWorkout
        {
            UserId = userId,
            SourceBundleId = ManualSource,
            // Manual rows have no HealthKit sample; a fresh Guid keeps the
            // uniqueness index meaningful (one row per confirmed log).
            HkSampleUuid = Guid.NewGuid(),
            WorkoutType = workout.WorkoutType,
            StartedAt = workout.StartedAt,
            ElapsedSeconds = workout.ElapsedSeconds,
            TotalEnergyKcal = workout.TotalEnergyKcal,
            TotalDistanceM = workout.TotalDistanceM,
            AverageHr = workout.AverageHr,
            HkMetadata = JsonDocument.Parse(metadata),
            IngestedAt = DateTimeOffset.UtcNow,
            TraceId = traceId,
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Quick-log workout confirmed for user {UserId}: {WorkoutType} {ElapsedSeconds}s",
            userId, workout.WorkoutType, workout.ElapsedSeconds);
    }

    private async Task InsertNoteAsync(Guid userId, NoteDraft note, string? traceId, CancellationToken ct)
    {
        _db.UserNotes.Add(new UserNote
        {
            UserId = userId,
            Source = QuickLogVia,
            Category = note.Category,
            Note = note.Note,
            ActiveUntil = note.ActiveUntil,
            TraceId = traceId,
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Quick-log note confirmed for user {UserId} (category={Category})",
            userId, note.Category ?? "none");
    }

    public async Task<IReadOnlyList<LoggedItem>> GetLoggedItemsAsync(Guid userId, CancellationToken ct)
    {
        var since = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddDays(-7);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        var meals = await _db.FoodEntries
            .AsNoTracking()
            .Where(f => f.UserId == userId && f.Source == "manual" && f.MealDate >= since)
            .OrderByDescending(f => f.MealDate)
            .Select(f => new { f.Id, f.MealDate, f.MealSlot, f.ProteinG, f.Calories, f.LoggedAt, f.IngestedAt })
            .ToListAsync(ct);

        var workouts = await _db.HealthKitWorkouts
            .AsNoTracking()
            .Where(w => w.UserId == userId && w.SourceBundleId == "manual"
                     && w.StartedAt >= DateTimeOffset.UtcNow.AddDays(-7))
            .OrderByDescending(w => w.StartedAt)
            .Select(w => new { w.Id, w.WorkoutType, w.ElapsedSeconds, w.StartedAt })
            .ToListAsync(ct);

        var notes = await _db.UserNotes
            .AsNoTracking()
            .Where(n => n.UserId == userId
                     && (n.ActiveUntil == null || n.ActiveUntil >= today))
            .OrderByDescending(n => n.CreatedAt)
            .Take(10)
            .Select(n => new { n.Id, n.Note, n.Category, n.ActiveUntil, n.CreatedAt })
            .ToListAsync(ct);

        var items = new List<LoggedItem>();
        items.AddRange(meals.Select(m => new LoggedItem(
            "meal", m.Id,
            $"{m.MealDate:MMM d} {m.MealSlot}"
                + (m.ProteinG is { } p ? $" — {p:0}g protein" : "")
                + (m.Calories is { } c ? $", {c:0} cal" : ""),
            m.LoggedAt ?? m.IngestedAt)));
        items.AddRange(workouts.Select(w => new LoggedItem(
            "workout", w.Id,
            $"{w.StartedAt:MMM d} {w.WorkoutType} — {w.ElapsedSeconds / 60} min",
            w.StartedAt)));
        items.AddRange(notes.Select(n => new LoggedItem(
            "note", n.Id,
            n.Note + (n.ActiveUntil is { } u ? $" (until {u:MMM d})" : ""),
            n.CreatedAt)));

        return items.OrderByDescending(i => i.LoggedAt).ToList();
    }

    public async Task<Result<bool>> DeleteAsync(
        Guid userId, string itemType, Guid itemId, CancellationToken ct)
    {
        // Ownership is enforced in every WHERE — a user can only delete
        // their own rows, whatever id arrives in the POST.
        var deleted = itemType switch
        {
            "meal" => await _db.FoodEntries
                .Where(f => f.Id == itemId && f.UserId == userId && f.Source == "manual")
                .ExecuteDeleteAsync(ct),
            "workout" => await _db.HealthKitWorkouts
                .Where(w => w.Id == itemId && w.UserId == userId && w.SourceBundleId == "manual")
                .ExecuteDeleteAsync(ct),
            "note" => await _db.UserNotes
                .Where(n => n.Id == itemId && n.UserId == userId)
                .ExecuteDeleteAsync(ct),
            _ => 0,
        };

        return deleted > 0
            ? Result<bool>.Success(true)
            : Result<bool>.Failure("Nothing deleted.");
    }

    private static decimal? AddNullable(decimal? a, decimal? b)
        => (a, b) switch
        {
            (null, null) => null,
            (null, _) => b,
            (_, null) => a,
            _ => a + b,
        };
}

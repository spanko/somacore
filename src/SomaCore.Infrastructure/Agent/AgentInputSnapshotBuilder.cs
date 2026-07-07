using System.Globalization;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using SomaCore.Infrastructure.Persistence;

namespace SomaCore.Infrastructure.Agent;

/// <summary>
/// Builds the input snapshot sent to the agent — per privacy doc D.1:
/// last 7 days of recovery + sleep, last 14 days of workouts, plus the
/// user's local timezone and current local time-of-day. NO identifiers,
/// names, emails, tokens, or raw payloads (per D.2).
/// </summary>
public sealed record AgentInputSnapshot(string Json);

internal static class AgentInputSnapshotBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<AgentInputSnapshot> BuildAsync(
        SomaCoreDbContext db,
        Guid userId,
        DateTimeOffset asOfUtc,
        CancellationToken ct)
    {
        var since7d = asOfUtc.AddDays(-7);
        var since14d = asOfUtc.AddDays(-14);

        // Pull more rows than we display because we dedupe in memory the
        // same way /me does — same connection-scoped uniqueness pattern.
        var recoveryRows = await db.WhoopRecoveries
            .AsNoTracking()
            .Where(r => r.UserId == userId && r.CycleStartAt >= since7d)
            .OrderByDescending(r => r.CycleStartAt)
            .ThenByDescending(r => r.IngestedAt)
            .Take(28) // 7 cycles × up to 4 dup-rows seen in production
            .Select(r => new
            {
                r.WhoopCycleId,
                r.CycleStartAt,
                r.ScoreState,
                r.RecoveryScore,
                r.HrvRmssdMilli,
                r.RestingHeartRate,
                r.IngestedAt,
            })
            .ToListAsync(ct);

        var recoveries = recoveryRows
            .GroupBy(r => r.WhoopCycleId)
            .Select(g => g.OrderByDescending(r => r.IngestedAt).First())
            .OrderByDescending(r => r.CycleStartAt)
            .Take(7)
            .Select(r => new RecoverySnapshot(
                CycleStart: r.CycleStartAt,
                ScoreState: r.ScoreState,
                RecoveryScore: r.RecoveryScore,
                HrvMs: r.HrvRmssdMilli,
                RhrBpm: r.RestingHeartRate))
            .ToList();

        var sleepRows = await db.WhoopSleeps
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.EndAt >= since7d && !s.Nap)
            .OrderByDescending(s => s.EndAt)
            .ThenByDescending(s => s.IngestedAt)
            .Take(28)
            .Select(s => new
            {
                s.WhoopSleepId,
                s.StartAt,
                s.EndAt,
                s.ScoreState,
                s.TimezoneOffset,
                s.SleepPerformancePercentage,
                s.SleepEfficiencyPercentage,
                s.TotalSleepTimeMilli,
                s.IngestedAt,
            })
            .ToListAsync(ct);

        var sleeps = sleepRows
            .GroupBy(s => s.WhoopSleepId)
            .Select(g => g.OrderByDescending(s => s.IngestedAt).First())
            .OrderByDescending(s => s.EndAt)
            .Take(7)
            .Select(s => new SleepSnapshot(
                Start: s.StartAt,
                End: s.EndAt,
                ScoreState: s.ScoreState,
                PerformancePct: s.SleepPerformancePercentage,
                EfficiencyPct: s.SleepEfficiencyPercentage,
                TotalSleepMin: s.TotalSleepTimeMilli is null
                    ? (int?)null
                    : (int)(s.TotalSleepTimeMilli.Value / 60000)))
            .ToList();

        var localTzOffset = sleeps.Count > 0
            ? sleepRows.OrderByDescending(s => s.EndAt).First().TimezoneOffset
            : "+00:00";

        var workoutRows = await db.WhoopWorkouts
            .AsNoTracking()
            .Where(w => w.UserId == userId && w.StartAt >= since14d)
            .OrderByDescending(w => w.StartAt)
            .ThenByDescending(w => w.IngestedAt)
            .Take(40)
            .Select(w => new
            {
                w.WhoopWorkoutId,
                w.StartAt,
                w.EndAt,
                w.SportName,
                w.ScoreState,
                w.Strain,
                w.AverageHeartRate,
                w.IngestedAt,
            })
            .ToListAsync(ct);

        var workouts = workoutRows
            .GroupBy(w => w.WhoopWorkoutId)
            .Select(g => g.OrderByDescending(w => w.IngestedAt).First())
            .OrderByDescending(w => w.StartAt)
            .Take(10)
            .Select(w => new WorkoutSnapshot(
                Start: w.StartAt,
                End: w.EndAt,
                SportName: w.SportName,
                ScoreState: w.ScoreState,
                Strain: w.Strain,
                AvgHr: w.AverageHeartRate,
                Source: "whoop"))
            .ToList();

        // Manually logged workouts (quick-log) join the same section, with
        // source provenance so the coach can hedge honestly ("based on what
        // you logged"). iOS-companion rows land here too when the Strava
        // session ships.
        var manualWorkouts = await db.HealthKitWorkouts
            .AsNoTracking()
            .Where(w => w.UserId == userId && w.StartedAt >= since14d)
            .OrderByDescending(w => w.StartedAt)
            .Take(10)
            .Select(w => new
            {
                w.StartedAt,
                w.ElapsedSeconds,
                w.WorkoutType,
                w.AverageHr,
                w.SourceBundleId,
            })
            .ToListAsync(ct);
        workouts = workouts
            .Concat(manualWorkouts.Select(w => new WorkoutSnapshot(
                Start: w.StartedAt,
                End: w.StartedAt.AddSeconds(w.ElapsedSeconds),
                SportName: w.WorkoutType,
                ScoreState: "SCORED",
                Strain: null,
                AvgHr: w.AverageHr,
                Source: w.SourceBundleId == "manual" ? "manual" : "healthkit")))
            .OrderByDescending(w => w.Start)
            .Take(14)
            .ToList();

        // Local time-of-day computed from the most recent sleep's TZ offset.
        var localOffset = ParseOffset(localTzOffset);
        var localNow = asOfUtc.ToOffset(localOffset);
        var localTimeOfDay = localNow.ToString("HH:mm", CultureInfo.InvariantCulture);

        var localToday = DateOnly.FromDateTime(localNow.DateTime);

        // Nutrition (quick-log now; MFP integration later, same tables).
        // Per privacy Section D.1 / draft Part 4: macro totals + meal-slot
        // timing only — food_items (names) NEVER enter the snapshot.
        var foodRows = await db.FoodEntries
            .AsNoTracking()
            .Where(f => f.UserId == userId && f.MealDate >= localToday.AddDays(-7))
            .OrderByDescending(f => f.MealDate)
            .Select(f => new
            {
                f.MealDate,
                f.MealSlot,
                f.Source,
                f.LoggedAt,
                f.Calories,
                f.ProteinG,
                f.CarbsG,
                f.FatG,
                f.FiberG,
            })
            .ToListAsync(ct);

        var latestFoodEntries = foodRows
            .Where(f => f.MealDate >= localToday.AddDays(-3))
            .Select(f => new FoodEntrySnapshot(
                MealDate: f.MealDate,
                MealSlot: f.MealSlot,
                LoggedAt: f.LoggedAt,
                Calories: f.Calories,
                ProteinG: f.ProteinG,
                CarbsG: f.CarbsG,
                FatG: f.FatG,
                FiberG: f.FiberG,
                Source: f.Source))
            .ToList();

        // Rollups computed in memory — the mfp_daily_rollups table is
        // deferred to the MFP session (session-quick-log.md checklist §1).
        var dailyMacroRollups = foodRows
            .GroupBy(f => f.MealDate)
            .OrderByDescending(g => g.Key)
            .Select(g => new DailyMacroRollup(
                Date: g.Key,
                Calories: SumNullable(g.Select(f => f.Calories)),
                ProteinG: SumNullable(g.Select(f => f.ProteinG)),
                CarbsG: SumNullable(g.Select(f => f.CarbsG)),
                FatG: SumNullable(g.Select(f => f.FatG)),
                FiberG: SumNullable(g.Select(f => f.FiberG)),
                MealsLogged: g.Count()))
            .ToList();

        // Active user notes — visible, deletable memory (privacy Part 4c).
        var notes = await db.UserNotes
            .AsNoTracking()
            .Where(n => n.UserId == userId
                     && (n.ActiveUntil == null || n.ActiveUntil >= localToday))
            .OrderByDescending(n => n.CreatedAt)
            .Take(10)
            .Select(n => new UserNoteSnapshot(n.Category, n.Note, n.ActiveUntil, n.CreatedAt))
            .ToListAsync(ct);

        // Confirmed lab biomarkers — most recent value per marker within a
        // year, flagged rows first, capped at 60 (privacy Section F.1 /
        // session-function-health §1.4). ONLY confirmed uploads: the user
        // attests the extraction before the coach may read it.
        var labSince = localToday.AddDays(-365);
        var labRows = await db.LabBiomarkers
            .AsNoTracking()
            .Where(b => b.UserId == userId
                     && b.CollectedAt >= labSince
                     && b.LabUpload!.ParseStatus == "confirmed")
            .Select(b => new
            {
                b.BiomarkerName,
                b.DisplayName,
                b.Category,
                b.NumericValue,
                b.StringValue,
                b.Unit,
                b.ReferenceLow,
                b.ReferenceHigh,
                b.Flagged,
                b.CollectedAt,
                b.LabUploadId,
            })
            .ToListAsync(ct);

        var latestBiomarkers = labRows
            .GroupBy(b => b.BiomarkerName)
            .Select(g => g.OrderByDescending(b => b.CollectedAt).First())
            .OrderBy(b => b.Flagged == "in_range" ? 1 : 0)
            .ThenBy(b => b.BiomarkerName)
            .Take(60)
            .Select(b => new BiomarkerSnapshot(
                b.BiomarkerName, b.DisplayName, b.Category, b.NumericValue,
                b.StringValue, b.Unit, b.ReferenceLow, b.ReferenceHigh,
                b.Flagged, b.CollectedAt, b.LabUploadId))
            .ToList();

        // Documents on file — summaries only, so the coach KNOWS what the
        // user has handed over everywhere (daily card + any conversation)
        // without paying full-text tokens outside a document-anchored
        // thread. The summary was produced by the extraction pass; the full
        // text rides only in threads opened from the document's chip.
        var documentsOnFile = await db.UserDocuments
            .AsNoTracking()
            .Where(d => d.UserId == userId && d.ParseStatus == "parsed")
            .OrderByDescending(d => d.UploadedAt)
            .Take(5)
            .Select(d => new DocumentOnFile(d.FileName, d.Summary, d.UploadedAt))
            .ToListAsync(ct);

        // Sections are omitted (not sent as empty arrays) when a user has no
        // data — the coach shouldn't reason about "zero meals" when the truth
        // is "hasn't logged anything yet".
        var snapshot = new
        {
            as_of_utc = asOfUtc,
            local_timezone_offset = localTzOffset,
            local_time_of_day = localTimeOfDay,
            recoveries,
            sleeps,
            workouts,
            latest_food_entries = latestFoodEntries.Count > 0 ? latestFoodEntries : null,
            daily_macro_rollups = dailyMacroRollups.Count > 0 ? dailyMacroRollups : null,
            user_notes = notes.Count > 0 ? notes : null,
            documents_on_file = documentsOnFile.Count > 0 ? documentsOnFile : null,
            latest_biomarkers = latestBiomarkers.Count > 0 ? latestBiomarkers : null,
        };

        return new AgentInputSnapshot(JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    private static TimeSpan ParseOffset(string offset)
    {
        // Accepts "+00:00" / "-06:00" formats; falls back to zero on garbage.
        if (TimeSpan.TryParseExact(offset.TrimStart('+'), @"hh\:mm",
                CultureInfo.InvariantCulture, out var positive))
        {
            return offset.StartsWith('-') ? -positive : positive;
        }
        return TimeSpan.Zero;
    }

    private sealed record RecoverySnapshot(
        DateTimeOffset CycleStart,
        string ScoreState,
        int? RecoveryScore,
        decimal? HrvMs,
        int? RhrBpm);

    private sealed record SleepSnapshot(
        DateTimeOffset Start,
        DateTimeOffset End,
        string ScoreState,
        decimal? PerformancePct,
        decimal? EfficiencyPct,
        int? TotalSleepMin);

    private sealed record WorkoutSnapshot(
        DateTimeOffset Start,
        DateTimeOffset End,
        string SportName,
        string ScoreState,
        decimal? Strain,
        int? AvgHr,
        string Source);

    private sealed record FoodEntrySnapshot(
        DateOnly MealDate,
        string MealSlot,
        DateTimeOffset? LoggedAt,
        decimal? Calories,
        decimal? ProteinG,
        decimal? CarbsG,
        decimal? FatG,
        decimal? FiberG,
        string Source);

    private sealed record DailyMacroRollup(
        DateOnly Date,
        decimal? Calories,
        decimal? ProteinG,
        decimal? CarbsG,
        decimal? FatG,
        decimal? FiberG,
        int MealsLogged);

    private sealed record UserNoteSnapshot(
        string? Category,
        string Note,
        DateOnly? ActiveUntil,
        DateTimeOffset CreatedAt);

    private sealed record DocumentOnFile(
        string FileName,
        string? Summary,
        DateTimeOffset UploadedAt);

    private sealed record BiomarkerSnapshot(
        string BiomarkerName,
        string DisplayName,
        string Category,
        decimal? NumericValue,
        string? StringValue,
        string? Unit,
        decimal? ReferenceLow,
        decimal? ReferenceHigh,
        string Flagged,
        DateOnly CollectedAt,
        Guid LabUploadId);

    private static decimal? SumNullable(IEnumerable<decimal?> values)
    {
        decimal? sum = null;
        foreach (var v in values)
        {
            if (v is { } val)
            {
                sum = (sum ?? 0) + val;
            }
        }
        return sum;
    }
}

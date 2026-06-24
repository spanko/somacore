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
        var since7d  = asOfUtc.AddDays(-7);
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
                r.WhoopCycleId, r.CycleStartAt, r.ScoreState,
                r.RecoveryScore, r.HrvRmssdMilli, r.RestingHeartRate, r.IngestedAt,
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
                s.WhoopSleepId, s.StartAt, s.EndAt, s.ScoreState, s.TimezoneOffset,
                s.SleepPerformancePercentage, s.SleepEfficiencyPercentage,
                s.TotalSleepTimeMilli, s.IngestedAt,
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
                w.WhoopWorkoutId, w.StartAt, w.EndAt, w.SportName, w.ScoreState,
                w.Strain, w.AverageHeartRate, w.IngestedAt,
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
                AvgHr: w.AverageHeartRate))
            .ToList();

        // Local time-of-day computed from the most recent sleep's TZ offset.
        var localOffset = ParseOffset(localTzOffset);
        var localNow = asOfUtc.ToOffset(localOffset);
        var localTimeOfDay = localNow.ToString("HH:mm", CultureInfo.InvariantCulture);

        var snapshot = new
        {
            as_of_utc = asOfUtc,
            local_timezone_offset = localTzOffset,
            local_time_of_day = localTimeOfDay,
            recoveries,
            sleeps,
            workouts,
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
        int? AvgHr);
}

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

        var workouts = await BuildMergedWorkoutsAsync(db, userId, since14d, ct);

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

    /// <summary>
    /// The merged-workout view (Strava brief §1.8/§1.9): one entry per
    /// physical workout across whoop_workouts, strava_activities
    /// (deleted_at null), and healthkit_workouts. Captures of the same
    /// workout — start within ±5 minutes AND same <see cref="WorkoutTypeMap"/>
    /// family — merge: Strava wins distance/elevation/speed detail and the
    /// zone/split summaries, WHOOP wins strain, duration takes the max,
    /// average HR prefers Strava → WHOOP → HealthKit. <c>sources</c> keeps
    /// provenance. Summaries are rollups — raw zone/split arrays (and GPS,
    /// polylines, kudos, gear, descriptions) NEVER enter the snapshot
    /// (privacy Section D commitment).
    /// </summary>
    private static async Task<List<MergedWorkoutSnapshot>> BuildMergedWorkoutsAsync(
        SomaCoreDbContext db,
        Guid userId,
        DateTimeOffset since,
        CancellationToken ct)
    {
        var whoopRows = await db.WhoopWorkouts
            .AsNoTracking()
            .Where(w => w.UserId == userId && w.StartAt >= since)
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
                w.MaxHeartRate,
                w.IngestedAt,
            })
            .ToListAsync(ct);

        var sources = new List<SourceWorkout>();

        sources.AddRange(whoopRows
            .GroupBy(w => w.WhoopWorkoutId)
            .Select(g => g.OrderByDescending(w => w.IngestedAt).First())
            .Select(w => new SourceWorkout(
                Source: "whoop",
                Family: WorkoutTypeMap.FamilyOf(w.SportName),
                ActivityType: w.SportName,
                Start: w.StartAt,
                ElapsedSeconds: (int)(w.EndAt - w.StartAt).TotalSeconds)
            {
                ScoreState = w.ScoreState,
                Strain = w.Strain,
                AvgHr = w.AverageHeartRate,
                MaxHr = w.MaxHeartRate,
            }));

        // Strava rows: typed columns only — the raw payloads (which carry
        // GPS, polylines, kudos, gear ids, descriptions) are not selected,
        // so they cannot leak into the snapshot.
        var stravaRows = await db.StravaActivities
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.StartedAt >= since && a.DeletedAt == null)
            .OrderByDescending(a => a.StartedAt)
            .Take(40)
            .Select(a => new
            {
                a.ActivityType,
                a.StartedAt,
                a.ElapsedSeconds,
                a.DistanceMeters,
                a.TotalElevationGainM,
                a.AverageHr,
                a.MaxHr,
                a.AverageCadence,
                a.AverageWatts,
                HrZonesJson = a.HrZones,
                SplitsJson = a.Splits,
            })
            .ToListAsync(ct);

        sources.AddRange(stravaRows.Select(a => new SourceWorkout(
            Source: "strava",
            Family: WorkoutTypeMap.FamilyOf(a.ActivityType),
            ActivityType: a.ActivityType,
            Start: a.StartedAt,
            ElapsedSeconds: a.ElapsedSeconds)
        {
            DistanceMeters = a.DistanceMeters,
            ElevationGainM = a.TotalElevationGainM,
            AvgHr = a.AverageHr,
            MaxHr = a.MaxHr,
            AvgCadence = a.AverageCadence,
            AvgWatts = a.AverageWatts,
            HrZonesSummary = SummarizeHrZones(a.HrZonesJson),
            SplitsSummary = SummarizeSplits(a.SplitsJson),
        }));

        var hkRows = await db.HealthKitWorkouts
            .AsNoTracking()
            .Where(w => w.UserId == userId && w.StartedAt >= since)
            .OrderByDescending(w => w.StartedAt)
            .Take(40)
            .Select(w => new
            {
                w.StartedAt,
                w.ElapsedSeconds,
                w.WorkoutType,
                w.AverageHr,
                w.TotalDistanceM,
                w.SourceBundleId,
            })
            .ToListAsync(ct);

        sources.AddRange(hkRows.Select(w => new SourceWorkout(
            Source: w.SourceBundleId == "manual" ? "manual" : "healthkit",
            Family: WorkoutTypeMap.FamilyOf(w.WorkoutType),
            ActivityType: w.WorkoutType,
            Start: w.StartedAt,
            ElapsedSeconds: w.ElapsedSeconds)
        {
            AvgHr = w.AverageHr,
            DistanceMeters = w.TotalDistanceM,
        }));

        return MergeWorkouts(sources);
    }

    private static readonly TimeSpan MergeWindow = TimeSpan.FromMinutes(5);
    private static readonly string[] SourcePriority = { "strava", "whoop", "healthkit", "manual" };

    private static List<MergedWorkoutSnapshot> MergeWorkouts(List<SourceWorkout> items)
    {
        var merged = new List<MergedWorkoutSnapshot>();

        foreach (var familyGroup in items.GroupBy(i => i.Family))
        {
            var ordered = familyGroup.OrderBy(i => i.Start).ToList();
            var cluster = new List<SourceWorkout>();

            foreach (var item in ordered)
            {
                if (cluster.Count > 0 && item.Start - cluster[0].Start > MergeWindow)
                {
                    merged.Add(ComposeMerged(cluster));
                    cluster = new List<SourceWorkout>();
                }
                cluster.Add(item);
            }
            if (cluster.Count > 0)
            {
                merged.Add(ComposeMerged(cluster));
            }
        }

        // Cap 20, most recent first (brief §1.9).
        return merged
            .OrderByDescending(w => w.Start)
            .Take(20)
            .ToList();
    }

    private static MergedWorkoutSnapshot ComposeMerged(List<SourceWorkout> cluster)
    {
        SourceWorkout? Pick(string source) => cluster.FirstOrDefault(c => c.Source == source);

        var strava = Pick("strava");
        var whoop = Pick("whoop");
        var hk = Pick("healthkit") ?? Pick("manual");

        var provenance = SourcePriority
            .Where(s => cluster.Any(c => c.Source == s))
            .ToList();

        return new MergedWorkoutSnapshot(
            ActivityType: strava?.ActivityType ?? whoop?.ActivityType ?? hk!.ActivityType,
            Start: cluster.Min(c => c.Start),
            // WHOOP truncates, Strava sometimes over-runs on auto-pause: max wins.
            ElapsedSeconds: cluster.Max(c => c.ElapsedSeconds),
            DistanceMeters: strava?.DistanceMeters ?? hk?.DistanceMeters,
            ElevationGainM: strava?.ElevationGainM,
            AvgHr: strava?.AvgHr ?? whoop?.AvgHr ?? hk?.AvgHr,
            MaxHr: strava?.MaxHr ?? whoop?.MaxHr,
            AvgCadence: strava?.AvgCadence,
            AvgWatts: strava?.AvgWatts,
            Strain: whoop?.Strain,
            ScoreState: whoop?.ScoreState,
            HrZonesSummary: strava?.HrZonesSummary,
            SplitsSummary: strava?.SplitsSummary,
            Sources: provenance);
    }

    /// <summary>
    /// Percent-time-per-zone rollup from the stored Strava zones response
    /// (array of {type, distribution_buckets:[{min,max,time}]}). The coach
    /// reasons about "22 min of zone 3", never raw per-second HR.
    /// </summary>
    private static IReadOnlyList<HrZonePct>? SummarizeHrZones(JsonDocument? hrZones)
    {
        if (hrZones is null) return null;
        try
        {
            foreach (var zoneSet in hrZones.RootElement.EnumerateArray())
            {
                if (!zoneSet.TryGetProperty("type", out var type)
                    || type.GetString() != "heartrate"
                    || !zoneSet.TryGetProperty("distribution_buckets", out var buckets))
                {
                    continue;
                }

                var raw = buckets.EnumerateArray()
                    .Select(b => new
                    {
                        Min = b.TryGetProperty("min", out var min) ? min.GetDecimal() : 0,
                        Max = b.TryGetProperty("max", out var max) ? max.GetDecimal() : 0,
                        Time = b.TryGetProperty("time", out var time) ? time.GetDecimal() : 0,
                    })
                    .ToList();
                var total = raw.Sum(b => b.Time);
                if (total <= 0) return null;

                return raw
                    .Select((b, i) => new HrZonePct(
                        Zone: i + 1,
                        MinBpm: (int)b.Min,
                        MaxBpm: (int)b.Max,
                        Pct: Math.Round(b.Time / total * 100, 1)))
                    .ToList();
            }
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // Malformed zones payload — drop the summary, never the workout.
            return null;
        }
    }

    /// <summary>
    /// Count + fastest/slowest pace rollup from the stored splits_metric
    /// array. Raw splits (with per-split HR) stay server-side.
    /// </summary>
    private static SplitsSummary? SummarizeSplits(JsonDocument? splits)
    {
        if (splits is null) return null;
        try
        {
            var paces = new List<decimal>();
            foreach (var split in splits.RootElement.EnumerateArray())
            {
                decimal? pace = null;
                if (split.TryGetProperty("moving_time", out var mt)
                    && split.TryGetProperty("distance", out var dist)
                    && dist.GetDecimal() > 0)
                {
                    pace = mt.GetDecimal() / (dist.GetDecimal() / 1000m);
                }
                else if (split.TryGetProperty("average_speed", out var speed)
                    && speed.GetDecimal() > 0)
                {
                    pace = 1000m / speed.GetDecimal();
                }
                if (pace is decimal p)
                {
                    paces.Add(Math.Round(p, 0));
                }
            }

            var count = splits.RootElement.GetArrayLength();
            if (count == 0) return null;

            return new SplitsSummary(
                SplitCount: count,
                FastestSplitPaceSecondsPerKm: paces.Count > 0 ? paces.Min() : null,
                SlowestSplitPaceSecondsPerKm: paces.Count > 0 ? paces.Max() : null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
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

    /// <summary>
    /// One workout capture from one source, pre-merge. Positional core plus
    /// init-only extras so each source sets only what it knows.
    /// </summary>
    private sealed record SourceWorkout(
        string Source,
        string Family,
        string ActivityType,
        DateTimeOffset Start,
        int ElapsedSeconds)
    {
        public string? ScoreState { get; init; }
        public decimal? Strain { get; init; }
        public int? AvgHr { get; init; }
        public int? MaxHr { get; init; }
        public decimal? DistanceMeters { get; init; }
        public decimal? ElevationGainM { get; init; }
        public decimal? AvgCadence { get; init; }
        public decimal? AvgWatts { get; init; }
        public IReadOnlyList<HrZonePct>? HrZonesSummary { get; init; }
        public SplitsSummary? SplitsSummary { get; init; }
    }

    private sealed record MergedWorkoutSnapshot(
        string ActivityType,
        DateTimeOffset Start,
        int ElapsedSeconds,
        decimal? DistanceMeters,
        decimal? ElevationGainM,
        int? AvgHr,
        int? MaxHr,
        decimal? AvgCadence,
        decimal? AvgWatts,
        decimal? Strain,
        string? ScoreState,
        IReadOnlyList<HrZonePct>? HrZonesSummary,
        SplitsSummary? SplitsSummary,
        IReadOnlyList<string> Sources);

    private sealed record HrZonePct(int Zone, int MinBpm, int MaxBpm, decimal Pct);

    private sealed record SplitsSummary(
        int SplitCount,
        decimal? FastestSplitPaceSecondsPerKm,
        decimal? SlowestSplitPaceSecondsPerKm);

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

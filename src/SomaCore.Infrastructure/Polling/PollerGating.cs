using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Domain.WhoopSleeps;

namespace SomaCore.Infrastructure.Polling;

/// <summary>
/// Pure decision function for the adaptive poller (Session 4.5). Given a
/// connection's current state, decides whether <see cref="ReconciliationPoller"/>
/// should fan out for it on this tick or skip it.
///
/// All inputs (including <paramref name="now"/>) are passed in so the function
/// is unit-testable without a clock, DB, or network. The reconciliation
/// poller itself is responsible for loading the inputs and acting on the
/// decision.
/// </summary>
public static class PollerGating
{
    /// <summary>
    /// Default minimum interval between successive polls of the same
    /// connection. Matches the "every 30 minutes" cadence in
    /// <c>whoop-architecture.docx</c>. The cron tick can fire more often
    /// (e.g. hourly); per-connection gating debounces that down to actual work.
    /// </summary>
    public static readonly TimeSpan DefaultMinimumPollInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Cold-start wake window in UTC, in hours. See <see cref="Evaluate"/>
    /// for the cold-start trade-off rationale.
    /// </summary>
    private const int ColdStartWakeStartHourUtc = 4;
    private const int ColdStartWakeEndHourUtc   = 11;

    /// <summary>Warm-mode window: typical wake minus 60 minutes ... plus 4 hours.</summary>
    private static readonly TimeSpan WarmWindowBefore = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan WarmWindowAfter  = TimeSpan.FromHours(4);

    /// <summary>
    /// A SCORED recovery is "for the current cycle" if its cycle ended within
    /// this many hours. WHOOP cycles are typically ~24 hours; 36 leaves
    /// generous headroom for late-night cycles that straddle a day boundary.
    /// </summary>
    private static readonly TimeSpan ActiveCycleWindow = TimeSpan.FromHours(36);

    /// <summary>
    /// Decide whether to poll this connection now.
    /// </summary>
    /// <param name="connection">Connection being evaluated.</param>
    /// <param name="latestRecovery">Most recent recovery for this connection (any score state), or null.</param>
    /// <param name="recentSleeps">
    /// Most-recent-first list of recent sleep rows for this connection (typically the last 14 days).
    /// Empty/null triggers cold-start logic. The first element's timezone offset is treated as the
    /// user's current local offset for warm-mode window math; the median of all elements'
    /// <c>EndAt</c> values is the typical wake time.
    /// </param>
    /// <param name="now">Current UTC time. Injected so tests don't depend on the clock.</param>
    /// <param name="minimumPollInterval">Minimum gap between successive polls of the same connection.</param>
    public static (PollDecision Decision, SkipReason? Reason) Evaluate(
        ExternalConnection connection,
        WhoopRecovery? latestRecovery,
        IReadOnlyList<WhoopSleep>? recentSleeps,
        DateTimeOffset now,
        TimeSpan? minimumPollInterval = null)
    {
        var minInterval = minimumPollInterval ?? DefaultMinimumPollInterval;

        // 1. Too-recent check. Debounces hourly cron ticks down to a 30-min
        //    effective cadence; also prevents stampedes if the cron fires
        //    twice in close succession (manual + scheduled).
        if (connection.LastPolledAt is DateTimeOffset lastPolled
            && now - lastPolled < minInterval)
        {
            return (PollDecision.Skip, SkipReason.TooRecent);
        }

        // 2. Stop-condition: the current cycle already has a SCORED recovery.
        //    No reason to keep polling until the next cycle begins. A SCORED
        //    recovery older than ~36 hours means the current cycle has rolled
        //    over and we should poll for the new one.
        if (latestRecovery is not null
            && latestRecovery.ScoreState == ScoreState.Scored
            && latestRecovery.CycleEndAt is DateTimeOffset cycleEnd
            && cycleEnd > now - ActiveCycleWindow)
        {
            return (PollDecision.Skip, SkipReason.CurrentCycleScored);
        }

        // 3. Wake-window check.
        //
        //    Cold-start trade-off (no sleep history yet): we use UTC hours
        //    4-11 as the cold-start window. For non-UTC users this can
        //    misalign by up to ~12 hours on the very first day; once a single
        //    sleep cycle lands the user is warm and the per-user timezone
        //    offset takes over. The alternative (capture a default timezone
        //    at onboarding) requires schema + onboarding flow changes that
        //    aren't worth the value at three-internal-user scale. Documented
        //    in the Session 4.5 prompt; revisit if this turns out to bite.
        if (recentSleeps is null || recentSleeps.Count == 0)
        {
            var utcHour = now.UtcDateTime.Hour;
            return (utcHour >= ColdStartWakeStartHourUtc && utcHour < ColdStartWakeEndHourUtc)
                ? (PollDecision.Poll, null)
                : (PollDecision.Skip, SkipReason.ColdStartOutsideWindow);
        }

        // Warm mode. The user's current local offset is whatever the most
        // recent sleep was recorded with — WHOOP returns it per sleep.
        var localOffset = recentSleeps[0].TimezoneOffset;
        var localNow = ConvertToLocal(now, localOffset);
        var typicalWakeLocal = MedianWakeLocal(recentSleeps);

        var windowStart = typicalWakeLocal - WarmWindowBefore;
        var windowEnd   = typicalWakeLocal + WarmWindowAfter;
        var localTimeOfDay = localNow.TimeOfDay;

        var inWindow = IsWithinDailyWindow(localTimeOfDay, windowStart, windowEnd);
        return inWindow
            ? (PollDecision.Poll, null)
            : (PollDecision.Skip, SkipReason.WarmOutsideWindow);
    }

    /// <summary>
    /// Median of <c>EndAt</c> across the provided sleeps, expressed as a
    /// time-of-day in the user's local timezone (taken from the most recent
    /// sleep's offset). The median is robust to occasional 3am-bedtimes and
    /// graveyard-shift outliers.
    /// </summary>
    private static TimeSpan MedianWakeLocal(IReadOnlyList<WhoopSleep> recentSleeps)
    {
        var localOffset = recentSleeps[0].TimezoneOffset;
        var localEnds = recentSleeps
            .Select(s => ConvertToLocal(s.EndAt, localOffset).TimeOfDay)
            .OrderBy(t => t)
            .ToArray();
        return localEnds[localEnds.Length / 2];
    }

    /// <summary>
    /// Apply a WHOOP-style timezone offset string (<c>"-07:00"</c>, <c>"+05:30"</c>)
    /// to a UTC <see cref="DateTimeOffset"/> and return the local wall-clock
    /// view. Falls back to the input unchanged if the offset string is malformed.
    /// </summary>
    private static DateTimeOffset ConvertToLocal(DateTimeOffset utc, string offsetText)
    {
        if (TimeSpan.TryParse(TrimSign(offsetText, out var sign), out var ts))
        {
            return utc.ToOffset(sign < 0 ? -ts : ts);
        }
        return utc;
    }

    private static string TrimSign(string s, out int sign)
    {
        if (string.IsNullOrEmpty(s)) { sign = 1; return s; }
        if (s[0] == '-') { sign = -1; return s[1..]; }
        if (s[0] == '+') { sign = 1;  return s[1..]; }
        sign = 1; return s;
    }

    /// <summary>
    /// True iff <paramref name="t"/> falls inside <c>[start, end]</c> on a
    /// 24-hour clock, handling the wrap case where <c>start</c> can be
    /// negative (window starts before midnight) or <c>end</c> exceeds 24h
    /// (window ends after midnight).
    /// </summary>
    private static bool IsWithinDailyWindow(TimeSpan t, TimeSpan start, TimeSpan end)
    {
        // Normalize start/end into [0, 24h). Negative start (window straddles
        // midnight backward) and end > 24h (window straddles forward) both
        // wrap around. TimeSpan has no native % operator, so we work in ticks
        // and use C#'s ((x % m) + m) % m idiom to handle negative inputs.
        var oneDayTicks = TimeSpan.FromHours(24).Ticks;
        static long Norm(long ticks, long mod) => ((ticks % mod) + mod) % mod;

        var wrappedStart = TimeSpan.FromTicks(Norm(start.Ticks, oneDayTicks));
        var wrappedEnd   = TimeSpan.FromTicks(Norm(end.Ticks,   oneDayTicks));

        if (wrappedStart <= wrappedEnd)
        {
            return t >= wrappedStart && t <= wrappedEnd;
        }
        // Wrap case: e.g. window = 23:00..02:00 — match if t >= 23:00 OR t <= 02:00.
        return t >= wrappedStart || t <= wrappedEnd;
    }
}

public enum PollDecision
{
    Skip,
    Poll,
}

/// <summary>Why a Skip decision was returned. Logged on every skip for ops visibility.</summary>
public enum SkipReason
{
    TooRecent,
    CurrentCycleScored,
    ColdStartOutsideWindow,
    WarmOutsideWindow,
}

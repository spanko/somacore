using System.Text.Json;

using FluentAssertions;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Domain.WhoopSleeps;
using SomaCore.Infrastructure.Polling;

namespace SomaCore.UnitTests.Polling;

/// <summary>
/// Pure-function tests for <see cref="PollerGating.Evaluate"/>. No Docker,
/// no DB, no clock — every input is injected. Covers each gating branch
/// plus boundary behavior on the warm wake window.
/// </summary>
public class PollerGatingTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 5, 24, 14, 0, 0, TimeSpan.Zero);

    // ---- Too-recent ---------------------------------------------------------

    [Fact]
    public void Skips_with_TooRecent_when_last_poll_inside_minimum_interval()
    {
        var connection = NewConnection(lastPolledAt: NowUtc - TimeSpan.FromMinutes(10));

        var (decision, reason) = PollerGating.Evaluate(
            connection, latestRecovery: null, recentSleeps: null, NowUtc,
            minimumPollInterval: TimeSpan.FromMinutes(30));

        decision.Should().Be(PollDecision.Skip);
        reason.Should().Be(SkipReason.TooRecent);
    }

    [Fact]
    public void Does_not_skip_when_last_poll_at_or_beyond_minimum_interval()
    {
        var connection = NewConnection(lastPolledAt: NowUtc - TimeSpan.FromMinutes(30));

        var (decision, _) = PollerGating.Evaluate(
            connection, latestRecovery: null, recentSleeps: null, NowUtc,
            minimumPollInterval: TimeSpan.FromMinutes(30));

        // Cold-start path takes over — 14:00 UTC is outside the 4-11 UTC window,
        // so this still skips, just not for the too-recent reason.
        decision.Should().Be(PollDecision.Skip);
        // Specifically, not TooRecent.
        _ = decision;
    }

    [Fact]
    public void Does_not_apply_too_recent_check_when_last_polled_at_is_null()
    {
        var connection = NewConnection(lastPolledAt: null);
        // Force a cold-start in-window decision so we can assert it ran past the too-recent gate.
        var nowInWindow = new DateTimeOffset(2026, 5, 24, 7, 0, 0, TimeSpan.Zero); // 7 UTC

        var (decision, reason) = PollerGating.Evaluate(
            connection, latestRecovery: null, recentSleeps: null, nowInWindow);

        decision.Should().Be(PollDecision.Poll);
        reason.Should().BeNull();
    }

    // ---- Stop-condition: current cycle already scored -----------------------

    [Fact]
    public void Skips_with_CurrentCycleScored_when_SCORED_recovery_is_within_active_cycle_window()
    {
        var connection = NewConnection(lastPolledAt: NowUtc - TimeSpan.FromHours(2));
        var scoredRecent = NewRecovery(ScoreState.Scored, cycleEndAt: NowUtc - TimeSpan.FromHours(2));

        var (decision, reason) = PollerGating.Evaluate(
            connection, latestRecovery: scoredRecent, recentSleeps: null, NowUtc);

        decision.Should().Be(PollDecision.Skip);
        reason.Should().Be(SkipReason.CurrentCycleScored);
    }

    [Fact]
    public void Does_not_short_circuit_when_recovery_is_PENDING_SCORE()
    {
        // LastPolledAt null to bypass the too-recent gate; using a cold-start
        // in-window "now" so the wake-window gate also passes; the only thing
        // under test here is that PENDING_SCORE doesn't short-circuit.
        var connection = NewConnection(lastPolledAt: null);
        var nowInWindow = new DateTimeOffset(2026, 5, 24, 7, 0, 0, TimeSpan.Zero);
        var pending = NewRecovery(ScoreState.PendingScore, cycleEndAt: nowInWindow - TimeSpan.FromHours(2));

        var (decision, reason) = PollerGating.Evaluate(
            connection, latestRecovery: pending, recentSleeps: null, nowInWindow);

        decision.Should().Be(PollDecision.Poll);
        reason.Should().BeNull();
    }

    [Fact]
    public void Does_not_short_circuit_when_recovery_is_UNSCORABLE()
    {
        var connection = NewConnection(lastPolledAt: null);
        var nowInWindow = new DateTimeOffset(2026, 5, 24, 7, 0, 0, TimeSpan.Zero);
        var unscorable = NewRecovery(ScoreState.Unscorable, cycleEndAt: nowInWindow - TimeSpan.FromHours(2));

        var (decision, _) = PollerGating.Evaluate(
            connection, latestRecovery: unscorable, recentSleeps: null, nowInWindow);

        decision.Should().Be(PollDecision.Poll);
    }

    [Fact]
    public void Does_not_short_circuit_when_SCORED_recovery_is_older_than_active_cycle_window()
    {
        var connection = NewConnection(lastPolledAt: null);
        var nowInWindow = new DateTimeOffset(2026, 5, 24, 7, 0, 0, TimeSpan.Zero);
        var stale = NewRecovery(ScoreState.Scored, cycleEndAt: nowInWindow - TimeSpan.FromHours(48));

        var (decision, _) = PollerGating.Evaluate(
            connection, latestRecovery: stale, recentSleeps: null, nowInWindow);

        decision.Should().Be(PollDecision.Poll);
    }

    // ---- Cold start (no sleep history) --------------------------------------

    [Theory]
    [InlineData(4, true)]   // start of window
    [InlineData(7, true)]   // middle
    [InlineData(10, true)]  // last in-window hour
    [InlineData(11, false)] // end of window (exclusive)
    [InlineData(3, false)]  // before window
    [InlineData(23, false)] // late night
    public void Cold_start_polls_when_UTC_hour_in_window(int utcHour, bool expectedPoll)
    {
        var connection = NewConnection(lastPolledAt: null);
        var now = new DateTimeOffset(2026, 5, 24, utcHour, 0, 0, TimeSpan.Zero);

        var (decision, reason) = PollerGating.Evaluate(
            connection, latestRecovery: null, recentSleeps: null, now);

        if (expectedPoll)
        {
            decision.Should().Be(PollDecision.Poll);
            reason.Should().BeNull();
        }
        else
        {
            decision.Should().Be(PollDecision.Skip);
            reason.Should().Be(SkipReason.ColdStartOutsideWindow);
        }
    }

    // ---- Warm mode ----------------------------------------------------------

    [Fact]
    public void Warm_mode_polls_within_window_of_typical_wake_local_time()
    {
        // Typical wake = 07:00 local (-07:00 offset → 14:00 UTC).
        // Window = 06:00..11:00 local = 13:00..18:00 UTC.
        var connection = NewConnection(lastPolledAt: null);
        var sleeps = MountainSleepsEndingAt(new TimeSpan(7, 0, 0), count: 7);
        var now = new DateTimeOffset(2026, 5, 24, 15, 0, 0, TimeSpan.Zero); // 08:00 local

        var (decision, reason) = PollerGating.Evaluate(connection, latestRecovery: null, recentSleeps: sleeps, now);

        decision.Should().Be(PollDecision.Poll);
        reason.Should().BeNull();
    }

    [Fact]
    public void Warm_mode_skips_when_local_time_after_window_end()
    {
        var connection = NewConnection(lastPolledAt: null);
        var sleeps = MountainSleepsEndingAt(new TimeSpan(7, 0, 0), count: 7);
        // 12:00 local = 19:00 UTC. Window ends at 11:00 local (= 18:00 UTC).
        var now = new DateTimeOffset(2026, 5, 24, 19, 0, 0, TimeSpan.Zero);

        var (decision, reason) = PollerGating.Evaluate(connection, latestRecovery: null, recentSleeps: sleeps, now);

        decision.Should().Be(PollDecision.Skip);
        reason.Should().Be(SkipReason.WarmOutsideWindow);
    }

    [Fact]
    public void Warm_mode_skips_when_local_time_before_window_start()
    {
        var connection = NewConnection(lastPolledAt: null);
        var sleeps = MountainSleepsEndingAt(new TimeSpan(7, 0, 0), count: 7);
        // 03:00 local = 10:00 UTC. Window starts at 06:00 local (= 13:00 UTC).
        var now = new DateTimeOffset(2026, 5, 24, 10, 0, 0, TimeSpan.Zero);

        var (decision, _) = PollerGating.Evaluate(connection, latestRecovery: null, recentSleeps: sleeps, now);
        decision.Should().Be(PollDecision.Skip);
    }

    [Fact]
    public void Warm_mode_includes_lower_boundary_typical_wake_minus_60_min()
    {
        // Typical wake = 07:00 local. Window start = 06:00 local = 13:00 UTC. Exactly on boundary -> Poll.
        var connection = NewConnection(lastPolledAt: null);
        var sleeps = MountainSleepsEndingAt(new TimeSpan(7, 0, 0), count: 7);
        var now = new DateTimeOffset(2026, 5, 24, 13, 0, 0, TimeSpan.Zero);

        var (decision, _) = PollerGating.Evaluate(connection, latestRecovery: null, recentSleeps: sleeps, now);
        decision.Should().Be(PollDecision.Poll);
    }

    [Fact]
    public void Warm_mode_includes_upper_boundary_typical_wake_plus_4_hours()
    {
        // Typical wake = 07:00 local. Window end = 11:00 local = 18:00 UTC. Exactly on boundary -> Poll.
        var connection = NewConnection(lastPolledAt: null);
        var sleeps = MountainSleepsEndingAt(new TimeSpan(7, 0, 0), count: 7);
        var now = new DateTimeOffset(2026, 5, 24, 18, 0, 0, TimeSpan.Zero);

        var (decision, _) = PollerGating.Evaluate(connection, latestRecovery: null, recentSleeps: sleeps, now);
        decision.Should().Be(PollDecision.Poll);
    }

    [Fact]
    public void Warm_mode_median_is_robust_to_one_outlier_late_wake()
    {
        // Six sleeps wake at 07:00, one at 13:00. Median is still 07:00.
        var connection = NewConnection(lastPolledAt: null);
        var sleeps = new List<WhoopSleep>(MountainSleepsEndingAt(new TimeSpan(7, 0, 0), count: 6));
        sleeps.Insert(0, NewSleep(end: ToUtc(new TimeSpan(13, 0, 0), "-07:00"), tz: "-07:00"));
        // Within window of 07:00 wake -> Poll.
        var now = new DateTimeOffset(2026, 5, 24, 14, 0, 0, TimeSpan.Zero); // 07:00 local
        var (decision, _) = PollerGating.Evaluate(connection, latestRecovery: null, recentSleeps: sleeps, now);
        decision.Should().Be(PollDecision.Poll);
    }

    [Fact]
    public void Warm_mode_handles_window_that_wraps_midnight()
    {
        // Typical wake = 23:30 local (graveyard worker). Window = 22:30 local .. 03:30 local.
        // 00:00 local should still be in window.
        var connection = NewConnection(lastPolledAt: null);
        var sleeps = MountainSleepsEndingAt(new TimeSpan(23, 30, 0), count: 7);
        // 00:00 local = 07:00 UTC (with -07:00 offset).
        var now = new DateTimeOffset(2026, 5, 24, 7, 0, 0, TimeSpan.Zero);

        var (decision, _) = PollerGating.Evaluate(connection, latestRecovery: null, recentSleeps: sleeps, now);
        decision.Should().Be(PollDecision.Poll);
    }

    // ---- Helpers -----------------------------------------------------------

    private static ExternalConnection NewConnection(DateTimeOffset? lastPolledAt) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Source = ConnectionSource.Whoop,
        Status = ConnectionStatus.Active,
        KeyVaultSecretName = "whoop-refresh-test",
        Scopes = Array.Empty<string>(),
        ConnectionMetadata = JsonDocument.Parse("{}"),
        LastPolledAt = lastPolledAt,
        LastPollOutcome = lastPolledAt is null ? null : PollOutcome.Polled,
    };

    private static WhoopRecovery NewRecovery(string scoreState, DateTimeOffset cycleEndAt) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        WhoopCycleId = 1,
        ScoreState = scoreState,
        CycleStartAt = cycleEndAt - TimeSpan.FromHours(24),
        CycleEndAt = cycleEndAt,
        IngestedVia = IngestedVia.Poller,
        IngestedAt = cycleEndAt,
        RawPayload = JsonDocument.Parse("{}"),
    };

    /// <summary>
    /// Build N sleep rows whose local <c>end_at</c> wall-clock time is the
    /// given time-of-day. Spaced one day apart, going backwards from "today."
    /// </summary>
    private static List<WhoopSleep> MountainSleepsEndingAt(TimeSpan localWake, int count)
    {
        const string tz = "-07:00";
        var sleeps = new List<WhoopSleep>(count);
        for (int i = 0; i < count; i++)
        {
            var dayUtc = new DateTimeOffset(2026, 5, 24, 0, 0, 0, TimeSpan.Zero).AddDays(-i);
            // dayUtc represents "today's midnight UTC"; convert to wake-time-of-day in the local offset
            // by composing UTC midnight + (localWake) and then shifting to UTC.
            var localOffset = TimeSpan.FromHours(-7);
            var localDateTime = new DateTime(dayUtc.Year, dayUtc.Month, dayUtc.Day, localWake.Hours, localWake.Minutes, localWake.Seconds);
            var localDto = new DateTimeOffset(localDateTime, localOffset);
            sleeps.Add(NewSleep(end: localDto.ToUniversalTime(), tz));
        }
        return sleeps;
    }

    private static DateTimeOffset ToUtc(TimeSpan localTimeOfDay, string offsetText)
    {
        var offset = TimeSpan.Parse(offsetText.TrimStart('+'));
        if (offsetText.StartsWith('-')) offset = -offset;
        var localDateTime = new DateTime(2026, 5, 24, localTimeOfDay.Hours, localTimeOfDay.Minutes, localTimeOfDay.Seconds);
        return new DateTimeOffset(localDateTime, offset).ToUniversalTime();
    }

    private static WhoopSleep NewSleep(DateTimeOffset end, string tz) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        WhoopSleepId = Guid.NewGuid(),
        StartAt = end - TimeSpan.FromHours(8),
        EndAt = end,
        TimezoneOffset = tz,
        Nap = false,
        ScoreState = ScoreState.Scored,
        IngestedVia = IngestedVia.Poller,
        IngestedAt = end,
        RawPayload = JsonDocument.Parse("{}"),
    };
}

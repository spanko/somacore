using System.Text.Json;

using FluentAssertions;

using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Domain.WhoopSleeps;

namespace SomaCore.UnitTests.Domain;

public class WhoopSleepsTests
{
    [Fact]
    public void Should_reuse_score_state_and_ingested_via_constants_from_recovery_namespace()
    {
        // ADR alignment + CLAUDE.md "no parallel patterns": sleep does not
        // introduce its own ScoreState/IngestedVia constants. It reuses the
        // recovery namespace so cross-table queries (e.g. "all SCORED entities
        // for this user") stay clean.
        var sleep = new WhoopSleep
        {
            UserId = Guid.NewGuid(),
            ExternalConnectionId = Guid.NewGuid(),
            WhoopSleepId = Guid.NewGuid(),
            StartAt = DateTimeOffset.UtcNow.AddHours(-9),
            EndAt = DateTimeOffset.UtcNow.AddHours(-1),
            TimezoneOffset = "-07:00",
            Nap = false,
            ScoreState = ScoreState.Scored,
            SleepPerformancePercentage = 87.5m,
            SleepEfficiencyPercentage = 95.2m,
            SleepConsistencyPercentage = 80.0m,
            TotalInBedTimeMilli = 30_000_000,
            TotalSleepTimeMilli = 28_500_000,
            IngestedVia = IngestedVia.Webhook,
            IngestedAt = DateTimeOffset.UtcNow,
            RawPayload = JsonDocument.Parse("""{"id":"00000000-0000-0000-0000-000000000000"}"""),
            Score = JsonDocument.Parse("""{"sleep_performance_percentage":87.5}"""),
        };

        sleep.ScoreState.Should().Be(ScoreState.Scored);
        sleep.IngestedVia.Should().Be(IngestedVia.Webhook);
        sleep.TimezoneOffset.Should().Be("-07:00");
        sleep.Nap.Should().BeFalse();
    }

    [Fact]
    public void Score_column_is_nullable_for_unscored_states()
    {
        var pending = new WhoopSleep
        {
            UserId = Guid.NewGuid(),
            ExternalConnectionId = Guid.NewGuid(),
            WhoopSleepId = Guid.NewGuid(),
            StartAt = DateTimeOffset.UtcNow.AddHours(-9),
            EndAt = DateTimeOffset.UtcNow.AddHours(-1),
            TimezoneOffset = "+00:00",
            Nap = false,
            ScoreState = ScoreState.PendingScore,
            IngestedVia = IngestedVia.Webhook,
            IngestedAt = DateTimeOffset.UtcNow,
            RawPayload = JsonDocument.Parse("""{}"""),
            Score = null,
        };

        pending.Score.Should().BeNull();
        pending.SleepPerformancePercentage.Should().BeNull();
        pending.TotalSleepTimeMilli.Should().BeNull();
    }
}

using System.Text.Json;

using FluentAssertions;

using SomaCore.Domain.WhoopRecoveries;

namespace SomaCore.UnitTests.Domain;

public class WhoopRecoveriesTests
{
    [Fact]
    public void Should_define_score_state_and_ingested_via_constants_and_construct_entity()
    {
        ScoreState.All.Should().BeEquivalentTo(new[] { "SCORED", "PENDING_SCORE", "UNSCORABLE" });
        IngestedVia.All.Should().BeEquivalentTo(new[] { "webhook", "poller", "on_open_pull" });

        var recovery = new WhoopRecovery
        {
            UserId = Guid.NewGuid(),
            ExternalConnectionId = Guid.NewGuid(),
            WhoopCycleId = 1234567890L,
            ScoreState = ScoreState.Scored,
            RecoveryScore = 67,
            HrvRmssdMilli = 42.1234m,
            CycleStartAt = DateTimeOffset.UtcNow.AddHours(-8),
            IngestedVia = IngestedVia.Webhook,
            IngestedAt = DateTimeOffset.UtcNow,
            RawPayload = JsonDocument.Parse("""{"score":67}"""),
        };

        recovery.ScoreState.Should().Be("SCORED");
        recovery.IngestedVia.Should().Be("webhook");
        recovery.RecoveryScore.Should().Be(67);
    }
}

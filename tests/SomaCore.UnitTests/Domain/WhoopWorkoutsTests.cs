using System.Text.Json;

using FluentAssertions;

using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Domain.WhoopWorkouts;

namespace SomaCore.UnitTests.Domain;

public class WhoopWorkoutsTests
{
    [Fact]
    public void Should_reuse_score_state_and_ingested_via_constants_from_recovery_namespace()
    {
        // Same invariant as WhoopSleepsTests: workout does not introduce its
        // own ScoreState/IngestedVia constants. Shared vocabulary keeps
        // cross-table queries (e.g. "all SCORED entities for this user")
        // mechanically simple.
        var workout = new WhoopWorkout
        {
            UserId = Guid.NewGuid(),
            ExternalConnectionId = Guid.NewGuid(),
            WhoopWorkoutId = Guid.NewGuid(),
            StartAt = DateTimeOffset.UtcNow.AddHours(-2),
            EndAt = DateTimeOffset.UtcNow.AddHours(-1),
            TimezoneOffset = "-07:00",
            SportName = "running",
            ScoreState = ScoreState.Scored,
            Strain = 12.5m,
            AverageHeartRate = 145,
            MaxHeartRate = 178,
            Kilojoule = 2100.5m,
            IngestedVia = IngestedVia.Webhook,
            IngestedAt = DateTimeOffset.UtcNow,
            RawPayload = JsonDocument.Parse("""{"id":"00000000-0000-0000-0000-000000000000"}"""),
            Score = JsonDocument.Parse("""{"strain":12.5}"""),
        };

        workout.ScoreState.Should().Be(ScoreState.Scored);
        workout.IngestedVia.Should().Be(IngestedVia.Webhook);
        workout.SportName.Should().Be("running");
        workout.TimezoneOffset.Should().Be("-07:00");
    }

    [Fact]
    public void Score_and_typed_columns_are_nullable_for_unscored_states()
    {
        var pending = new WhoopWorkout
        {
            UserId = Guid.NewGuid(),
            ExternalConnectionId = Guid.NewGuid(),
            WhoopWorkoutId = Guid.NewGuid(),
            StartAt = DateTimeOffset.UtcNow.AddHours(-2),
            EndAt = DateTimeOffset.UtcNow.AddHours(-1),
            TimezoneOffset = "+00:00",
            SportName = "weightlifting",
            ScoreState = ScoreState.PendingScore,
            IngestedVia = IngestedVia.Webhook,
            IngestedAt = DateTimeOffset.UtcNow,
            RawPayload = JsonDocument.Parse("""{}"""),
            Score = null,
        };

        pending.Score.Should().BeNull();
        pending.Strain.Should().BeNull();
        pending.AverageHeartRate.Should().BeNull();
        pending.MaxHeartRate.Should().BeNull();
        pending.Kilojoule.Should().BeNull();
    }
}

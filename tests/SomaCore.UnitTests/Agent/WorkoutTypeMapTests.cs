using FluentAssertions;

using SomaCore.Infrastructure.Agent;

namespace SomaCore.UnitTests.Agent;

public class WorkoutTypeMapTests
{
    [Theory]
    // The dedup rule's load-bearing equivalences (Strava brief §1.8):
    // WHOOP name ≈ Strava type ≈ HealthKit type.
    [InlineData("running", "running")]        // WHOOP
    [InlineData("Run", "running")]            // Strava
    [InlineData("TrailRun", "running")]       // Strava
    [InlineData("HKWorkoutActivityTypeRunning", "running")] // HealthKit
    [InlineData("cycling", "cycling")]
    [InlineData("Ride", "cycling")]
    [InlineData("VirtualRide", "cycling")]
    [InlineData("HKWorkoutActivityTypeCycling", "cycling")]
    [InlineData("weightlifting", "strength")]
    [InlineData("WeightTraining", "strength")]
    [InlineData("HKWorkoutActivityTypeTraditionalStrengthTraining", "strength")]
    [InlineData("strength", "strength")]      // quick-log free text
    [InlineData("Swim", "swimming")]
    [InlineData("Walk", "walking")]
    [InlineData("Hike", "hiking")]
    [InlineData("Yoga", "yoga")]
    public void Maps_each_source_vocabulary_to_the_shared_family(string activityType, string expectedFamily)
        => WorkoutTypeMap.FamilyOf(activityType).Should().Be(expectedFamily);

    [Theory]
    [InlineData("Windsurf")]
    [InlineData("something-unheard-of")]
    [InlineData("")]
    [InlineData(null)]
    public void Unmapped_types_fall_through_to_other_rather_than_over_merging(string? activityType)
        => WorkoutTypeMap.FamilyOf(activityType).Should().Be(WorkoutTypeMap.Other);
}

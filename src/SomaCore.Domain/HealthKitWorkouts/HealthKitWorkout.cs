using System.Text.Json;

using SomaCore.Domain.Common;
using SomaCore.Domain.Users;

namespace SomaCore.Domain.HealthKitWorkouts;

/// <summary>
/// A workout that did not come through the WHOOP pipeline: manually logged
/// via quick-log today; Apple-Health-sourced (iOS companion) in the Strava
/// session. Table pulled forward from the Strava session brief §1.7 so that
/// build lands into a live schema. <c>SourceBundleId</c> carries provenance:
/// 'manual' now, iOS bundle ids (e.g. 'com.apple.workout') later.
/// </summary>
public class HealthKitWorkout : IHasTimestamps
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>'manual' or the writing app's iOS bundle identifier.</summary>
    public string SourceBundleId { get; set; } = string.Empty;

    /// <summary>
    /// Idempotency key. HealthKit's HKObject.uuid for iOS-sourced rows; a
    /// fresh Guid for manual rows (each confirmed manual log is one workout).
    /// </summary>
    public Guid HkSampleUuid { get; set; }

    /// <summary>HKWorkoutActivityType enum name for iOS rows; a free-form type ('run', 'strength') for manual rows.</summary>
    public string WorkoutType { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public int ElapsedSeconds { get; set; }

    public decimal? TotalEnergyKcal { get; set; }

    public decimal? TotalDistanceM { get; set; }

    public int? AverageHr { get; set; }

    /// <summary>Raw HKMetadata for iOS rows; {intensity, user_text} for manual rows.</summary>
    public JsonDocument? HkMetadata { get; set; }

    public DateTimeOffset IngestedAt { get; set; }

    public string? TraceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User? User { get; set; }
}

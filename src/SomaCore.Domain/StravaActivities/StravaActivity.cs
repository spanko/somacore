using System.Text.Json;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.Users;

namespace SomaCore.Domain.StravaActivities;

/// <summary>
/// A Strava activity ingested via the direct API integration (webhook,
/// reconciliation poller, or on-open pull). Schema per the Strava session
/// brief §1.7. Rows deleted at Strava are soft-deleted here
/// (<see cref="DeletedAt"/>) so the trace survives; the snapshot builder
/// filters them out.
/// </summary>
public class StravaActivity : IHasTimestamps
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// FK to <see cref="ExternalConnection"/>. Nullable so that disconnecting
    /// Strava (deleting the connection row) preserves the activity as
    /// historical data tied to the user, not the connection. Same contract as
    /// <see cref="SomaCore.Domain.WhoopWorkouts.WhoopWorkout.ExternalConnectionId"/>.
    /// </summary>
    public Guid? ExternalConnectionId { get; set; }

    /// <summary>Strava's activity id — a bigint, unlike WHOOP's uuid ids.</summary>
    public long StravaActivityId { get; set; }

    public long StravaAthleteId { get; set; }

    /// <summary>Strava sport type verbatim (e.g. 'Run', 'Ride', 'WeightTraining').</summary>
    public string ActivityType { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public int ElapsedSeconds { get; set; }

    public int? MovingSeconds { get; set; }

    public decimal? DistanceMeters { get; set; }

    public decimal? TotalElevationGainM { get; set; }

    public decimal? AverageSpeedMps { get; set; }

    public decimal? MaxSpeedMps { get; set; }

    public int? AverageHr { get; set; }

    public int? MaxHr { get; set; }

    public decimal? AverageCadence { get; set; }

    public decimal? AverageWatts { get; set; }

    public int? MaxWatts { get; set; }

    public int? WeightedAvgWatts { get; set; }

    /// <summary>True when watts came from a power meter rather than Strava's estimate.</summary>
    public bool? DeviceWatts { get; set; }

    /// <summary>
    /// Stored server-side only — stripped before the agent snapshot per the
    /// privacy commitment (brief §1.9/§1.10).
    /// </summary>
    public int? KudosCount { get; set; }

    public decimal? Calories { get; set; }

    /// <summary>From the detail fetch (§1.5); null until <see cref="DetailFetchedAt"/> is set.</summary>
    public JsonDocument? HrZones { get; set; }

    public JsonDocument? Splits { get; set; }

    public JsonDocument? Laps { get; set; }

    public JsonDocument? RawSummaryPayload { get; set; }

    public JsonDocument? RawDetailPayload { get; set; }

    /// <summary>Null = detail not fetched yet (short activity, or fetch failed and awaits poller retry).</summary>
    public DateTimeOffset? DetailFetchedAt { get; set; }

    /// <summary>Soft delete — set when Strava sends an activity-delete webhook event.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public string IngestedVia { get; set; } = string.Empty;

    public DateTimeOffset IngestedAt { get; set; }

    public string? TraceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User? User { get; set; }

    public ExternalConnection? ExternalConnection { get; set; }
}

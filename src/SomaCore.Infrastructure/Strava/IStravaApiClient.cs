using System.Text.Json;

using SomaCore.Domain.Common;

namespace SomaCore.Infrastructure.Strava;

public interface IStravaApiClient
{
    /// <summary>
    /// GET /activities/{id} — the detailed activity (splits_metric + laps
    /// included for own activities). Null on 404 (deleted at Strava between
    /// the webhook and our fetch).
    /// </summary>
    Task<Result<StravaActivityFetch?>> GetActivityAsync(
        string accessToken,
        long activityId,
        CancellationToken cancellationToken);

    /// <summary>
    /// GET /activities/{id}/zones — HR (and power) zone distribution. Null on
    /// 404: Strava has no zones for the activity (no HR data, or the athlete
    /// hasn't configured zones) — that's an answer, not an error.
    /// </summary>
    Task<Result<JsonDocument?>> GetActivityZonesAsync(
        string accessToken,
        long activityId,
        CancellationToken cancellationToken);

    /// <summary>
    /// GET /athlete/activities?after={epoch} — summary shapes, newest window
    /// first paged internally (per_page 100, capped). Used by the S5
    /// reconciliation poller to find missed activities.
    /// </summary>
    Task<Result<IReadOnlyList<StravaActivityFetch>>> ListAthleteActivitiesAsync(
        string accessToken,
        long afterEpoch,
        CancellationToken cancellationToken);
}

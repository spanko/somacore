using System.Text.Json;
using System.Text.Json.Serialization;

namespace SomaCore.Infrastructure.Strava;

/// <summary>
/// Token response from POST /oauth/token. Strava rotates the refresh token on
/// every exchange AND every refresh (same rotation contract as WHOOP). The
/// <c>athlete</c> summary is present on the authorization-code exchange only —
/// refresh responses omit it. Granted scopes are NOT in this body; Strava
/// reports them as a comma-separated <c>scope</c> query parameter on the
/// callback redirect instead.
/// </summary>
public sealed record StravaTokenResponse(
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresInSeconds,
    [property: JsonPropertyName("expires_at")] long ExpiresAtEpoch,
    [property: JsonPropertyName("athlete")] StravaAthleteSummary? Athlete);

public sealed record StravaAthleteSummary(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("firstname")] string? FirstName,
    [property: JsonPropertyName("lastname")] string? LastName);

/// <summary>
/// Typed subset of Strava's activity representation — GET /activities/{id}
/// returns the detailed shape (includes <c>splits_metric</c> + <c>laps</c>);
/// GET /athlete/activities returns summaries (those fields absent). Numeric
/// HR/cadence/watt fields arrive as floats; we accept <c>decimal?</c> on the
/// wire and round where the schema wants ints. The full response is preserved
/// alongside as a raw payload (see <see cref="StravaActivityFetch"/>).
/// </summary>
public sealed record StravaActivityPayload(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("athlete")] StravaAthleteRef? Athlete,
    [property: JsonPropertyName("sport_type")] string? SportType,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("start_date")] DateTimeOffset StartDate,
    [property: JsonPropertyName("elapsed_time")] int ElapsedTime,
    [property: JsonPropertyName("moving_time")] int? MovingTime,
    [property: JsonPropertyName("distance")] decimal? Distance,
    [property: JsonPropertyName("total_elevation_gain")] decimal? TotalElevationGain,
    [property: JsonPropertyName("average_speed")] decimal? AverageSpeed,
    [property: JsonPropertyName("max_speed")] decimal? MaxSpeed,
    [property: JsonPropertyName("average_heartrate")] decimal? AverageHeartrate,
    [property: JsonPropertyName("max_heartrate")] decimal? MaxHeartrate,
    [property: JsonPropertyName("average_cadence")] decimal? AverageCadence,
    [property: JsonPropertyName("average_watts")] decimal? AverageWatts,
    [property: JsonPropertyName("max_watts")] int? MaxWatts,
    [property: JsonPropertyName("weighted_average_watts")] int? WeightedAverageWatts,
    [property: JsonPropertyName("device_watts")] bool? DeviceWatts,
    [property: JsonPropertyName("kudos_count")] int? KudosCount,
    [property: JsonPropertyName("calories")] decimal? Calories,
    [property: JsonPropertyName("splits_metric")] JsonElement? SplitsMetric,
    [property: JsonPropertyName("laps")] JsonElement? Laps)
{
    /// <summary>Strava is migrating type → sport_type; prefer the newer field.</summary>
    public string ResolvedType => SportType ?? Type ?? "Unknown";
}

public sealed record StravaAthleteRef(
    [property: JsonPropertyName("id")] long Id);

/// <summary>A typed activity plus the verbatim response body it came from.</summary>
public sealed record StravaActivityFetch(StravaActivityPayload Payload, JsonDocument Raw);

/// <summary>
/// Inbound webhook event per developers.strava.com/docs/webhooks. One event
/// per POST (no batching). <c>updates</c> values arrive as strings — athlete
/// deauthorization is <c>object_type=athlete</c> with
/// <c>updates["authorized"]=="false"</c>. Strava does not sign webhook
/// bodies; authenticity rests on the verify-token handshake at subscription
/// time plus the subscription_id check on each event.
/// </summary>
public sealed record StravaWebhookEnvelope(
    [property: JsonPropertyName("object_type")] string ObjectType,
    [property: JsonPropertyName("object_id")] long ObjectId,
    [property: JsonPropertyName("aspect_type")] string AspectType,
    [property: JsonPropertyName("updates")] Dictionary<string, string>? Updates,
    [property: JsonPropertyName("owner_id")] long OwnerId,
    [property: JsonPropertyName("subscription_id")] long SubscriptionId,
    [property: JsonPropertyName("event_time")] long EventTimeEpoch);

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

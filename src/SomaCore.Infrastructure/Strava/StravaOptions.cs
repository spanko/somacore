namespace SomaCore.Infrastructure.Strava;

/// <summary>
/// Configuration for the Strava direct-API integration (Track D Session 3).
/// Defaults OFF — nothing Strava-facing runs until Adam creates the developer
/// account, adds the Key Vault secrets, and flips the flag at deploy time.
/// Unlike <see cref="Whoop.WhoopOptions"/>, fields carry no [Required]
/// attributes: the section is expected to be absent while the flag is off.
/// </summary>
public sealed class StravaOptions
{
    public const string SectionName = "Strava";

    public bool Enabled { get; init; }

    public string ClientId { get; init; } = string.Empty;

    public string ClientSecret { get; init; } = string.Empty;

    public string RedirectUri { get; init; } = string.Empty;

    public string AuthorizeUri { get; init; } = "https://www.strava.com/oauth/authorize";

    public string TokenUri { get; init; } = "https://www.strava.com/oauth/token";

    /// <summary>
    /// Best-effort revocation on disconnect. Strava's published endpoint is
    /// /oauth/deauthorize (the session brief's "/oauth/revoke" doesn't exist
    /// in Strava's docs) — configurable here so a wire-level surprise is a
    /// config change, not a code change.
    /// </summary>
    public string DeauthorizeUri { get; init; } = "https://www.strava.com/oauth/deauthorize";

    /// <summary>Comma-separated Strava scopes (Strava's wire format, unlike WHOOP's space-separated).</summary>
    public string Scopes { get; init; } = "activity:read_all";

    /// <summary>Base URL for the v3 data API (activities, zones, athlete listings).</summary>
    public string ApiBaseUri { get; init; } = "https://www.strava.com/api/v3";

    /// <summary>
    /// Shared token echoed back by Strava's webhook verify-challenge GET
    /// (hub.verify_token). Chosen by us at subscription registration time.
    /// </summary>
    public string WebhookVerifyToken { get; init; } = string.Empty;

    /// <summary>
    /// Our push-subscription id, assigned by Strava at registration. Events
    /// carrying a different subscription_id are dropped (brief §1.3). 0 =
    /// not yet registered — the check is skipped so the first deploy can
    /// complete the chicken-and-egg registration handshake.
    /// </summary>
    public long WebhookSubscriptionId { get; init; }

    /// <summary>
    /// Activities longer than this get a synchronous detail fetch (hr zones,
    /// splits, laps) at ingest; shorter ones keep the summary only. Brief §1.5.
    /// </summary>
    public int DetailFetchMinSeconds { get; init; } = 1200;
}

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

    /// <summary>
    /// Shared token echoed back by Strava's webhook verify-challenge GET
    /// (hub.verify_token). Chosen by us at subscription registration time.
    /// </summary>
    public string WebhookVerifyToken { get; init; } = string.Empty;

    /// <summary>
    /// Activities longer than this get a synchronous detail fetch (hr zones,
    /// splits, laps) at ingest; shorter ones keep the summary only. Brief §1.5.
    /// </summary>
    public int DetailFetchMinSeconds { get; init; } = 1200;
}

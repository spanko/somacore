namespace SomaCore.Domain.OAuthAudit;

public static class OAuthAuditSource
{
    public const string Whoop = "whoop";
    public const string Oura = "oura";
    public const string Strava = "strava";
    public const string AppleHealth = "apple_health";

    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        Whoop,
        Oura,
        Strava,
        AppleHealth,
    };
}

namespace SomaCore.Domain.ExternalConnections;

public static class ConnectionSource
{
    public const string Whoop = "whoop";
    public const string Oura = "oura";
    public const string Strava = "strava";
    public const string AppleHealth = "apple_health";
    public const string Manual = "manual";

    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        Whoop,
        Oura,
        Strava,
        AppleHealth,
        Manual,
    };
}

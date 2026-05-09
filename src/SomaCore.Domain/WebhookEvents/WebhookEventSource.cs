namespace SomaCore.Domain.WebhookEvents;

public static class WebhookEventSource
{
    public const string Whoop = "whoop";
    public const string Oura = "oura";
    public const string Strava = "strava";

    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        Whoop,
        Oura,
        Strava,
    };
}

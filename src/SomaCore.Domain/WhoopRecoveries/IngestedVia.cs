namespace SomaCore.Domain.WhoopRecoveries;

public static class IngestedVia
{
    public const string Webhook = "webhook";
    public const string Poller = "poller";
    public const string OnOpenPull = "on_open_pull";

    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        Webhook,
        Poller,
        OnOpenPull,
    };
}

namespace SomaCore.Domain.WhoopRecoveries;

public static class IngestedVia
{
    public const string Webhook = "webhook";
    public const string Poller = "poller";
    public const string OnOpenPull = "on_open_pull";
    /// <summary>Session 5 backfill — on-demand historical reconciliation, admin-triggered.</summary>
    public const string Backfill = "backfill";

    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        Webhook,
        Poller,
        OnOpenPull,
        Backfill,
    };
}

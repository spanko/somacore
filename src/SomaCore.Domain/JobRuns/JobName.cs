namespace SomaCore.Domain.JobRuns;

public static class JobName
{
    public const string ReconciliationPoller = "reconciliation-poller";
    public const string TokenRefreshSweeper = "token-refresh-sweeper";
    public const string StravaReconciliationPoller = "strava-reconciliation-poller";

    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        ReconciliationPoller,
        TokenRefreshSweeper,
        StravaReconciliationPoller,
    };
}

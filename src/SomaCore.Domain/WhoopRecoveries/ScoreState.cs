namespace SomaCore.Domain.WhoopRecoveries;

public static class ScoreState
{
    public const string Scored = "SCORED";
    public const string PendingScore = "PENDING_SCORE";
    public const string Unscorable = "UNSCORABLE";

    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        Scored,
        PendingScore,
        Unscorable,
    };
}

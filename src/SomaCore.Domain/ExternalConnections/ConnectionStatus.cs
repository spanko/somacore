namespace SomaCore.Domain.ExternalConnections;

public static class ConnectionStatus
{
    public const string Active = "active";
    public const string Revoked = "revoked";
    public const string RefreshFailed = "refresh_failed";
    public const string PendingAuthorization = "pending_authorization";

    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        Active,
        Revoked,
        RefreshFailed,
        PendingAuthorization,
    };
}

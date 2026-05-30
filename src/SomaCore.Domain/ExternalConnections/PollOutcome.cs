namespace SomaCore.Domain.ExternalConnections;

/// <summary>
/// Vocabulary for <c>external_connections.last_poll_outcome</c>. Tracks the
/// most recent reconciliation poller decision for a connection, for
/// dashboarding and gating diagnostics. Backed by a CHECK constraint in the
/// schema (per <c>docs/schema/0003_connection_polling_state.sql</c>).
/// </summary>
public static class PollOutcome
{
    /// <summary>Gating decided not to poll this connection (too-recent, stop-condition, or outside wake window).</summary>
    public const string Skipped = "Skipped";

    /// <summary>The poller ran fan-out for this connection and succeeded.</summary>
    public const string Polled = "Polled";

    /// <summary>The poller ran fan-out for this connection and an error occurred.</summary>
    public const string Failed = "Failed";

    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        Skipped,
        Polled,
        Failed,
    };
}

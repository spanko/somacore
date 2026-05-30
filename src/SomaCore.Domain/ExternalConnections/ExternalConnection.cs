using System.Text.Json;

using SomaCore.Domain.Common;
using SomaCore.Domain.Users;
using SomaCore.Domain.WhoopRecoveries;

namespace SomaCore.Domain.ExternalConnections;

public class ExternalConnection : IHasTimestamps
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Source { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string[] Scopes { get; set; } = Array.Empty<string>();

    public string KeyVaultSecretName { get; set; } = string.Empty;

    public DateTimeOffset? LastRefreshAt { get; set; }

    public DateTimeOffset? NextRefreshAt { get; set; }

    public int RefreshFailureCount { get; set; }

    public string? LastRefreshError { get; set; }

    /// <summary>
    /// Set at the end of every poller pass for this connection, whether work
    /// was done or not. Used by <c>PollerGating</c> for the too-recent skip
    /// check and as the timestamp on dashboards. See ADR 0011 / phase-2
    /// session 4.5 (adaptive poller gating).
    /// </summary>
    public DateTimeOffset? LastPolledAt { get; set; }

    /// <summary>
    /// Last decision the reconciliation poller made for this connection.
    /// One of <see cref="PollOutcome.Skipped"/> / <see cref="PollOutcome.Polled"/>
    /// / <see cref="PollOutcome.Failed"/>. Null until the first tick. Backed
    /// by a CHECK constraint in the schema.
    /// </summary>
    public string? LastPollOutcome { get; set; }

    public JsonDocument ConnectionMetadata { get; set; } = JsonDocument.Parse("{}");

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User? User { get; set; }

    public ICollection<WhoopRecovery> WhoopRecoveries { get; set; } = new List<WhoopRecovery>();
}

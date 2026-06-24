using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.WhoopRecoveries;

namespace SomaCore.Domain.Users;

public class User : IHasTimestamps
{
    public Guid Id { get; set; }

    public Guid EntraOid { get; set; }

    public Guid EntraTenantId { get; set; }

    public string Email { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>
    /// Per ADR 0012, the daily-card agent rolls out as a per-user opt-in,
    /// not a flag day. When false, /me renders the stub card; when true,
    /// the router invokes the network-backed implementation. Default false
    /// at row creation; flipped manually (SQL or future admin UI) once
    /// the user has consented and the privacy review has cleared.
    /// </summary>
    public bool AgentOptIn { get; set; }

    public ICollection<ExternalConnection> ExternalConnections { get; set; } = new List<ExternalConnection>();

    public ICollection<WhoopRecovery> WhoopRecoveries { get; set; } = new List<WhoopRecovery>();
}

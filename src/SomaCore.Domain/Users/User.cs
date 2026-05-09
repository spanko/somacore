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

    public ICollection<ExternalConnection> ExternalConnections { get; set; } = new List<ExternalConnection>();

    public ICollection<WhoopRecovery> WhoopRecoveries { get; set; } = new List<WhoopRecovery>();
}

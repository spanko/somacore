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

    public JsonDocument ConnectionMetadata { get; set; } = JsonDocument.Parse("{}");

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User? User { get; set; }

    public ICollection<WhoopRecovery> WhoopRecoveries { get; set; } = new List<WhoopRecovery>();
}

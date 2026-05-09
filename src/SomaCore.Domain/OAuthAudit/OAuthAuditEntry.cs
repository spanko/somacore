using System.Text.Json;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.Users;

namespace SomaCore.Domain.OAuthAudit;

public class OAuthAuditEntry
{
    public Guid Id { get; set; }

    public Guid? UserId { get; set; }

    public Guid? ExternalConnectionId { get; set; }

    public string Source { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public bool Success { get; set; }

    public int? HttpStatusCode { get; set; }

    public string? ErrorMessage { get; set; }

    public JsonDocument Context { get; set; } = JsonDocument.Parse("{}");

    public DateTimeOffset OccurredAt { get; set; }

    public User? User { get; set; }

    public ExternalConnection? ExternalConnection { get; set; }
}

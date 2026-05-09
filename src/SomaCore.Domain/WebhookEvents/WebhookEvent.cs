using System.Text.Json;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.Users;

namespace SomaCore.Domain.WebhookEvents;

public class WebhookEvent : IHasTimestamps
{
    public Guid Id { get; set; }

    public string Source { get; set; } = string.Empty;

    public string SourceEventId { get; set; } = string.Empty;

    public string SourceTraceId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public Guid? UserId { get; set; }

    public Guid? ExternalConnectionId { get; set; }

    public string Status { get; set; } = WebhookEventStatus.Received;

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset? ProcessingStartedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int ProcessingAttempts { get; set; }

    public string? LastError { get; set; }

    public JsonDocument RawBody { get; set; } = null!;

    public string SignatureHeader { get; set; } = string.Empty;

    public string SignatureTimestampHeader { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User? User { get; set; }

    public ExternalConnection? ExternalConnection { get; set; }
}

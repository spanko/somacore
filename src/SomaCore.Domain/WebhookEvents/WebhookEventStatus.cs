namespace SomaCore.Domain.WebhookEvents;

public static class WebhookEventStatus
{
    public const string Received = "received";
    public const string Processing = "processing";
    public const string Processed = "processed";
    public const string Failed = "failed";
    public const string Discarded = "discarded";

    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        Received,
        Processing,
        Processed,
        Failed,
        Discarded,
    };
}

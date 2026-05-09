using System.Text.Json;

using FluentAssertions;

using SomaCore.Domain.WebhookEvents;

namespace SomaCore.UnitTests.Domain;

public class WebhookEventsTests
{
    [Fact]
    public void Should_define_webhook_constants_and_default_status_to_received()
    {
        WebhookEventSource.All.Should().BeEquivalentTo(new[] { "whoop", "oura", "strava" });
        WebhookEventStatus.All.Should().BeEquivalentTo(new[]
        {
            "received", "processing", "processed", "failed", "discarded",
        });

        var evt = new WebhookEvent
        {
            Source = WebhookEventSource.Whoop,
            SourceEventId = "sleep-uuid-1",
            SourceTraceId = "trace-1",
            EventType = "recovery.updated",
            RawBody = JsonDocument.Parse("""{"id":"x"}"""),
            SignatureHeader = "sig",
            SignatureTimestampHeader = "1700000000",
        };

        evt.Status.Should().Be("received");
        evt.ProcessingAttempts.Should().Be(0);
    }
}

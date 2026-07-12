using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.WebhookEvents;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Strava;

namespace SomaCore.Api.Strava;

/// <summary>
/// Strava webhook receiver (brief §1.3). GET answers the subscription
/// verify-challenge; POST enqueues to webhook_events and returns 200 fast —
/// Strava requires the ack within 2 seconds, so the receiver does ZERO
/// Strava API calls; all ingest work happens in <see cref="StravaWebhookDrainer"/>.
/// Strava does not sign webhook bodies (no HMAC counterpart to WHOOP's):
/// authenticity rests on the verify-token handshake plus the per-event
/// subscription_id check.
/// </summary>
public static class StravaWebhookEndpoint
{
    public static IEndpointRouteBuilder MapStravaWebhookEndpoint(this IEndpointRouteBuilder app)
    {
        // Anonymous: Strava's servers call these — Entra cookies don't apply.
        app.MapGet("/webhooks/strava", HandleChallengeAsync).AllowAnonymous();
        app.MapPost("/webhooks/strava", HandleEventAsync).AllowAnonymous();
        return app;
    }

    internal static Task<IResult> HandleChallengeAsync(
        HttpContext httpContext,
        IOptions<StravaOptions> options,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Strava.Webhook.Challenge");

        if (!options.Value.Enabled)
        {
            return Task.FromResult(Results.NotFound());
        }

        var query = httpContext.Request.Query;
        var mode = query["hub.mode"].ToString();
        var verifyToken = query["hub.verify_token"].ToString();
        var challenge = query["hub.challenge"].ToString();

        // Never echo the challenge unless the token matches the one we chose
        // at subscription registration. An empty configured token can never
        // match — a half-configured environment fails closed.
        if (mode != "subscribe"
            || string.IsNullOrEmpty(options.Value.WebhookVerifyToken)
            || !string.Equals(verifyToken, options.Value.WebhookVerifyToken, StringComparison.Ordinal))
        {
            logger.LogWarning("Rejecting verify-challenge: mode={Mode}, token mismatch", mode);
            return Task.FromResult(Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        logger.LogInformation("Answering Strava webhook verify-challenge");
        // Strava expects the challenge echoed under this exact key.
        return Task.FromResult(Results.Json(new Dictionary<string, string>
        {
            ["hub.challenge"] = challenge,
        }));
    }

    internal static async Task<IResult> HandleEventAsync(
        HttpContext httpContext,
        SomaCoreDbContext dbContext,
        IOptions<StravaOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Strava.Webhook");

        if (!options.Value.Enabled)
        {
            return Results.NotFound();
        }

        // Cap at 64 KiB, same as the WHOOP receiver; Strava events are tiny.
        await using var ms = new MemoryStream();
        await httpContext.Request.Body.CopyToAsync(ms, cancellationToken);
        var rawBody = ms.ToArray();

        if (rawBody.Length > 64 * 1024)
        {
            logger.LogWarning("Rejecting webhook: body too large ({Bytes} bytes)", rawBody.Length);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        StravaWebhookEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<StravaWebhookEnvelope>(rawBody);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Webhook JSON parse failed");
            return Results.BadRequest("Malformed JSON.");
        }

        if (envelope is null
            || string.IsNullOrEmpty(envelope.ObjectType)
            || string.IsNullOrEmpty(envelope.AspectType)
            || envelope.ObjectId == 0)
        {
            logger.LogWarning("Webhook missing required fields");
            return Results.BadRequest("Missing required fields.");
        }

        // Brief §1.3: verify subscription_id matches ours. 0 = not yet
        // configured (pre-registration) — skip. On mismatch, ack with 200 but
        // record nothing: a non-2xx would make Strava retry a forged/foreign
        // event forever.
        if (options.Value.WebhookSubscriptionId != 0
            && envelope.SubscriptionId != options.Value.WebhookSubscriptionId)
        {
            logger.LogWarning(
                "Dropping webhook with foreign subscription_id {SubscriptionId}",
                envelope.SubscriptionId);
            return Results.Ok();
        }

        var eventType = $"{envelope.ObjectType}.{envelope.AspectType}".ToLowerInvariant();
        var status = IsSupportedEventType(eventType)
            ? WebhookEventStatus.Received
            : WebhookEventStatus.Discarded;

        // Map owner_id (the athlete) to a SomaCore user + connection if known.
        // Best-effort: an event for an unknown athlete still gets recorded.
        var matcher = JsonDocument.Parse($"{{\"strava_athlete_id\":{envelope.OwnerId}}}");
        var connection = await dbContext.ExternalConnections
            .AsNoTracking()
            .Where(c => c.Source == ConnectionSource.Strava
                     && c.Status == ConnectionStatus.Active
                     && EF.Functions.JsonContains(c.ConnectionMetadata, matcher))
            .Select(c => new { c.Id, c.UserId })
            .FirstOrDefaultAsync(cancellationToken);

        // Strava has no per-event id or trace id. The brief's dedupe key
        // (subscription_id, object_id, aspect_type, event_time) is composed
        // into source_event_id so the existing unique index
        // (source, source_event_id, source_trace_id) enforces it.
        var dedupeKey =
            $"{envelope.SubscriptionId}:{envelope.ObjectId}:{envelope.AspectType}:{envelope.EventTimeEpoch}";

        var evt = new WebhookEvent
        {
            Source = WebhookEventSource.Strava,
            SourceEventId = dedupeKey,
            SourceTraceId = dedupeKey,
            EventType = eventType,
            UserId = connection?.UserId,
            ExternalConnectionId = connection?.Id,
            Status = status,
            ReceivedAt = DateTimeOffset.UtcNow,
            RawBody = JsonDocument.Parse(rawBody),
            SignatureHeader = string.Empty,          // Strava does not sign webhooks
            SignatureTimestampHeader = string.Empty,
        };

        try
        {
            dbContext.WebhookEvents.Add(evt);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            logger.LogInformation("Duplicate webhook ignored: {DedupeKey}", dedupeKey);
            return Results.Ok();
        }

        logger.LogInformation(
            "Webhook accepted: type={EventType} object_id={ObjectId} status={Status}",
            eventType,
            envelope.ObjectId,
            status);
        return Results.Ok();
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505";

    private static bool IsSupportedEventType(string eventType)
        => eventType is "activity.create" or "activity.update" or "activity.delete" or "athlete.update";
}

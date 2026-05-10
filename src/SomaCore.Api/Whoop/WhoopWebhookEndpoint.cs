using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.WebhookEvents;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Whoop;

namespace SomaCore.Api.Whoop;

public static class WhoopWebhookEndpoint
{
    public const string SignatureHeader  = "X-WHOOP-Signature";
    public const string TimestampHeader  = "X-WHOOP-Signature-Timestamp";

    public static IEndpointRouteBuilder MapWhoopWebhookEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/whoop", HandleAsync)
            .AllowAnonymous(); // HMAC signature is the auth — Entra cookies don't apply here.
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        IWhoopWebhookSignatureValidator validator,
        SomaCoreDbContext dbContext,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Whoop.Webhook");

        // Read raw body so we can HMAC it byte-for-byte. Cap at 64 KiB; the WHOOP
        // payload is tiny (well under 1 KiB), anything larger is malformed/abusive.
        httpContext.Request.EnableBuffering();
        await using var ms = new MemoryStream();
        await httpContext.Request.Body.CopyToAsync(ms, cancellationToken);
        var rawBody = ms.ToArray();
        httpContext.Request.Body.Position = 0;

        if (rawBody.Length > 64 * 1024)
        {
            logger.LogWarning("Rejecting webhook: body too large ({Bytes} bytes)", rawBody.Length);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var signature = httpContext.Request.Headers[SignatureHeader].ToString();
        var timestamp = httpContext.Request.Headers[TimestampHeader].ToString();

        var validation = validator.Validate(signature, timestamp, rawBody);
        if (!validation.IsSuccess)
        {
            // 401 (not 400) tells WHOOP not to keep retrying with the same signature.
            logger.LogWarning("Rejecting webhook: {Reason}", validation.Error);
            return Results.Unauthorized();
        }

        WhoopWebhookEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<WhoopWebhookEnvelope>(rawBody);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Webhook JSON parse failed");
            return Results.BadRequest("Malformed JSON.");
        }

        if (envelope is null
            || string.IsNullOrEmpty(envelope.EventType)
            || string.IsNullOrEmpty(envelope.Id)
            || string.IsNullOrEmpty(envelope.TraceId))
        {
            logger.LogWarning("Webhook missing required fields");
            return Results.BadRequest("Missing required fields.");
        }

        // Phase 1 only acts on recovery events; anything else gets a 200 + 'discarded' row.
        var status = envelope.EventType.Equals("recovery.updated", StringComparison.OrdinalIgnoreCase)
            ? WebhookEventStatus.Received
            : WebhookEventStatus.Discarded;

        // Map the WHOOP user_id (numeric) to a SomaCore user + connection if we know them.
        // Best-effort: a webhook for an unknown user still gets recorded for audit.
        var matcher = JsonDocument.Parse($"{{\"whoop_user_id\":{envelope.UserId}}}");
        var connection = await dbContext.ExternalConnections
            .AsNoTracking()
            .Where(c => c.Source == ConnectionSource.Whoop
                     && c.Status == ConnectionStatus.Active
                     && EF.Functions.JsonContains(c.ConnectionMetadata, matcher))
            .Select(c => new { c.Id, c.UserId })
            .FirstOrDefaultAsync(cancellationToken);

        var rawDoc = JsonDocument.Parse(rawBody);
        var evt = new WebhookEvent
        {
            Source = WebhookEventSource.Whoop,
            SourceEventId = envelope.Id,
            SourceTraceId = envelope.TraceId,
            EventType = envelope.EventType,
            UserId = connection?.UserId,
            ExternalConnectionId = connection?.Id,
            Status = status,
            ReceivedAt = DateTimeOffset.UtcNow,
            RawBody = rawDoc,
            SignatureHeader = signature,
            SignatureTimestampHeader = timestamp,
        };

        try
        {
            dbContext.WebhookEvents.Add(evt);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Duplicate delivery (same source/event_id/trace_id) — already recorded. 200 + done.
            logger.LogInformation(
                "Duplicate webhook ignored: event_id={EventId} trace_id={TraceId}",
                envelope.Id,
                envelope.TraceId);
            return Results.Ok();
        }

        logger.LogInformation(
            "Webhook accepted: type={EventType} event_id={EventId} trace_id={TraceId} status={Status}",
            envelope.EventType,
            envelope.Id,
            envelope.TraceId,
            status);
        return Results.Ok();
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505";
}

using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.OAuthAudit;
using SomaCore.Domain.WebhookEvents;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Secrets;
using SomaCore.Infrastructure.Strava;

namespace SomaCore.Api.Strava;

/// <summary>
/// Background drainer for Strava rows in the shared <c>webhook_events</c>
/// queue (ADR 0009) — same claim-then-process shape as
/// <see cref="Whoop.WhoopWebhookDrainer"/>, filtered to <c>source='strava'</c>
/// (each source drains its own rows; the WHOOP drainer filters likewise).
///
/// Routing (brief §1.3):
///   - <c>activity.create</c> / <c>activity.update</c> → fetch+upsert via
///     <see cref="IStravaActivityIngestService"/> (the S4 seam).
///   - <c>activity.delete</c> → soft delete (<c>deleted_at</c>), never a row delete.
///   - <c>athlete.update</c> with <c>authorized=false</c> → mark connection
///     revoked, purge the KV refresh token, write a revoke_detected audit row.
///     strava_activities rows are NOT touched — history survives deauth.
/// </summary>
public sealed class StravaWebhookDrainer(
    IServiceScopeFactory scopeFactory,
    ILogger<StravaWebhookDrainer> logger)
    : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(10);
    private const int BatchSize = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Strava webhook drainer starting (batch size = {BatchSize})", BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            int processed;
            try
            {
                processed = await DrainOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Strava drainer iteration failed; backing off");
                await Task.Delay(ErrorDelay, stoppingToken);
                continue;
            }

            if (processed == 0)
            {
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }

        logger.LogInformation("Strava webhook drainer stopping");
    }

    private async Task<int> DrainOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();

        await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var claimed = await dbContext.WebhookEvents
            .FromSqlRaw(
                """
                SELECT * FROM webhook_events
                WHERE status = 'received' AND source = 'strava'
                ORDER BY received_at
                LIMIT {0}
                FOR UPDATE SKIP LOCKED
                """,
                BatchSize)
            .ToListAsync(cancellationToken);

        if (claimed.Count == 0)
        {
            await tx.CommitAsync(cancellationToken);
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var evt in claimed)
        {
            evt.Status = WebhookEventStatus.Processing;
            evt.ProcessingStartedAt = now;
            evt.ProcessingAttempts += 1;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        foreach (var evt in claimed)
        {
            await ProcessOneAsync(scope.ServiceProvider, evt, cancellationToken);
        }

        return claimed.Count;
    }

    /// <summary>
    /// Process a single claimed event end-to-end. Exposed as <c>internal</c>
    /// so integration tests drive it directly, per the scaffolding decision
    /// documented on the WHOOP drainer's tests.
    /// </summary>
    internal async Task ProcessOneAsync(
        IServiceProvider services,
        WebhookEvent evt,
        CancellationToken cancellationToken)
    {
        var dbContext = services.GetRequiredService<SomaCoreDbContext>();
        var refreshed = await dbContext.WebhookEvents
            .FirstAsync(e => e.Id == evt.Id, cancellationToken);

        if (refreshed.ExternalConnectionId is null)
        {
            refreshed.Status = WebhookEventStatus.Discarded;
            refreshed.LastError = "No external_connection mapped for this Strava owner_id";
            refreshed.ProcessedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Discarding Strava webhook {EventId}: no connection mapped", refreshed.Id);
            return;
        }

        StravaWebhookEnvelope? envelope;
        try
        {
            envelope = refreshed.RawBody.Deserialize<StravaWebhookEnvelope>();
        }
        catch (JsonException)
        {
            envelope = null;
        }

        if (envelope is null)
        {
            refreshed.Status = WebhookEventStatus.Discarded;
            refreshed.LastError = "Raw body did not deserialize to a Strava webhook envelope";
            refreshed.ProcessedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        using var rootSpan = IngestionTracing.StartIngestionScope(
            source: "strava",
            trigger: "webhook",
            eventType: refreshed.EventType,
            externalConnectionId: refreshed.ExternalConnectionId.Value,
            upstreamTraceId: refreshed.SourceTraceId);
        IngestionTracing.RecordOutcome(rootSpan, "activity", IngestionTracing.Outcomes.NotInvoked);

        try
        {
            var result = refreshed.EventType switch
            {
                "activity.create" or "activity.update" => await IngestActivityAsync(
                    services, rootSpan, refreshed, envelope, cancellationToken),
                "activity.delete" => await SoftDeleteActivityAsync(
                    dbContext, rootSpan, envelope, cancellationToken),
                "athlete.update" => await HandleAthleteUpdateAsync(
                    services, dbContext, refreshed, envelope, cancellationToken),
                _ => Result<string>.Failure($"Unsupported event type: {refreshed.EventType}"),
            };

            if (result.IsSuccess)
            {
                refreshed.Status = WebhookEventStatus.Processed;
                refreshed.ProcessedAt = DateTimeOffset.UtcNow;
                refreshed.LastError = null;
            }
            else
            {
                refreshed.Status = WebhookEventStatus.Failed;
                refreshed.ProcessedAt = DateTimeOffset.UtcNow;
                refreshed.LastError = Truncate(result.Error, 2000);
                logger.LogWarning(
                    "Strava webhook {EventId} processing failed: {Error}",
                    refreshed.Id, result.Error);
            }
        }
        catch (Exception ex)
        {
            refreshed.Status = WebhookEventStatus.Failed;
            refreshed.ProcessedAt = DateTimeOffset.UtcNow;
            refreshed.LastError = Truncate($"{ex.GetType().Name}: {ex.Message}", 2000);
            logger.LogError(ex, "Strava webhook {EventId} processing threw", refreshed.Id);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Result<string>> IngestActivityAsync(
        IServiceProvider services,
        System.Diagnostics.Activity? rootSpan,
        WebhookEvent evt,
        StravaWebhookEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var ingest = services.GetRequiredService<IStravaActivityIngestService>();
        var result = await ingest.IngestAsync(
            evt.ExternalConnectionId!.Value,
            envelope.ObjectId,
            ingestedVia: "webhook",
            traceId: evt.SourceTraceId,
            cancellationToken);

        if (!result.IsSuccess)
        {
            return Result<string>.Failure(result.Error!);
        }

        IngestionTracing.RecordOutcome(rootSpan, "activity", result.Value!.Outcome);
        return Result<string>.Success(result.Value.Outcome);
    }

    private async Task<Result<string>> SoftDeleteActivityAsync(
        SomaCoreDbContext dbContext,
        System.Diagnostics.Activity? rootSpan,
        StravaWebhookEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var activity = await dbContext.StravaActivities
            .FirstOrDefaultAsync(
                a => a.StravaActivityId == envelope.ObjectId && a.DeletedAt == null,
                cancellationToken);

        if (activity is null)
        {
            // Never ingested (or already soft-deleted) — nothing to do.
            IngestionTracing.RecordOutcome(rootSpan, "activity", IngestionTracing.Outcomes.NoOp);
            return Result<string>.Success(IngestionTracing.Outcomes.NoOp);
        }

        activity.DeletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Soft-deleted Strava activity {StravaActivityId} per delete webhook",
            envelope.ObjectId);
        IngestionTracing.RecordOutcome(rootSpan, "activity", IngestionTracing.Outcomes.Updated);
        return Result<string>.Success(IngestionTracing.Outcomes.Updated);
    }

    private async Task<Result<string>> HandleAthleteUpdateAsync(
        IServiceProvider services,
        SomaCoreDbContext dbContext,
        WebhookEvent evt,
        StravaWebhookEnvelope envelope,
        CancellationToken cancellationToken)
    {
        // The only athlete.update we act on is deauthorization.
        if (envelope.Updates is null
            || !envelope.Updates.TryGetValue("authorized", out var authorized)
            || !string.Equals(authorized, "false", StringComparison.OrdinalIgnoreCase))
        {
            return Result<string>.Success(IngestionTracing.Outcomes.NoOp);
        }

        var connection = await dbContext.ExternalConnections
            .FirstOrDefaultAsync(c => c.Id == evt.ExternalConnectionId, cancellationToken);
        if (connection is null)
        {
            return Result<string>.Failure("Connection row vanished before deauth processing.");
        }

        connection.Status = ConnectionStatus.Revoked;

        dbContext.OAuthAuditEntries.Add(new OAuthAuditEntry
        {
            UserId = connection.UserId,
            ExternalConnectionId = connection.Id,
            Source = OAuthAuditSource.Strava,
            Action = OAuthAuditAction.RevokeDetected,
            Success = true,
            Context = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                reason = "athlete deauthorize webhook",
                strava_athlete_id = envelope.OwnerId,
            })),
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        // Purge the now-useless refresh token. Best-effort — the connection
        // is revoked either way, and no code path reads the secret afterward.
        // strava_activities rows are deliberately NOT touched: deauth revokes
        // future access, it does not erase the user's history.
        var kv = services.GetRequiredService<IKeyVaultSecretsClient>();
        var kvDeleted = await kv.TryDeleteSecretAsync(connection.KeyVaultSecretName, cancellationToken);
        if (!kvDeleted)
        {
            logger.LogWarning(
                "Failed to delete Key Vault secret {SecretName} after Strava deauth for connection {ConnectionId}",
                connection.KeyVaultSecretName,
                connection.Id);
        }

        logger.LogInformation(
            "Strava deauth processed for connection {ConnectionId} (kv_deleted={KvDeleted})",
            connection.Id,
            kvDeleted);
        return Result<string>.Success("Revoked");
    }

    private static string? Truncate(string? s, int max)
        => s is null ? null : s.Length <= max ? s : s[..max];
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SomaCore.Domain.WebhookEvents;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Recovery;

namespace SomaCore.Api.Whoop;

/// <summary>
/// Background drainer for the Postgres-backed work queue (per ADR 0009). Polls
/// `webhook_events` rows in 'received' status, claims them via SELECT ... FOR
/// UPDATE SKIP LOCKED, calls the shared <see cref="IRecoveryIngestionHandler"/>,
/// and marks each row 'processed' or 'failed'. Replace with Service Bus when
/// volume / fan-out demands it.
/// </summary>
public sealed class WhoopWebhookDrainer(
    IServiceScopeFactory scopeFactory,
    ILogger<WhoopWebhookDrainer> logger)
    : BackgroundService
{
    private static readonly TimeSpan IdleDelay  = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(10);
    private const int BatchSize = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Webhook drainer starting (batch size = {BatchSize})", BatchSize);

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
                logger.LogError(ex, "Drainer iteration failed; backing off");
                await Task.Delay(ErrorDelay, stoppingToken);
                continue;
            }

            if (processed == 0)
            {
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }

        logger.LogInformation("Webhook drainer stopping");
    }

    private async Task<int> DrainOnceAsync(CancellationToken cancellationToken)
    {
        // Each batch runs in its own scope so DbContext lifetimes are bounded.
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();
        var handler = scope.ServiceProvider.GetRequiredService<IRecoveryIngestionHandler>();

        await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Claim a batch of pending rows. SKIP LOCKED keeps multiple drainer
        // replicas (or future fan-out) from stepping on each other.
        var claimed = await dbContext.WebhookEvents
            .FromSqlRaw(
                """
                SELECT * FROM webhook_events
                WHERE status = 'received'
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

        // Process each claimed row outside the lock-holding transaction.
        foreach (var evt in claimed)
        {
            await ProcessOneAsync(scope.ServiceProvider, evt, handler, cancellationToken);
        }

        return claimed.Count;
    }

    private async Task ProcessOneAsync(
        IServiceProvider services,
        WebhookEvent evt,
        IRecoveryIngestionHandler handler,
        CancellationToken cancellationToken)
    {
        var dbContext = services.GetRequiredService<SomaCoreDbContext>();
        var refreshed = await dbContext.WebhookEvents
            .FirstAsync(e => e.Id == evt.Id, cancellationToken);

        try
        {
            if (refreshed.ExternalConnectionId is null)
            {
                refreshed.Status = WebhookEventStatus.Discarded;
                refreshed.LastError = "No external_connection mapped for this WHOOP user_id";
                refreshed.ProcessedAt = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync(cancellationToken);
                logger.LogWarning(
                    "Discarding webhook {EventId}: no connection mapped",
                    refreshed.Id);
                return;
            }

            Guid? sleepId = null;
            if (Guid.TryParse(refreshed.SourceEventId, out var parsedSleep))
            {
                sleepId = parsedSleep;
            }

            var request = new RecoveryIngestionRequest(
                ExternalConnectionId: refreshed.ExternalConnectionId.Value,
                IngestedVia: IngestedVia.Webhook,
                CycleId: null,
                SleepId: sleepId,
                TraceId: refreshed.SourceTraceId);

            var result = await handler.IngestAsync(request, cancellationToken);

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
                    "Webhook {EventId} processing failed: {Error}",
                    refreshed.Id,
                    result.Error);
            }
        }
        catch (Exception ex)
        {
            refreshed.Status = WebhookEventStatus.Failed;
            refreshed.ProcessedAt = DateTimeOffset.UtcNow;
            refreshed.LastError = Truncate($"{ex.GetType().Name}: {ex.Message}", 2000);
            logger.LogError(ex, "Webhook {EventId} processing threw", refreshed.Id);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string? Truncate(string? s, int max)
        => s is null ? null : s.Length <= max ? s : s[..max];
}

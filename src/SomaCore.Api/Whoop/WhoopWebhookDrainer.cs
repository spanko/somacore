using System.Diagnostics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SomaCore.Domain.Common;
using SomaCore.Domain.WebhookEvents;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Recovery;
using SomaCore.Infrastructure.Sleep;
using SomaCore.Infrastructure.Whoop;
using SomaCore.Infrastructure.Workout;

namespace SomaCore.Api.Whoop;

/// <summary>
/// Background drainer for the Postgres-backed work queue (per ADR 0009). Polls
/// <c>webhook_events</c> rows in 'received' status, claims them via
/// <c>SELECT ... FOR UPDATE SKIP LOCKED</c>, and dispatches to ingestion
/// handlers based on event type.
///
/// Two dispatch paths:
///   - Cycle events (<c>recovery.updated</c>, <c>sleep.updated</c>) → resolve
///     cycle id once via list-recent-recoveries, then invoke recovery + sleep
///     handlers with the explicit cycle id. A missed delivery of one event
///     type is recovered by the other.
///   - Workout events (<c>workout.updated</c>) → no fan-out, no cycle
///     resolution. Invoke the workout handler directly with the WHOOP workout
///     id from the webhook envelope.
///
/// Per ADR 0011 the orchestrator pre-seeds all three outcome rollup tags
/// (<c>outcomes.recovery</c> / <c>outcomes.sleep</c> / <c>outcomes.workout</c>)
/// to <c>NotInvoked</c> on the root span before fan-out runs; handlers
/// overwrite their own tag when they execute. This keeps Application Insights
/// queries uniform across event types.
/// </summary>
public sealed class WhoopWebhookDrainer(
    IServiceScopeFactory scopeFactory,
    ILogger<WhoopWebhookDrainer> logger)
    : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(10);
    private const int BatchSize = 5;

    // Event types we know how to handle. The webhook receiver already filters
    // at the boundary; this is a defensive double-check before dispatch.
    private static readonly HashSet<string> CycleEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "recovery.updated",
        "sleep.updated",
    };
    private const string WorkoutEventType = "workout.updated";

    private static bool IsSupportedEventType(string eventType)
        => CycleEventTypes.Contains(eventType)
        || eventType.Equals(WorkoutEventType, StringComparison.OrdinalIgnoreCase);

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
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();

        await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var claimed = await dbContext.WebhookEvents
            .FromSqlRaw(
                """
                SELECT * FROM webhook_events
                WHERE status = 'received' AND source = 'whoop'
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
    /// Process a single claimed webhook event end-to-end: dispatch by event
    /// type, run the appropriate fan-out, update the row's status. Exposed
    /// as <c>internal</c> so integration tests can drive a webhook through
    /// the drainer without spinning up the BackgroundService lifecycle.
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
            refreshed.LastError = "No external_connection mapped for this WHOOP user_id";
            refreshed.ProcessedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Discarding webhook {EventId}: no connection mapped", refreshed.Id);
            return;
        }

        if (!IsSupportedEventType(refreshed.EventType))
        {
            refreshed.Status = WebhookEventStatus.Discarded;
            refreshed.LastError = $"Unsupported event type: {refreshed.EventType}";
            refreshed.ProcessedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Discarding webhook {EventId} type={EventType}: not yet supported",
                refreshed.Id, refreshed.EventType);
            return;
        }

        // ADR 0011 root span. Pre-seed all three rolled-up outcome tags to
        // NotInvoked so downstream KQL queries against any outcomes.* tag work
        // uniformly across event types. Handlers overwrite their own tag when
        // they execute; tags for handlers structurally out of scope for this
        // event type (e.g. workout webhook never invokes recovery/sleep) stay
        // at NotInvoked.
        using var rootSpan = IngestionTracing.StartIngestionScope(
            source: "whoop",
            trigger: "webhook",
            eventType: refreshed.EventType,
            externalConnectionId: refreshed.ExternalConnectionId.Value,
            upstreamTraceId: refreshed.SourceTraceId);
        IngestionTracing.RecordOutcome(rootSpan, "recovery", IngestionTracing.Outcomes.NotInvoked);
        IngestionTracing.RecordOutcome(rootSpan, "sleep", IngestionTracing.Outcomes.NotInvoked);
        IngestionTracing.RecordOutcome(rootSpan, "workout", IngestionTracing.Outcomes.NotInvoked);

        try
        {
            Guid? entityIdFromWebhook = null;
            if (Guid.TryParse(refreshed.SourceEventId, out var parsed))
            {
                entityIdFromWebhook = parsed;
            }

            var fanOutResult = CycleEventTypes.Contains(refreshed.EventType)
                ? await FanOutCycleAsync(
                    services,
                    rootSpan,
                    refreshed.ExternalConnectionId.Value,
                    refreshed.SourceTraceId,
                    sleepIdFromWebhook: entityIdFromWebhook,
                    cancellationToken)
                : await FanOutWorkoutAsync(
                    services,
                    rootSpan,
                    refreshed.ExternalConnectionId.Value,
                    refreshed.SourceTraceId,
                    workoutIdFromWebhook: entityIdFromWebhook,
                    cancellationToken);

            if (fanOutResult.IsSuccess)
            {
                refreshed.Status = WebhookEventStatus.Processed;
                refreshed.ProcessedAt = DateTimeOffset.UtcNow;
                refreshed.LastError = null;
            }
            else
            {
                refreshed.Status = WebhookEventStatus.Failed;
                refreshed.ProcessedAt = DateTimeOffset.UtcNow;
                refreshed.LastError = Truncate(fanOutResult.Error, 2000);
                logger.LogWarning(
                    "Webhook {EventId} processing failed: {Error}",
                    refreshed.Id, fanOutResult.Error);
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

    /// <summary>
    /// Cycle pull-and-fan-out. The drainer resolves the cycle id once (the
    /// upstream "cycle pull" from a tracing perspective), then invokes
    /// recovery and sleep handlers in sequence with an explicit
    /// <c>CycleId</c> so both take their fast cycle-keyed branches.
    /// </summary>
    private async Task<Result<int>> FanOutCycleAsync(
        IServiceProvider services,
        Activity? rootSpan,
        Guid externalConnectionId,
        string upstreamTraceId,
        Guid? sleepIdFromWebhook,
        CancellationToken cancellationToken)
    {
        var apiClient = services.GetRequiredService<IWhoopApiClient>();
        var tokenCache = services.GetRequiredService<IWhoopAccessTokenCache>();
        var recoveryHandler = services.GetRequiredService<IRecoveryIngestionHandler>();
        var sleepHandler = services.GetRequiredService<IWhoopSleepIngestionHandler>();

        using var fetchSpan = IngestionTracing.StartFetchScope(
            "whoop.cycle.fetch",
            naturalKey: sleepIdFromWebhook?.ToString());
        var fetchStart = Stopwatch.GetTimestamp();

        long? resolvedCycleId = null;
        if (sleepIdFromWebhook is Guid sleepId)
        {
            var token = await tokenCache.GetAccessTokenAsync(externalConnectionId, cancellationToken);
            if (!token.IsSuccess)
            {
                RecordFetch(fetchSpan, fetchStart, statusCode: null);
                // Handlers stay at the pre-seeded NotInvoked.
                return Result<int>.Failure($"Token acquisition failed: {token.Error}");
            }

            var listResult = await apiClient.ListRecentRecoveriesAsync(token.Value!, limit: 10, cancellationToken);
            if (!listResult.IsSuccess)
            {
                RecordFetch(fetchSpan, fetchStart, statusCode: null);
                return Result<int>.Failure(listResult.Error!);
            }
            var match = listResult.Value!.Records.FirstOrDefault(r => r.SleepId == sleepId);
            resolvedCycleId = match?.CycleId;
            if (resolvedCycleId is long cid)
            {
                fetchSpan?.SetTag(IngestionTracing.Tags.WhoopCycleId, cid.ToString());
            }
        }

        RecordFetch(fetchSpan, fetchStart, statusCode: 200);

        var recoveryRequest = new RecoveryIngestionRequest(
            ExternalConnectionId: externalConnectionId,
            IngestedVia: IngestedVia.Webhook,
            CycleId: resolvedCycleId,
            SleepId: sleepIdFromWebhook,
            TraceId: upstreamTraceId);
        var recoveryResult = await recoveryHandler.IngestAsync(recoveryRequest, cancellationToken);
        if (recoveryResult.IsSuccess && recoveryResult.Value!.CycleId is long viaRecovery)
        {
            resolvedCycleId ??= viaRecovery;
        }

        if (resolvedCycleId is null)
        {
            // No cycle id available even after recovery ran (SkippedNoData
            // path). Sleep can't proceed. Sleep outcome stays at NotInvoked.
            return recoveryResult.IsSuccess
                ? Result<int>.Success(0)
                : Result<int>.Failure($"recovery: {recoveryResult.Error}");
        }

        var sleepRequest = new SleepIngestionRequest(
            ExternalConnectionId: externalConnectionId,
            IngestedVia: IngestedVia.Webhook,
            CycleId: resolvedCycleId,
            SleepId: sleepIdFromWebhook,
            TraceId: upstreamTraceId);
        var sleepResult = await sleepHandler.IngestAsync(sleepRequest, cancellationToken);

        if (!recoveryResult.IsSuccess)
        {
            return Result<int>.Failure($"recovery: {recoveryResult.Error}");
        }
        if (!sleepResult.IsSuccess)
        {
            return Result<int>.Failure($"sleep: {sleepResult.Error}");
        }
        return Result<int>.Success(0);
    }

    /// <summary>
    /// Workout dispatch. Single handler, no fan-out. Workouts are not
    /// cycle-keyed; the WHOOP workout UUID arrives directly in the webhook
    /// envelope's <c>id</c> field. The fetch span wraps the single
    /// <c>/activity/workout/{id}</c> call.
    /// </summary>
    private async Task<Result<int>> FanOutWorkoutAsync(
        IServiceProvider services,
        Activity? rootSpan,
        Guid externalConnectionId,
        string upstreamTraceId,
        Guid? workoutIdFromWebhook,
        CancellationToken cancellationToken)
    {
        if (workoutIdFromWebhook is not Guid workoutId)
        {
            // Webhook envelope lacked a parseable workout UUID. Nothing to do.
            return Result<int>.Failure("workout.updated webhook missing parseable workout UUID");
        }

        var workoutHandler = services.GetRequiredService<IWhoopWorkoutIngestionHandler>();

        using var fetchSpan = IngestionTracing.StartFetchScope(
            "whoop.workout.fetch",
            naturalKey: workoutId.ToString());
        var fetchStart = Stopwatch.GetTimestamp();

        var request = new WorkoutIngestionRequest(
            ExternalConnectionId: externalConnectionId,
            IngestedVia: IngestedVia.Webhook,
            WorkoutId: workoutId,
            TraceId: upstreamTraceId);
        var result = await workoutHandler.IngestAsync(request, cancellationToken);

        // The handler's GetWorkoutByIdAsync call is the only fetch under this
        // span; we don't have an HTTP status code at this layer, so the fetch
        // outcome is just duration. (Auto-instrumentation can capture HTTP
        // details separately when enabled.)
        RecordFetch(fetchSpan, fetchStart, statusCode: null);

        return result.IsSuccess
            ? Result<int>.Success(0)
            : Result<int>.Failure($"workout: {result.Error}");
    }

    private static void RecordFetch(Activity? span, long startTimestamp, int? statusCode)
    {
        IngestionTracing.RecordFetchOutcome(
            span,
            httpStatusCode: statusCode,
            durationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
    }

    private static string? Truncate(string? s, int max)
        => s is null ? null : s.Length <= max ? s : s[..max];
}

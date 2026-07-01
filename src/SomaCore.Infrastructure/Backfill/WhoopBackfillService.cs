using System.Diagnostics;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.OAuthAudit;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Recovery;
using SomaCore.Infrastructure.Sleep;
using SomaCore.Infrastructure.Whoop;
using SomaCore.Infrastructure.Workout;

namespace SomaCore.Infrastructure.Backfill;

/// <summary>
/// Walks WHOOP's recovery, sleep, and workout list endpoints over a date
/// window and feeds each entity to the corresponding handler via the
/// <c>UpsertFromPayloadAsync</c> bypass (no per-id refetch). Per ADR 0011
/// Session 5 resolution: emits one <c>whoop.ingestion</c> trace root per
/// ingested entity with <c>ingestion.trigger=backfill</c>. Writes one row to
/// <c>oauth_audit</c> with <c>action='backfill'</c> on completion.
///
/// Idempotency: the handlers already dedupe on
/// (external_connection_id, natural_key); re-running the same window
/// produces <c>NoOp</c> outcomes for everything that already landed.
///
/// Failure policy: per-entity errors are counted + the first error surfaces
/// in the summary; the run continues past them so one bad workout doesn't
/// abort the whole window. Connection-level errors (no token, connection
/// not found, list-endpoint failed) abort and return Failure.
/// </summary>
public sealed class WhoopBackfillService(
    SomaCoreDbContext dbContext,
    IWhoopApiClient whoopApi,
    IWhoopAccessTokenCache tokens,
    IRecoveryIngestionHandler recoveryHandler,
    IWhoopSleepIngestionHandler sleepHandler,
    IWhoopWorkoutIngestionHandler workoutHandler,
    ILogger<WhoopBackfillService> logger)
    : IWhoopBackfillService
{
    /// <summary>
    /// WHOOP v2's max page size is 25 across all list endpoints. Backfill
    /// paginates with this; ~30 days × 1-2 entities per day per kind ≈ ~3
    /// pages per kind per user.
    /// </summary>
    private const int PageLimit = 25;

    /// <summary>
    /// Courtesy delay between paginated requests so we don't hammer WHOOP
    /// even when there's no rate-limit response. Cumulative latency at three
    /// list endpoints × ~3 pages each × 100ms is well under a second.
    /// </summary>
    private static readonly TimeSpan PaginationDelay = TimeSpan.FromMilliseconds(100);

    public async Task<Result<BackfillSummary>> RunAsync(
        Guid externalConnectionId,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        using var _ = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ExternalConnectionId"] = externalConnectionId,
            ["BackfillStart"] = start,
            ["BackfillEnd"] = end,
        });

        var stopwatch = Stopwatch.StartNew();

        var connection = await dbContext.ExternalConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == externalConnectionId, cancellationToken);
        if (connection is null)
        {
            return Result<BackfillSummary>.Failure($"External connection {externalConnectionId} not found.");
        }
        if (connection.Status != ConnectionStatus.Active)
        {
            return Result<BackfillSummary>.Failure(
                $"Connection {externalConnectionId} is not active (status={connection.Status}); backfill skipped.");
        }

        var tokenResult = await tokens.GetAccessTokenAsync(externalConnectionId, cancellationToken);
        if (!tokenResult.IsSuccess)
        {
            return Result<BackfillSummary>.Failure($"Token acquisition failed: {tokenResult.Error}");
        }
        var accessToken = tokenResult.Value!;

        var counters = new Counters();

        // --- Recoveries ----------------------------------------------------
        var recoveryErr = await BackfillRecoveriesAsync(
            connection.Id, accessToken, start, end, counters, cancellationToken);
        if (recoveryErr is not null && counters.RecoveriesProcessed == 0)
        {
            // Connection-level failure on the first list call — abort.
            return await AbortWith(connection, start, end, counters, stopwatch, recoveryErr, cancellationToken);
        }

        // --- Sleeps --------------------------------------------------------
        var sleepErr = await BackfillSleepsAsync(
            connection.Id, accessToken, start, end, counters, cancellationToken);
        if (sleepErr is not null && counters.SleepsProcessed == 0)
        {
            return await AbortWith(connection, start, end, counters, stopwatch, sleepErr, cancellationToken);
        }

        // --- Workouts ------------------------------------------------------
        var workoutErr = await BackfillWorkoutsAsync(
            connection.Id, accessToken, start, end, counters, cancellationToken);
        if (workoutErr is not null && counters.WorkoutsProcessed == 0)
        {
            return await AbortWith(connection, start, end, counters, stopwatch, workoutErr, cancellationToken);
        }

        stopwatch.Stop();
        var summary = counters.ToSummary(stopwatch.Elapsed);
        await WriteAuditAsync(connection, start, end, summary, success: true, errorMessage: null, cancellationToken);

        logger.LogInformation(
            "Backfill complete connection={ConnectionId} duration={DurationMs}ms recoveries={Rec} sleeps={Slp} workouts={Wkt} failed={Failed}",
            connection.Id,
            (int)stopwatch.Elapsed.TotalMilliseconds,
            counters.RecoveriesProcessed,
            counters.SleepsProcessed,
            counters.WorkoutsProcessed,
            counters.FailedEntities);

        return Result<BackfillSummary>.Success(summary);
    }

    private async Task<string?> BackfillRecoveriesAsync(
        Guid connectionId, string accessToken,
        DateTimeOffset start, DateTimeOffset end,
        Counters counters, CancellationToken ct)
    {
        string? nextToken = null;
        // Cache fetched cycles by id so repeated occurrences (rare) reuse the same envelope.
        var cycleCache = new Dictionary<long, WhoopCyclePayload>();

        do
        {
            var page = await whoopApi.ListRecoveriesAsync(accessToken, PageLimit, nextToken, start, end, ct);
            if (!page.IsSuccess)
            {
                logger.LogWarning("Recovery list-endpoint failed: {Error}", page.Error);
                return page.Error;
            }
            foreach (var recovery in page.Value!.Records)
            {
                if (ct.IsCancellationRequested) break;

                // Each entity gets its own ingestion trace root per ADR 0011.
                using var rootSpan = StartBackfillRoot(connectionId, "cycle.backfill");

                // Resolve the cycle envelope (for cycle_start_at / cycle_end_at).
                if (!cycleCache.TryGetValue(recovery.CycleId, out var cyclePayload))
                {
                    var cycleResult = await whoopApi.GetCycleAsync(accessToken, recovery.CycleId, ct);
                    if (!cycleResult.IsSuccess)
                    {
                        counters.FailedEntities++;
                        counters.FirstError ??= cycleResult.Error;
                        logger.LogWarning("Backfill cycle fetch failed cycleId={CycleId}: {Error}",
                            recovery.CycleId, cycleResult.Error);
                        continue;
                    }
                    cyclePayload = cycleResult.Value!;
                    cycleCache[recovery.CycleId] = cyclePayload;
                }

                var result = await recoveryHandler.UpsertFromPayloadAsync(
                    connectionId, IngestedVia.Backfill, recovery, cyclePayload,
                    upstreamTraceId: null, ct);
                counters.RecoveriesProcessed++;
                if (!result.IsSuccess)
                {
                    counters.FailedEntities++;
                    counters.FirstError ??= result.Error;
                    continue;
                }
                counters.TallyRecovery(result.Value!.Status);
            }
            nextToken = page.Value!.NextToken;
            if (!string.IsNullOrEmpty(nextToken))
            {
                await Task.Delay(PaginationDelay, ct);
            }
        } while (!string.IsNullOrEmpty(nextToken) && !ct.IsCancellationRequested);

        return null;
    }

    private async Task<string?> BackfillSleepsAsync(
        Guid connectionId, string accessToken,
        DateTimeOffset start, DateTimeOffset end,
        Counters counters, CancellationToken ct)
    {
        string? nextToken = null;
        do
        {
            var page = await whoopApi.ListSleepsAsync(accessToken, PageLimit, nextToken, start, end, ct);
            if (!page.IsSuccess)
            {
                logger.LogWarning("Sleep list-endpoint failed: {Error}", page.Error);
                return page.Error;
            }
            foreach (var sleep in page.Value!.Records)
            {
                if (ct.IsCancellationRequested) break;

                using var rootSpan = StartBackfillRoot(connectionId, "cycle.backfill");

                var result = await sleepHandler.UpsertFromPayloadAsync(
                    connectionId, IngestedVia.Backfill, sleep,
                    cycleId: null, upstreamTraceId: null, ct);
                counters.SleepsProcessed++;
                if (!result.IsSuccess)
                {
                    counters.FailedEntities++;
                    counters.FirstError ??= result.Error;
                    continue;
                }
                counters.TallySleep(result.Value!.Status);
            }
            nextToken = page.Value!.NextToken;
            if (!string.IsNullOrEmpty(nextToken))
            {
                await Task.Delay(PaginationDelay, ct);
            }
        } while (!string.IsNullOrEmpty(nextToken) && !ct.IsCancellationRequested);

        return null;
    }

    private async Task<string?> BackfillWorkoutsAsync(
        Guid connectionId, string accessToken,
        DateTimeOffset start, DateTimeOffset end,
        Counters counters, CancellationToken ct)
    {
        string? nextToken = null;
        do
        {
            var page = await whoopApi.ListWorkoutsAsync(accessToken, PageLimit, nextToken, start, end, ct);
            if (!page.IsSuccess)
            {
                logger.LogWarning("Workout list-endpoint failed: {Error}", page.Error);
                return page.Error;
            }
            foreach (var workout in page.Value!.Records)
            {
                if (ct.IsCancellationRequested) break;

                using var rootSpan = StartBackfillRoot(connectionId, "workout.backfill");

                var result = await workoutHandler.UpsertFromPayloadAsync(
                    connectionId, IngestedVia.Backfill, workout,
                    upstreamTraceId: null, ct);
                counters.WorkoutsProcessed++;
                if (!result.IsSuccess)
                {
                    counters.FailedEntities++;
                    counters.FirstError ??= result.Error;
                    continue;
                }
                counters.TallyWorkout(result.Value!.Status);
            }
            nextToken = page.Value!.NextToken;
            if (!string.IsNullOrEmpty(nextToken))
            {
                await Task.Delay(PaginationDelay, ct);
            }
        } while (!string.IsNullOrEmpty(nextToken) && !ct.IsCancellationRequested);

        return null;
    }

    /// <summary>
    /// Open the per-entity ADR 0011 trace root. Pre-seeds all three rollup
    /// outcome tags to NotInvoked; the handler that runs overwrites its own.
    /// For cycle.backfill events the recovery+sleep handler set their tags;
    /// for workout.backfill the workout handler does.
    /// </summary>
    private static Activity? StartBackfillRoot(Guid connectionId, string eventType)
    {
        var root = IngestionTracing.StartIngestionScope(
            source: "whoop",
            trigger: "backfill",
            eventType: eventType,
            externalConnectionId: connectionId,
            upstreamTraceId: null);
        IngestionTracing.RecordOutcome(root, "recovery", IngestionTracing.Outcomes.NotInvoked);
        IngestionTracing.RecordOutcome(root, "sleep", IngestionTracing.Outcomes.NotInvoked);
        IngestionTracing.RecordOutcome(root, "workout", IngestionTracing.Outcomes.NotInvoked);
        return root;
    }

    private async Task<Result<BackfillSummary>> AbortWith(
        ExternalConnection connection,
        DateTimeOffset start,
        DateTimeOffset end,
        Counters counters,
        Stopwatch stopwatch,
        string error,
        CancellationToken ct)
    {
        stopwatch.Stop();
        var summary = counters.ToSummary(stopwatch.Elapsed);
        await WriteAuditAsync(connection, start, end, summary, success: false, errorMessage: error, ct);
        return Result<BackfillSummary>.Failure(error);
    }

    private async Task WriteAuditAsync(
        ExternalConnection connection,
        DateTimeOffset start,
        DateTimeOffset end,
        BackfillSummary summary,
        bool success,
        string? errorMessage,
        CancellationToken ct)
    {
        var context = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            window = new { start, end },
            summary,
        }));

        dbContext.OAuthAuditEntries.Add(new OAuthAuditEntry
        {
            UserId = connection.UserId,
            ExternalConnectionId = connection.Id,
            Source = OAuthAuditSource.Whoop,
            Action = OAuthAuditAction.Backfill,
            Success = success,
            HttpStatusCode = null,
            ErrorMessage = errorMessage is null
                ? null
                : (errorMessage.Length > 1000 ? errorMessage[..1000] : errorMessage),
            Context = context,
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(ct);
    }

    private sealed class Counters
    {
        public int RecoveriesProcessed;
        public int RecoveriesInserted, RecoveriesUpdated, RecoveriesNoOp;
        public int SleepsProcessed;
        public int SleepsInserted, SleepsUpdated, SleepsNoOp;
        public int WorkoutsProcessed;
        public int WorkoutsInserted, WorkoutsUpdated, WorkoutsNoOp;
        public int FailedEntities;
        public string? FirstError;

        public void TallyRecovery(RecoveryIngestionStatus s)
        {
            switch (s)
            {
                case RecoveryIngestionStatus.Inserted: RecoveriesInserted++; break;
                case RecoveryIngestionStatus.Updated: RecoveriesUpdated++; break;
                case RecoveryIngestionStatus.NoOp: RecoveriesNoOp++; break;
                case RecoveryIngestionStatus.SkippedNoData: /* not counted; no insert happened */ break;
            }
        }
        public void TallySleep(SleepIngestionStatus s)
        {
            switch (s)
            {
                case SleepIngestionStatus.Inserted: SleepsInserted++; break;
                case SleepIngestionStatus.Updated: SleepsUpdated++; break;
                case SleepIngestionStatus.NoOp: SleepsNoOp++; break;
                case SleepIngestionStatus.SkippedNoData: break;
            }
        }
        public void TallyWorkout(WorkoutIngestionStatus s)
        {
            switch (s)
            {
                case WorkoutIngestionStatus.Inserted: WorkoutsInserted++; break;
                case WorkoutIngestionStatus.Updated: WorkoutsUpdated++; break;
                case WorkoutIngestionStatus.NoOp: WorkoutsNoOp++; break;
                case WorkoutIngestionStatus.SkippedNoData: break;
            }
        }

        public BackfillSummary ToSummary(TimeSpan duration) => new(
            RecoveriesInserted, RecoveriesUpdated, RecoveriesNoOp,
            SleepsInserted, SleepsUpdated, SleepsNoOp,
            WorkoutsInserted, WorkoutsUpdated, WorkoutsNoOp,
            FailedEntities,
            FirstError,
            duration);
    }
}

using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using SomaCore.Domain.Common;
using SomaCore.Domain.WhoopSleeps;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Whoop;

namespace SomaCore.Infrastructure.Sleep;

/// <summary>
/// Ingest WHOOP sleep data. Mirrors
/// <see cref="SomaCore.Infrastructure.Recovery.RecoveryIngestionHandler"/> in
/// structure, error handling, and idempotency strategy. Differences from
/// recovery:
///
/// - Natural key is <c>whoop_sleep_id</c> (a Guid) rather than
///   <c>whoop_cycle_id</c> (a long). The cycle id is still required to fetch
///   from WHOOP (via <c>/cycle/{id}/sleep</c>), but uniqueness in our table
///   is scoped to <c>(external_connection_id, whoop_sleep_id)</c>.
/// - Sleep can legitimately be absent for a cycle (e.g. naps without main
///   sleep). 404 from WHOOP maps to <see cref="SleepIngestionStatus.SkippedNoData"/>.
/// - The full WHOOP <c>score</c> sub-object is preserved as jsonb in the
///   <c>score</c> column alongside the lifted typed columns, per the
///   phase-2-track-A schema (<c>docs/schema/0002_whoop_sleep_workout.sql</c>).
/// </summary>
public sealed class WhoopSleepIngestionHandler(
    SomaCoreDbContext dbContext,
    IWhoopApiClient whoopApi,
    IWhoopAccessTokenCache tokens,
    ILogger<WhoopSleepIngestionHandler> logger)
    : IWhoopSleepIngestionHandler
{
    public async Task<Result<SleepIngestionOutcome>> IngestAsync(
        SleepIngestionRequest request,
        CancellationToken cancellationToken)
    {
        using var _ = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ExternalConnectionId"] = request.ExternalConnectionId,
            ["IngestedVia"] = request.IngestedVia,
            ["TraceId"] = request.TraceId,
            ["SleepId"] = request.SleepId,
            ["CycleId"] = request.CycleId,
        });

        // ADR 0011 handler scope. Natural key is the WHOOP sleep id when we
        // already know it (drainer pre-resolved), otherwise the cycle id.
        using var span = IngestionTracing.StartHandlerScope(
            handlerShortName: "sleep",
            naturalKey: request.SleepId?.ToString() ?? request.CycleId?.ToString());

        var connection = await dbContext.ExternalConnections
            .FirstOrDefaultAsync(c => c.Id == request.ExternalConnectionId, cancellationToken);

        if (connection is null)
        {
            return Failure(span, $"External connection {request.ExternalConnectionId} not found.");
        }

        var tokenResult = await tokens.GetAccessTokenAsync(request.ExternalConnectionId, cancellationToken);
        if (!tokenResult.IsSuccess)
        {
            return Failure(span, $"Token acquisition failed: {tokenResult.Error}");
        }
        var accessToken = tokenResult.Value!;

        // Resolve sleep payload. Priority: explicit cycle id (the drainer's
        // pre-resolved happy path; also the poller's), then fall back to
        // listing recent recoveries and using the matched cycle id. WHOOP
        // doesn't expose a list-recent-sleeps endpoint by sleep UUID, so we
        // piggyback on the recovery list to map sleep_id -> cycle_id when
        // the caller only knows the sleep UUID.
        long resolvedCycleId;
        if (request.CycleId is long cid)
        {
            resolvedCycleId = cid;
        }
        else if (request.SleepId is Guid sid)
        {
            var listResult = await whoopApi.ListRecentRecoveriesAsync(accessToken, limit: 10, cancellationToken);
            if (!listResult.IsSuccess)
            {
                return Failure(span, listResult.Error!);
            }
            var match = listResult.Value!.Records.FirstOrDefault(r => r.SleepId == sid);
            if (match is null)
            {
                logger.LogInformation("Sleep {SleepId} did not match any recent cycle", sid);
                return SkippedNoData(span);
            }
            resolvedCycleId = match.CycleId;
        }
        else
        {
            return Failure(span, "SleepIngestionRequest must carry CycleId or SleepId.");
        }

        var sleepResult = await whoopApi.GetSleepByCycleAsync(accessToken, resolvedCycleId, cancellationToken);
        if (!sleepResult.IsSuccess)
        {
            return Failure(span, sleepResult.Error!);
        }

        var payload = sleepResult.Value;
        if (payload is null)
        {
            logger.LogInformation("WHOOP returned no sleep for cycle {CycleId}", resolvedCycleId);
            return SkippedNoData(span, resolvedCycleId);
        }

        return await UpsertCoreAsync(
            span,
            connection.UserId,
            connection.Id,
            request.IngestedVia,
            payload,
            cycleId: resolvedCycleId,
            cancellationToken);
    }

    public async Task<Result<SleepIngestionOutcome>> UpsertFromPayloadAsync(
        Guid externalConnectionId,
        string ingestedVia,
        WhoopSleepPayload payload,
        long? cycleId,
        string? upstreamTraceId,
        CancellationToken cancellationToken)
    {
        using var _ = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ExternalConnectionId"] = externalConnectionId,
            ["IngestedVia"] = ingestedVia,
            ["TraceId"] = upstreamTraceId,
            ["SleepId"] = payload.Id,
            ["CycleId"] = cycleId,
        });

        using var span = IngestionTracing.StartHandlerScope(
            handlerShortName: "sleep",
            naturalKey: payload.Id.ToString());

        var connection = await dbContext.ExternalConnections
            .FirstOrDefaultAsync(c => c.Id == externalConnectionId, cancellationToken);
        if (connection is null)
        {
            return Failure(span, $"External connection {externalConnectionId} not found.");
        }

        return await UpsertCoreAsync(
            span,
            connection.UserId,
            connection.Id,
            ingestedVia,
            payload,
            cycleId,
            cancellationToken);
    }

    /// <summary>
    /// Shared upsert body. Both <see cref="IngestAsync"/> and
    /// <see cref="UpsertFromPayloadAsync"/> funnel through this. The handler
    /// span and the rolled-up <c>outcomes.sleep</c> tag are set here.
    /// <paramref name="cycleId"/> rides along the outcome for callers that
    /// care; it is NOT a uniqueness key (sleep dedupes on whoop_sleep_id).
    /// </summary>
    private async Task<Result<SleepIngestionOutcome>> UpsertCoreAsync(
        System.Diagnostics.Activity? span,
        Guid userId,
        Guid connectionId,
        string ingestedVia,
        WhoopSleepPayload payload,
        long? cycleId,
        CancellationToken cancellationToken)
    {
        var rawDoc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload));
        JsonDocument? scoreDoc = payload.Score is null
            ? null
            : JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload.Score));

        var existing = await dbContext.WhoopSleeps
            .FirstOrDefaultAsync(
                s => s.ExternalConnectionId == connectionId
                  && s.WhoopSleepId == payload.Id,
                cancellationToken);

        if (existing is null)
        {
            var entity = new WhoopSleep
            {
                UserId = userId,
                ExternalConnectionId = connectionId,
                WhoopSleepId = payload.Id,
                StartAt = payload.Start,
                EndAt = payload.End,
                TimezoneOffset = payload.TimezoneOffset,
                Nap = payload.Nap,
                ScoreState = payload.ScoreState,
                SleepPerformancePercentage = payload.Score?.SleepPerformancePercentage,
                SleepEfficiencyPercentage = payload.Score?.SleepEfficiencyPercentage,
                SleepConsistencyPercentage = payload.Score?.SleepConsistencyPercentage,
                TotalInBedTimeMilli = payload.Score?.StageSummary?.TotalInBedTimeMilli,
                TotalSleepTimeMilli = ComputeTotalSleep(payload.Score?.StageSummary),
                Score = scoreDoc,
                IngestedVia = ingestedVia,
                IngestedAt = DateTimeOffset.UtcNow,
                RawPayload = rawDoc,
            };
            dbContext.WhoopSleeps.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Ingested new sleep id={SleepId} cycle={CycleId} state={ScoreState} via={IngestedVia}",
                payload.Id, cycleId, payload.ScoreState, ingestedVia);

            IngestionTracing.RecordHandlerOutcome(span, IngestionTracing.Outcomes.Inserted, payload.ScoreState);
            IngestionTracing.RecordOutcome(rootSpan: null, "sleep", IngestionTracing.Outcomes.Inserted);
            return Result<SleepIngestionOutcome>.Success(new SleepIngestionOutcome(
                SleepIngestionStatus.Inserted,
                entity.Id,
                payload.Id,
                cycleId,
                payload.ScoreState));
        }

        var newPerf = payload.Score?.SleepPerformancePercentage;
        if (existing.ScoreState == payload.ScoreState
         && existing.SleepPerformancePercentage == newPerf)
        {
            IngestionTracing.RecordHandlerOutcome(span, IngestionTracing.Outcomes.NoOp, existing.ScoreState);
            IngestionTracing.RecordOutcome(rootSpan: null, "sleep", IngestionTracing.Outcomes.NoOp);
            return Result<SleepIngestionOutcome>.Success(new SleepIngestionOutcome(
                SleepIngestionStatus.NoOp,
                existing.Id,
                existing.WhoopSleepId,
                cycleId,
                existing.ScoreState));
        }

        existing.StartAt = payload.Start;
        existing.EndAt = payload.End;
        existing.TimezoneOffset = payload.TimezoneOffset;
        existing.Nap = payload.Nap;
        existing.ScoreState = payload.ScoreState;
        existing.SleepPerformancePercentage = newPerf;
        existing.SleepEfficiencyPercentage = payload.Score?.SleepEfficiencyPercentage;
        existing.SleepConsistencyPercentage = payload.Score?.SleepConsistencyPercentage;
        existing.TotalInBedTimeMilli = payload.Score?.StageSummary?.TotalInBedTimeMilli;
        existing.TotalSleepTimeMilli = ComputeTotalSleep(payload.Score?.StageSummary);
        existing.Score = scoreDoc;
        existing.IngestedVia = ingestedVia;
        existing.IngestedAt = DateTimeOffset.UtcNow;
        existing.RawPayload = rawDoc;

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Updated sleep id={SleepId} cycle={CycleId} state={ScoreState} via={IngestedVia}",
            payload.Id, cycleId, payload.ScoreState, ingestedVia);

        IngestionTracing.RecordHandlerOutcome(span, IngestionTracing.Outcomes.Updated, payload.ScoreState);
        IngestionTracing.RecordOutcome(rootSpan: null, "sleep", IngestionTracing.Outcomes.Updated);
        return Result<SleepIngestionOutcome>.Success(new SleepIngestionOutcome(
            SleepIngestionStatus.Updated,
            existing.Id,
            existing.WhoopSleepId,
            cycleId,
            existing.ScoreState));
    }

    /// <summary>
    /// total_sleep_time = in_bed - awake - no_data. WHOOP doesn't return this
    /// directly. Returns null if any required component is missing.
    /// </summary>
    private static long? ComputeTotalSleep(WhoopSleepStageSummary? stage)
    {
        if (stage?.TotalInBedTimeMilli is not long inBed) return null;
        var awake = stage.TotalAwakeTimeMilli ?? 0L;
        var noData = stage.TotalNoDataTimeMilli ?? 0L;
        var derived = inBed - awake - noData;
        return derived < 0 ? null : derived;
    }

    private static Result<SleepIngestionOutcome> Failure(System.Diagnostics.Activity? span, string error)
    {
        // Failure outcomes are not in the ADR 0011 vocabulary on the root tag.
        // We tag the handler span as "Failed" for queryability but leave the
        // root's outcomes.sleep unset; the Result<>.Failure path bubbles up
        // and the drainer marks the webhook row as 'failed' separately.
        IngestionTracing.RecordHandlerOutcome(span, outcome: "Failed");
        return Result<SleepIngestionOutcome>.Failure(error);
    }

    private static Result<SleepIngestionOutcome> SkippedNoData(System.Diagnostics.Activity? span, long? cycleId = null)
    {
        IngestionTracing.RecordHandlerOutcome(span, IngestionTracing.Outcomes.SkippedNoData);
        IngestionTracing.RecordOutcome(rootSpan: null, "sleep", IngestionTracing.Outcomes.SkippedNoData);
        return Result<SleepIngestionOutcome>.Success(
            new SleepIngestionOutcome(SleepIngestionStatus.SkippedNoData, null, null, cycleId, null));
    }
}

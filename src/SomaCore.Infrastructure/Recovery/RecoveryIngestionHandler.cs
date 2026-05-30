using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using SomaCore.Domain.Common;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Whoop;

namespace SomaCore.Infrastructure.Recovery;

public sealed class RecoveryIngestionHandler(
    SomaCoreDbContext dbContext,
    IWhoopApiClient whoopApi,
    IWhoopAccessTokenCache tokens,
    ILogger<RecoveryIngestionHandler> logger)
    : IRecoveryIngestionHandler
{
    public async Task<Result<RecoveryIngestionOutcome>> IngestAsync(
        RecoveryIngestionRequest request,
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

        // ADR 0011 handler scope. Additive — no interface change. The natural
        // key is the cycle id when known up front (drainer pre-resolved /
        // poller), otherwise the sleep UUID from the webhook payload.
        using var span = IngestionTracing.StartHandlerScope(
            handlerShortName: "recovery",
            naturalKey: request.CycleId?.ToString() ?? request.SleepId?.ToString());

        var connection = await dbContext.ExternalConnections
            .FirstOrDefaultAsync(c => c.Id == request.ExternalConnectionId, cancellationToken);

        if (connection is null)
        {
            IngestionTracing.RecordHandlerOutcome(span, outcome: "Failed");
            return Result<RecoveryIngestionOutcome>.Failure(
                $"External connection {request.ExternalConnectionId} not found.");
        }

        var tokenResult = await tokens.GetAccessTokenAsync(request.ExternalConnectionId, cancellationToken);
        if (!tokenResult.IsSuccess)
        {
            IngestionTracing.RecordHandlerOutcome(span, outcome: "Failed");
            return Result<RecoveryIngestionOutcome>.Failure($"Token acquisition failed: {tokenResult.Error}");
        }
        var accessToken = tokenResult.Value!;

        // Resolve the recovery payload. Priority: explicit cycle id, then sleep id,
        // then "latest". Webhooks identify recoveries by sleep UUID (v2), so we
        // pull the most recent N and match by sleep_id.
        WhoopRecoveryPayload? payload = null;

        if (request.CycleId is long cycleId)
        {
            var byCycle = await whoopApi.GetRecoveryByCycleAsync(accessToken, cycleId, cancellationToken);
            if (!byCycle.IsSuccess)
            {
                IngestionTracing.RecordHandlerOutcome(span, outcome: "Failed");
                return Result<RecoveryIngestionOutcome>.Failure(byCycle.Error!);
            }
            payload = byCycle.Value;
        }
        else
        {
            // Webhook / latest path: list a small batch and pick the right one.
            var listResult = await whoopApi.ListRecentRecoveriesAsync(accessToken, limit: 10, cancellationToken);
            if (!listResult.IsSuccess)
            {
                IngestionTracing.RecordHandlerOutcome(span, outcome: "Failed");
                return Result<RecoveryIngestionOutcome>.Failure(listResult.Error!);
            }
            var records = listResult.Value!.Records;
            payload = request.SleepId is Guid sleep
                ? records.FirstOrDefault(r => r.SleepId == sleep)
                : records.FirstOrDefault();
        }

        if (payload is null)
        {
            logger.LogInformation("No matching recovery payload found");
            IngestionTracing.RecordHandlerOutcome(span, IngestionTracing.Outcomes.SkippedNoData);
            IngestionTracing.RecordOutcome(rootSpan: null, "recovery", IngestionTracing.Outcomes.SkippedNoData);
            return Result<RecoveryIngestionOutcome>.Success(
                new RecoveryIngestionOutcome(RecoveryIngestionStatus.SkippedNoData, null, null, null));
        }

        // Cycle window: best-effort fetch (we want cycle_start_at on the row).
        var cycle = await whoopApi.GetCycleAsync(accessToken, payload.CycleId, cancellationToken);
        if (!cycle.IsSuccess)
        {
            IngestionTracing.RecordHandlerOutcome(span, outcome: "Failed");
            return Result<RecoveryIngestionOutcome>.Failure($"Cycle fetch failed: {cycle.Error}");
        }

        return await UpsertCoreAsync(
            span,
            connection.UserId,
            connection.Id,
            request.IngestedVia,
            payload,
            cycle.Value!,
            cancellationToken);
    }

    public async Task<Result<RecoveryIngestionOutcome>> UpsertFromPayloadAsync(
        Guid externalConnectionId,
        string ingestedVia,
        WhoopRecoveryPayload payload,
        WhoopCyclePayload cycle,
        string? upstreamTraceId,
        CancellationToken cancellationToken)
    {
        using var _ = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ExternalConnectionId"] = externalConnectionId,
            ["IngestedVia"] = ingestedVia,
            ["TraceId"] = upstreamTraceId,
            ["CycleId"] = payload.CycleId,
        });

        using var span = IngestionTracing.StartHandlerScope(
            handlerShortName: "recovery",
            naturalKey: payload.CycleId.ToString());

        var connection = await dbContext.ExternalConnections
            .FirstOrDefaultAsync(c => c.Id == externalConnectionId, cancellationToken);
        if (connection is null)
        {
            IngestionTracing.RecordHandlerOutcome(span, outcome: "Failed");
            return Result<RecoveryIngestionOutcome>.Failure(
                $"External connection {externalConnectionId} not found.");
        }

        return await UpsertCoreAsync(
            span,
            connection.UserId,
            connection.Id,
            ingestedVia,
            payload,
            cycle,
            cancellationToken);
    }

    /// <summary>
    /// Shared upsert body. Both <see cref="IngestAsync"/> and
    /// <see cref="UpsertFromPayloadAsync"/> funnel through this. Handler span
    /// + rolled-up <c>outcomes.recovery</c> tag are set here.
    /// </summary>
    private async Task<Result<RecoveryIngestionOutcome>> UpsertCoreAsync(
        System.Diagnostics.Activity? span,
        Guid userId,
        Guid connectionId,
        string ingestedVia,
        WhoopRecoveryPayload payload,
        WhoopCyclePayload cycle,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.WhoopRecoveries
            .FirstOrDefaultAsync(
                r => r.ExternalConnectionId == connectionId
                  && r.WhoopCycleId == payload.CycleId,
                cancellationToken);

        var rawJson = JsonSerializer.SerializeToUtf8Bytes(payload);
        var rawDoc = JsonDocument.Parse(rawJson);

        if (existing is null)
        {
            var entity = new WhoopRecovery
            {
                UserId = userId,
                ExternalConnectionId = connectionId,
                WhoopCycleId = payload.CycleId,
                WhoopSleepId = payload.SleepId,
                ScoreState = payload.ScoreState,
                RecoveryScore = ToInt(payload.Score?.RecoveryScore),
                HrvRmssdMilli = payload.Score?.HrvRmssdMilli,
                RestingHeartRate = ToInt(payload.Score?.RestingHeartRate),
                Spo2Percentage = payload.Score?.Spo2Percentage,
                SkinTempCelsius = payload.Score?.SkinTempCelsius,
                CycleStartAt = cycle.Start,
                CycleEndAt = cycle.End,
                IngestedVia = ingestedVia,
                IngestedAt = DateTimeOffset.UtcNow,
                RawPayload = rawDoc,
            };
            dbContext.WhoopRecoveries.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Ingested new recovery cycle={CycleId} state={ScoreState} via={IngestedVia}",
                payload.CycleId, payload.ScoreState, ingestedVia);
            IngestionTracing.RecordHandlerOutcome(span, IngestionTracing.Outcomes.Inserted, payload.ScoreState);
            IngestionTracing.RecordOutcome(rootSpan: null, "recovery", IngestionTracing.Outcomes.Inserted);
            return Result<RecoveryIngestionOutcome>.Success(
                new RecoveryIngestionOutcome(
                    RecoveryIngestionStatus.Inserted,
                    entity.Id,
                    payload.CycleId,
                    payload.ScoreState));
        }

        var newRecoveryScore = ToInt(payload.Score?.RecoveryScore);

        if (existing.ScoreState == payload.ScoreState
         && existing.RecoveryScore == newRecoveryScore)
        {
            IngestionTracing.RecordHandlerOutcome(span, IngestionTracing.Outcomes.NoOp, existing.ScoreState);
            IngestionTracing.RecordOutcome(rootSpan: null, "recovery", IngestionTracing.Outcomes.NoOp);
            return Result<RecoveryIngestionOutcome>.Success(
                new RecoveryIngestionOutcome(
                    RecoveryIngestionStatus.NoOp,
                    existing.Id,
                    existing.WhoopCycleId,
                    existing.ScoreState));
        }

        existing.ScoreState = payload.ScoreState;
        existing.RecoveryScore = newRecoveryScore;
        existing.HrvRmssdMilli = payload.Score?.HrvRmssdMilli;
        existing.RestingHeartRate = ToInt(payload.Score?.RestingHeartRate);
        existing.Spo2Percentage = payload.Score?.Spo2Percentage;
        existing.SkinTempCelsius = payload.Score?.SkinTempCelsius;
        existing.CycleStartAt = cycle.Start;
        existing.CycleEndAt = cycle.End;
        existing.IngestedVia = ingestedVia;
        existing.IngestedAt = DateTimeOffset.UtcNow;
        existing.RawPayload = rawDoc;

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Updated recovery cycle={CycleId} state={ScoreState} via={IngestedVia}",
            payload.CycleId, payload.ScoreState, ingestedVia);
        IngestionTracing.RecordHandlerOutcome(span, IngestionTracing.Outcomes.Updated, existing.ScoreState);
        IngestionTracing.RecordOutcome(rootSpan: null, "recovery", IngestionTracing.Outcomes.Updated);
        return Result<RecoveryIngestionOutcome>.Success(
            new RecoveryIngestionOutcome(
                RecoveryIngestionStatus.Updated,
                existing.Id,
                existing.WhoopCycleId,
                existing.ScoreState));
    }

    /// <summary>
    /// WHOOP serializes "integer-valued" fields like recovery_score and
    /// resting_heart_rate as JSON numbers with a trailing .0. We accept them as
    /// decimal on the wire and round to int for storage where the schema expects int.
    /// </summary>
    private static int? ToInt(decimal? value) =>
        value is null ? null : (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);
}

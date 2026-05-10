using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using SomaCore.Domain.Common;
using SomaCore.Domain.WhoopRecoveries;
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

        var connection = await dbContext.ExternalConnections
            .FirstOrDefaultAsync(c => c.Id == request.ExternalConnectionId, cancellationToken);

        if (connection is null)
        {
            return Result<RecoveryIngestionOutcome>.Failure(
                $"External connection {request.ExternalConnectionId} not found.");
        }

        var tokenResult = await tokens.GetAccessTokenAsync(request.ExternalConnectionId, cancellationToken);
        if (!tokenResult.IsSuccess)
        {
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
            return Result<RecoveryIngestionOutcome>.Success(
                new RecoveryIngestionOutcome(RecoveryIngestionStatus.SkippedNoData, null, null, null));
        }

        // Cycle window: best-effort fetch (we want cycle_start_at on the row).
        var cycle = await whoopApi.GetCycleAsync(accessToken, payload.CycleId, cancellationToken);
        if (!cycle.IsSuccess)
        {
            return Result<RecoveryIngestionOutcome>.Failure($"Cycle fetch failed: {cycle.Error}");
        }

        var existing = await dbContext.WhoopRecoveries
            .FirstOrDefaultAsync(
                r => r.ExternalConnectionId == request.ExternalConnectionId
                  && r.WhoopCycleId == payload.CycleId,
                cancellationToken);

        var rawJson = JsonSerializer.SerializeToUtf8Bytes(payload);
        var rawDoc = JsonDocument.Parse(rawJson);

        if (existing is null)
        {
            var entity = new WhoopRecovery
            {
                UserId = connection.UserId,
                ExternalConnectionId = connection.Id,
                WhoopCycleId = payload.CycleId,
                WhoopSleepId = payload.SleepId,
                ScoreState = payload.ScoreState,
                RecoveryScore = ToInt(payload.Score?.RecoveryScore),
                HrvRmssdMilli = payload.Score?.HrvRmssdMilli,
                RestingHeartRate = ToInt(payload.Score?.RestingHeartRate),
                Spo2Percentage = payload.Score?.Spo2Percentage,
                SkinTempCelsius = payload.Score?.SkinTempCelsius,
                CycleStartAt = cycle.Value!.Start,
                CycleEndAt = cycle.Value!.End,
                IngestedVia = request.IngestedVia,
                IngestedAt = DateTimeOffset.UtcNow,
                RawPayload = rawDoc,
            };
            dbContext.WhoopRecoveries.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Ingested new recovery cycle={CycleId} state={ScoreState} via={IngestedVia}",
                payload.CycleId,
                payload.ScoreState,
                request.IngestedVia);
            return Result<RecoveryIngestionOutcome>.Success(
                new RecoveryIngestionOutcome(
                    RecoveryIngestionStatus.Inserted,
                    entity.Id,
                    payload.CycleId,
                    payload.ScoreState));
        }

        var newRecoveryScore = ToInt(payload.Score?.RecoveryScore);

        // Update path: WHOOP score may have transitioned from PENDING to SCORED.
        if (existing.ScoreState == payload.ScoreState
         && existing.RecoveryScore == newRecoveryScore)
        {
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
        existing.CycleStartAt = cycle.Value!.Start;
        existing.CycleEndAt = cycle.Value!.End;
        existing.IngestedVia = request.IngestedVia;
        existing.IngestedAt = DateTimeOffset.UtcNow;
        existing.RawPayload = rawDoc;

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Updated recovery cycle={CycleId} state={ScoreState} via={IngestedVia}",
            payload.CycleId,
            payload.ScoreState,
            request.IngestedVia);
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

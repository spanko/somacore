using System.Diagnostics;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using SomaCore.Domain.Common;
using SomaCore.Domain.WhoopWorkouts;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Whoop;

namespace SomaCore.Infrastructure.Workout;

/// <summary>
/// Ingest WHOOP workout data. Mirrors
/// <see cref="SomaCore.Infrastructure.Sleep.WhoopSleepIngestionHandler"/>
/// in structure, error handling, and idempotency strategy. Differences from
/// sleep:
///
/// - Natural key is the WHOOP workout UUID (<c>whoop_workout_id</c>).
///   Uniqueness in the table is scoped to
///   <c>(external_connection_id, whoop_workout_id)</c>.
/// - Workouts are NOT cycle-keyed; there's no list-recent fallback path.
///   <see cref="WorkoutIngestionRequest.WorkoutId"/> is required.
/// - The fetch endpoint is <c>/activity/workout/{id}</c> (not a cycle
///   sub-endpoint). 404 maps to <see cref="WorkoutIngestionStatus.SkippedNoData"/>.
/// - The full WHOOP <c>score</c> sub-object is preserved as jsonb in the
///   <c>score</c> column alongside the lifted typed columns.
///
/// Two entry points:
///   - <see cref="IngestAsync"/> — fetches by id, then upserts. Used by
///     drainer + poller + on-open-pull.
///   - <see cref="UpsertFromPayloadAsync"/> — skips the fetch when the caller
///     already has the payload. Used by Session 5 backfill (list endpoints
///     return the full envelope inline; refetching by id would double the
///     HTTP traffic for no benefit). Per ADR 0011 Session 5 resolution.
/// </summary>
public sealed class WhoopWorkoutIngestionHandler(
    SomaCoreDbContext dbContext,
    IWhoopApiClient whoopApi,
    IWhoopAccessTokenCache tokens,
    ILogger<WhoopWorkoutIngestionHandler> logger)
    : IWhoopWorkoutIngestionHandler
{
    public async Task<Result<WorkoutIngestionOutcome>> IngestAsync(
        WorkoutIngestionRequest request,
        CancellationToken cancellationToken)
    {
        using var _ = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ExternalConnectionId"] = request.ExternalConnectionId,
            ["IngestedVia"] = request.IngestedVia,
            ["TraceId"] = request.TraceId,
            ["WorkoutId"] = request.WorkoutId,
        });

        using var span = IngestionTracing.StartHandlerScope(
            handlerShortName: "workout",
            naturalKey: request.WorkoutId.ToString());

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

        var workoutResult = await whoopApi.GetWorkoutByIdAsync(accessToken, request.WorkoutId, cancellationToken);
        if (!workoutResult.IsSuccess)
        {
            return Failure(span, workoutResult.Error!);
        }

        var payload = workoutResult.Value;
        if (payload is null)
        {
            logger.LogInformation("WHOOP returned no workout for id {WorkoutId}", request.WorkoutId);
            return SkippedNoData(span);
        }

        return await UpsertCoreAsync(
            span,
            connection.UserId,
            connection.Id,
            request.IngestedVia,
            payload,
            cancellationToken);
    }

    public async Task<Result<WorkoutIngestionOutcome>> UpsertFromPayloadAsync(
        Guid externalConnectionId,
        string ingestedVia,
        WhoopWorkoutPayload payload,
        string? upstreamTraceId,
        CancellationToken cancellationToken)
    {
        using var _ = logger.BeginScope(new Dictionary<string, object?>
        {
            ["ExternalConnectionId"] = externalConnectionId,
            ["IngestedVia"] = ingestedVia,
            ["TraceId"] = upstreamTraceId,
            ["WorkoutId"] = payload.Id,
        });

        using var span = IngestionTracing.StartHandlerScope(
            handlerShortName: "workout",
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
            cancellationToken);
    }

    /// <summary>
    /// Shared upsert body. Both <see cref="IngestAsync"/> and
    /// <see cref="UpsertFromPayloadAsync"/> funnel through this. The handler
    /// span and the rolled-up <c>outcomes.workout</c> tag are set here.
    /// </summary>
    private async Task<Result<WorkoutIngestionOutcome>> UpsertCoreAsync(
        Activity? span,
        Guid userId,
        Guid connectionId,
        string ingestedVia,
        WhoopWorkoutPayload payload,
        CancellationToken cancellationToken)
    {
        var rawDoc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload));
        JsonDocument? scoreDoc = payload.Score is null
            ? null
            : JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload.Score));

        var existing = await dbContext.WhoopWorkouts
            .FirstOrDefaultAsync(
                w => w.ExternalConnectionId == connectionId
                  && w.WhoopWorkoutId == payload.Id,
                cancellationToken);

        if (existing is null)
        {
            var entity = new WhoopWorkout
            {
                UserId = userId,
                ExternalConnectionId = connectionId,
                WhoopWorkoutId = payload.Id,
                StartAt = payload.Start,
                EndAt = payload.End,
                TimezoneOffset = payload.TimezoneOffset,
                SportName = payload.SportName,
                ScoreState = payload.ScoreState,
                Strain = payload.Score?.Strain,
                AverageHeartRate = ToInt(payload.Score?.AverageHeartRate),
                MaxHeartRate = ToInt(payload.Score?.MaxHeartRate),
                Kilojoule = payload.Score?.Kilojoule,
                Score = scoreDoc,
                IngestedVia = ingestedVia,
                IngestedAt = DateTimeOffset.UtcNow,
                RawPayload = rawDoc,
            };
            dbContext.WhoopWorkouts.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Ingested new workout id={WorkoutId} sport={SportName} state={ScoreState} via={IngestedVia}",
                payload.Id, payload.SportName, payload.ScoreState, ingestedVia);

            IngestionTracing.RecordHandlerOutcome(span, IngestionTracing.Outcomes.Inserted, payload.ScoreState);
            IngestionTracing.RecordOutcome(rootSpan: null, "workout", IngestionTracing.Outcomes.Inserted);
            return Result<WorkoutIngestionOutcome>.Success(new WorkoutIngestionOutcome(
                WorkoutIngestionStatus.Inserted,
                entity.Id,
                payload.Id,
                payload.ScoreState));
        }

        var newStrain = payload.Score?.Strain;
        if (existing.ScoreState == payload.ScoreState
         && existing.Strain == newStrain)
        {
            IngestionTracing.RecordHandlerOutcome(span, IngestionTracing.Outcomes.NoOp, existing.ScoreState);
            IngestionTracing.RecordOutcome(rootSpan: null, "workout", IngestionTracing.Outcomes.NoOp);
            return Result<WorkoutIngestionOutcome>.Success(new WorkoutIngestionOutcome(
                WorkoutIngestionStatus.NoOp,
                existing.Id,
                existing.WhoopWorkoutId,
                existing.ScoreState));
        }

        existing.StartAt = payload.Start;
        existing.EndAt = payload.End;
        existing.TimezoneOffset = payload.TimezoneOffset;
        existing.SportName = payload.SportName;
        existing.ScoreState = payload.ScoreState;
        existing.Strain = newStrain;
        existing.AverageHeartRate = ToInt(payload.Score?.AverageHeartRate);
        existing.MaxHeartRate = ToInt(payload.Score?.MaxHeartRate);
        existing.Kilojoule = payload.Score?.Kilojoule;
        existing.Score = scoreDoc;
        existing.IngestedVia = ingestedVia;
        existing.IngestedAt = DateTimeOffset.UtcNow;
        existing.RawPayload = rawDoc;

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Updated workout id={WorkoutId} state={ScoreState} via={IngestedVia}",
            payload.Id, payload.ScoreState, ingestedVia);

        IngestionTracing.RecordHandlerOutcome(span, IngestionTracing.Outcomes.Updated, payload.ScoreState);
        IngestionTracing.RecordOutcome(rootSpan: null, "workout", IngestionTracing.Outcomes.Updated);
        return Result<WorkoutIngestionOutcome>.Success(new WorkoutIngestionOutcome(
            WorkoutIngestionStatus.Updated,
            existing.Id,
            existing.WhoopWorkoutId,
            existing.ScoreState));
    }

    /// <summary>
    /// WHOOP serializes heart-rate fields as JSON numbers that may carry a
    /// trailing <c>.0</c>. The schema columns are <c>int</c>; round at the
    /// persistence boundary. Same pattern as
    /// <see cref="SomaCore.Infrastructure.Recovery.RecoveryIngestionHandler"/>.
    /// </summary>
    private static int? ToInt(decimal? value) =>
        value is null ? null : (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);

    private static Result<WorkoutIngestionOutcome> Failure(Activity? span, string error)
    {
        IngestionTracing.RecordHandlerOutcome(span, outcome: "Failed");
        return Result<WorkoutIngestionOutcome>.Failure(error);
    }

    private static Result<WorkoutIngestionOutcome> SkippedNoData(Activity? span)
    {
        IngestionTracing.RecordHandlerOutcome(span, IngestionTracing.Outcomes.SkippedNoData);
        IngestionTracing.RecordOutcome(rootSpan: null, "workout", IngestionTracing.Outcomes.SkippedNoData);
        return Result<WorkoutIngestionOutcome>.Success(
            new WorkoutIngestionOutcome(WorkoutIngestionStatus.SkippedNoData, null, null, null));
    }
}

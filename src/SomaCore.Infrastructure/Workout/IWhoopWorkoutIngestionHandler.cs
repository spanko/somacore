using SomaCore.Domain.Common;
using SomaCore.Infrastructure.Whoop;

namespace SomaCore.Infrastructure.Workout;

/// <summary>
/// Single ingestion entry point for WHOOP workout data. Mirrors
/// <see cref="SomaCore.Infrastructure.Sleep.IWhoopSleepIngestionHandler"/>:
/// the webhook drainer (Session 3), the reconciliation poller (Session 4),
/// and the on-open synchronous pull (Session 4+) all funnel through this
/// handler so idempotency, tracing, and downstream effects converge to one
/// place. Per ADR 0006 (three-layer ingestion) + ADR 0011 (trace contract).
///
/// Workouts are NOT cycle-keyed (unlike recovery and sleep): they have their
/// own endpoint <c>/activity/workout/{id}</c> and are independent of the
/// sleep/wake cycle. As a result the request record carries
/// <see cref="WorkoutId"/> as a required field rather than the optional
/// (CycleId, SleepId) pair the sleep request uses.
/// </summary>
public interface IWhoopWorkoutIngestionHandler
{
    Task<Result<WorkoutIngestionOutcome>> IngestAsync(
        WorkoutIngestionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Session 5 backfill bypass: ingest from a payload the caller already
    /// has in hand (typically from <see cref="IWhoopApiClient.ListRecentWorkoutsAsync"/>),
    /// skipping the per-id <c>GetWorkoutByIdAsync</c> fetch. Same upsert
    /// semantics, same outcome vocabulary, same trace contract (handler span
    /// only; no fetch span since no fetch happens). Per ADR 0011 Session 5
    /// resolution.
    /// </summary>
    Task<Result<WorkoutIngestionOutcome>> UpsertFromPayloadAsync(
        Guid externalConnectionId,
        string ingestedVia,
        WhoopWorkoutPayload payload,
        string? upstreamTraceId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Request record for workout ingestion. <see cref="WorkoutId"/> is required
/// because workouts have no derivable natural-key fallback (no cycle id, no
/// list-recent dedupe path). This is a deliberate deviation from the sleep
/// request shape — see [WhoopWorkoutIngestionHandler.cs] for rationale.
/// </summary>
public sealed record WorkoutIngestionRequest(
    Guid ExternalConnectionId,
    string IngestedVia,        // 'webhook' | 'poller' | 'on_open_pull'
    Guid WorkoutId,            // required — the WHOOP workout UUID
    string? TraceId = null);   // upstream trace id, for log + span correlation

public enum WorkoutIngestionStatus
{
    Inserted,
    Updated,
    NoOp,           // workout already on file at this version
    SkippedNoData,  // WHOOP returned 404 for the workout id
}

public sealed record WorkoutIngestionOutcome(
    WorkoutIngestionStatus Status,
    Guid? WorkoutRowId,         // local PK
    Guid? WhoopWorkoutId,       // WHOOP natural key (echoes back the request)
    string? ScoreState);

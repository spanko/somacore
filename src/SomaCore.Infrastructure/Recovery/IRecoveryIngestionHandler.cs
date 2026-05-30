using SomaCore.Domain.Common;
using SomaCore.Infrastructure.Whoop;

namespace SomaCore.Infrastructure.Recovery;

/// <summary>
/// Single ingestion entry point for WHOOP recovery data. The webhook drainer,
/// the reconciliation poller (phase 5b), and the on-open synchronous pull
/// (phase 5b) all funnel through this handler so idempotency, logging, and
/// downstream effects converge to one place. Per ADR 0006.
/// </summary>
public interface IRecoveryIngestionHandler
{
    Task<Result<RecoveryIngestionOutcome>> IngestAsync(
        RecoveryIngestionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Session 5 backfill bypass: ingest from a recovery payload + cycle
    /// window the caller already has (typically from
    /// <see cref="IWhoopApiClient.ListRecentRecoveriesAsync"/> + a single
    /// <see cref="IWhoopApiClient.GetCycleAsync"/> for the window times).
    /// Skips the recovery-by-cycle fetch. Same upsert semantics, same
    /// outcome vocabulary, same trace contract (handler span only).
    /// Per ADR 0011 Session 5 resolution.
    /// </summary>
    Task<Result<RecoveryIngestionOutcome>> UpsertFromPayloadAsync(
        Guid externalConnectionId,
        string ingestedVia,
        WhoopRecoveryPayload payload,
        WhoopCyclePayload cycle,
        string? upstreamTraceId,
        CancellationToken cancellationToken);
}

public sealed record RecoveryIngestionRequest(
    Guid ExternalConnectionId,
    string IngestedVia,        // 'webhook' | 'poller' | 'on_open_pull'
    long? CycleId = null,      // when known up front (poller)
    Guid? SleepId = null,      // when known up front (webhook)
    string? TraceId = null);   // for log correlation

public enum RecoveryIngestionStatus
{
    Inserted,
    Updated,
    NoOp,           // recovery already on file at this version
    SkippedNoData,  // WHOOP had no recovery for the cycle (yet)
}

public sealed record RecoveryIngestionOutcome(
    RecoveryIngestionStatus Status,
    Guid? RecoveryId,
    long? CycleId,
    string? ScoreState);

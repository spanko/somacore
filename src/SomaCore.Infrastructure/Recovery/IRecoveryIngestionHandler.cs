using SomaCore.Domain.Common;

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

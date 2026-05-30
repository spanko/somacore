using SomaCore.Domain.Common;
using SomaCore.Infrastructure.Whoop;

namespace SomaCore.Infrastructure.Sleep;

/// <summary>
/// Single ingestion entry point for WHOOP sleep data. Mirrors
/// <see cref="SomaCore.Infrastructure.Recovery.IRecoveryIngestionHandler"/>:
/// the webhook drainer (Session 2), the reconciliation poller (Session 4),
/// and the on-open synchronous pull (Session 4+) all funnel through this
/// handler so idempotency, tracing, and downstream effects converge to one
/// place. Per ADR 0006 (three-layer ingestion) + ADR 0011 (trace contract).
/// </summary>
public interface IWhoopSleepIngestionHandler
{
    Task<Result<SleepIngestionOutcome>> IngestAsync(
        SleepIngestionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Session 5 backfill bypass: ingest from a sleep payload the caller
    /// already has in hand (typically from
    /// <see cref="IWhoopApiClient.ListRecentSleepsAsync"/>), skipping the
    /// per-cycle <c>GetSleepByCycleAsync</c> fetch. Same upsert semantics,
    /// same outcome vocabulary, same trace contract (handler span only).
    /// Per ADR 0011 Session 5 resolution.
    /// </summary>
    Task<Result<SleepIngestionOutcome>> UpsertFromPayloadAsync(
        Guid externalConnectionId,
        string ingestedVia,
        WhoopSleepPayload payload,
        long? cycleId,
        string? upstreamTraceId,
        CancellationToken cancellationToken);
}

public sealed record SleepIngestionRequest(
    Guid ExternalConnectionId,
    string IngestedVia,        // 'webhook' | 'poller' | 'on_open_pull'
    long? CycleId = null,      // when known up front (drainer pre-resolved; poller)
    Guid? SleepId = null,      // when known up front (e.g. webhook payload's id)
    string? TraceId = null);   // upstream trace id, for log + span correlation

public enum SleepIngestionStatus
{
    Inserted,
    Updated,
    NoOp,           // sleep already on file at this version
    SkippedNoData,  // WHOOP had no sleep for the cycle (yet, or never will)
}

public sealed record SleepIngestionOutcome(
    SleepIngestionStatus Status,
    Guid? SleepRowId,           // local PK
    Guid? WhoopSleepId,         // WHOOP natural key
    long? CycleId,
    string? ScoreState);

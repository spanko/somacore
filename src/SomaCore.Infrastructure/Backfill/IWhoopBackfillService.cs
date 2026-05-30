using SomaCore.Domain.Common;

namespace SomaCore.Infrastructure.Backfill;

/// <summary>
/// Session 5 — on-demand historical reconciliation of WHOOP recovery, sleep,
/// and workout data for one connection over an explicit date window. Bypasses
/// the scheduled poller and its gating. Admin-triggered only.
///
/// Backfill is the fourth ingestion trigger alongside the three from ADR 0006
/// (webhook, poller, on-open). It uses the same handlers as those triggers via
/// the <c>UpsertFromPayloadAsync</c> bypass entry point, so idempotency,
/// logging, and trace shape stay uniform.
/// </summary>
public interface IWhoopBackfillService
{
    Task<Result<BackfillSummary>> RunAsync(
        Guid externalConnectionId,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken);
}

/// <summary>
/// Per-entity-type breakdown of a backfill invocation. <c>FailedEntities</c>
/// counts entities that the handler returned an error for; the backfill
/// continues past per-entity failures (one bad workout doesn't abort the
/// whole run) and surfaces them in the count + first-error.
/// </summary>
public sealed record BackfillSummary(
    int RecoveriesInserted, int RecoveriesUpdated, int RecoveriesNoOp,
    int SleepsInserted, int SleepsUpdated, int SleepsNoOp,
    int WorkoutsInserted, int WorkoutsUpdated, int WorkoutsNoOp,
    int FailedEntities,
    string? FirstError,
    TimeSpan Duration);

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.JobRuns;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Recovery;

namespace SomaCore.IngestionJobs.Jobs;

/// <summary>
/// Walks active WHOOP connections and asks the shared
/// <see cref="IRecoveryIngestionHandler"/> for the latest recovery. Catches
/// webhooks the real-time path missed (cold-start cancellations, WHOOP
/// retries that gave up, score-state transitions that didn't re-fire, etc.).
/// Per ADR 0006: the poller converges with the webhook drainer on the same
/// handler, so idempotency, logging, downstream effects are identical.
/// </summary>
public sealed class ReconciliationPoller(
    SomaCoreDbContext dbContext,
    IRecoveryIngestionHandler handler,
    ILogger<ReconciliationPoller> logger)
    : IJob
{
    public string Name => JobName.ReconciliationPoller;

    public async Task<JobOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var active = await dbContext.ExternalConnections
            .AsNoTracking()
            .Where(c => c.Source == ConnectionSource.Whoop && c.Status == ConnectionStatus.Active)
            .Select(c => new { c.Id, c.UserId })
            .ToListAsync(cancellationToken);

        var summary = new
        {
            connections = active.Count,
            inserted = 0,
            updated = 0,
            no_op = 0,
            skipped_no_data = 0,
            failed = 0,
        };

        int inserted = 0, updated = 0, noOp = 0, skipped = 0, failed = 0;
        string? firstError = null;

        foreach (var c in active)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var request = new RecoveryIngestionRequest(
                ExternalConnectionId: c.Id,
                IngestedVia: IngestedVia.Poller,
                CycleId: null,
                SleepId: null,
                TraceId: null);

            var result = await handler.IngestAsync(request, cancellationToken);
            if (!result.IsSuccess)
            {
                failed++;
                firstError ??= result.Error;
                logger.LogWarning("Poller failed for connection {ConnectionId}: {Error}", c.Id, result.Error);
                continue;
            }

            switch (result.Value!.Status)
            {
                case RecoveryIngestionStatus.Inserted:       inserted++; break;
                case RecoveryIngestionStatus.Updated:        updated++;  break;
                case RecoveryIngestionStatus.NoOp:           noOp++;     break;
                case RecoveryIngestionStatus.SkippedNoData:  skipped++;  break;
            }
        }

        var success = failed == 0;
        return new JobOutcome(
            Success: success,
            Error: success ? null : firstError,
            Summary: new
            {
                connections = active.Count,
                inserted, updated, no_op = noOp,
                skipped_no_data = skipped,
                failed,
            });
    }
}

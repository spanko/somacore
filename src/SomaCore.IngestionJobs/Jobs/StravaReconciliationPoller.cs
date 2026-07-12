using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.JobRuns;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Strava;

namespace SomaCore.IngestionJobs.Jobs;

/// <summary>
/// Walks active Strava connections and catches activities the webhook path
/// missed (brief §1.4): per connection, list athlete activities after
/// max(started_at) from strava_activities and ingest anything not already
/// present through <see cref="IStravaActivityIngestService"/> — the same
/// converging path as the webhook drainer (ADR 0006), so idempotency, the
/// detail-fetch policy, and logging are identical.
///
/// Per ADR 0011 the poller emits one trace root per (connection, activity)
/// pulled, with <c>ingestion.source=strava</c> + <c>ingestion.trigger=poller</c>.
/// The job_runs row is written by <see cref="JobDispatcher"/>, same as every
/// job — and unlike the WHOOP poller's coverage gap, this one is asserted by
/// a dispatcher-driven test.
/// </summary>
public sealed class StravaReconciliationPoller(
    SomaCoreDbContext dbContext,
    IStravaApiClient stravaApi,
    IStravaAccessTokenCache tokens,
    IStravaActivityIngestService ingestService,
    ILogger<StravaReconciliationPoller> logger)
    : IJob
{
    /// <summary>
    /// List window for a connection with no activities yet. Reconciliation is
    /// a safety net for missed webhooks, not a backfill — a fresh connection's
    /// history lands via its first webhook-era activities (historical import
    /// is a deliberate non-goal, mirroring the WHOOP poller's stance).
    /// </summary>
    private static readonly TimeSpan DefaultLookback = TimeSpan.FromDays(7);

    public string Name => JobName.StravaReconciliationPoller;

    public async Task<JobOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var active = await dbContext.ExternalConnections
            .Where(c => c.Source == ConnectionSource.Strava && c.Status == ConnectionStatus.Active)
            .ToListAsync(cancellationToken);

        int listed = 0, ingested = 0, alreadyPresent = 0, failed = 0;
        string? firstError = null;

        foreach (var connection in active)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var now = DateTimeOffset.UtcNow;
            var error = await ReconcileConnectionAsync(
                connection.Id, cancellationToken,
                onListed: n => listed += n,
                onIngested: () => ingested++,
                onAlreadyPresent: () => alreadyPresent++,
                onFailed: () => failed++);

            connection.LastPolledAt = now;
            connection.LastPollOutcome = error is null ? PollOutcome.Polled : PollOutcome.Failed;
            firstError ??= error;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var success = failed == 0;
        return new JobOutcome(
            Success: success,
            Error: success ? null : firstError,
            Summary: new
            {
                connections = active.Count,
                listed,
                ingested,
                alreadyPresent,
                failed,
            });
    }

    private async Task<string?> ReconcileConnectionAsync(
        Guid connectionId,
        CancellationToken cancellationToken,
        Action<int> onListed,
        Action onIngested,
        Action onAlreadyPresent,
        Action onFailed)
    {
        var token = await tokens.GetAccessTokenAsync(connectionId, cancellationToken);
        if (!token.IsSuccess)
        {
            onFailed();
            logger.LogWarning(
                "Strava poller token acquisition failed for connection {ConnectionId}: {Error}",
                connectionId, token.Error);
            return token.Error;
        }

        // Brief §1.4: after = max(started_at) for this connection. Soft-deleted
        // rows still count — we saw the activity; its deletion is not a gap.
        var lastSeen = await dbContext.StravaActivities
            .AsNoTracking()
            .Where(a => a.ExternalConnectionId == connectionId)
            .MaxAsync(a => (DateTimeOffset?)a.StartedAt, cancellationToken);
        var afterEpoch = (lastSeen ?? DateTimeOffset.UtcNow - DefaultLookback).ToUnixTimeSeconds();

        var listResult = await stravaApi.ListAthleteActivitiesAsync(
            token.Value!, afterEpoch, cancellationToken);
        if (!listResult.IsSuccess)
        {
            onFailed();
            logger.LogWarning(
                "Strava poller list failed for connection {ConnectionId}: {Error}",
                connectionId, listResult.Error);
            return listResult.Error;
        }

        var summaries = listResult.Value!;
        onListed(summaries.Count);
        if (summaries.Count == 0)
        {
            return null;
        }

        var listedIds = summaries.Select(s => s.Payload.Id).ToArray();
        var knownIds = await dbContext.StravaActivities
            .AsNoTracking()
            .Where(a => listedIds.Contains(a.StravaActivityId))
            .Select(a => a.StravaActivityId)
            .ToListAsync(cancellationToken);
        var known = knownIds.ToHashSet();

        string? firstError = null;
        foreach (var summary in summaries)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (known.Contains(summary.Payload.Id))
            {
                onAlreadyPresent();
                continue;
            }

            using var rootSpan = IngestionTracing.StartIngestionScope(
                source: "strava",
                trigger: "poller",
                eventType: "activity.pull",
                externalConnectionId: connectionId,
                upstreamTraceId: null);
            IngestionTracing.RecordOutcome(rootSpan, "activity", IngestionTracing.Outcomes.NotInvoked);

            var result = await ingestService.IngestAsync(
                connectionId,
                summary.Payload.Id,
                ingestedVia: "poller",
                traceId: null,
                cancellationToken);
            if (!result.IsSuccess)
            {
                onFailed();
                firstError ??= result.Error;
                logger.LogWarning(
                    "Strava poller ingest failed for connection {ConnectionId} activity {StravaActivityId}: {Error}",
                    connectionId, summary.Payload.Id, result.Error);
                continue;
            }

            IngestionTracing.RecordOutcome(rootSpan, "activity", result.Value!.Outcome);
            onIngested();
        }

        return firstError;
    }
}

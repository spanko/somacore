using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.JobRuns;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Domain.WhoopSleeps;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Polling;
using SomaCore.Infrastructure.Recovery;
using SomaCore.Infrastructure.Sleep;
using SomaCore.Infrastructure.Whoop;
using SomaCore.Infrastructure.Workout;

namespace SomaCore.IngestionJobs.Jobs;

/// <summary>
/// Walks active WHOOP connections and reconciles WHOOP recovery, sleep, and
/// workout state. Catches webhooks the real-time path missed (cold-start
/// cancellations, WHOOP retries that gave up, score-state transitions that
/// didn't re-fire, etc.).
///
/// Per ADR 0006: the poller converges with the webhook drainer on the same
/// per-entity handlers, so idempotency, logging, and downstream effects are
/// identical to the webhook path. Per ADR 0011 (Session 4 amendment): the
/// poller emits a separate trace root per (user, cycle) for the cycle pull
/// and per (user, workout) for each workout enumerated.
///
/// Session 4.5 added per-connection gating via <see cref="PollerGating"/>:
/// on each tick the job evaluates whether to do work for each connection
/// (too-recent / current-cycle-already-scored / outside wake window) and
/// skips connections that don't need a poll. Skipped connections do NOT
/// emit ingestion trace roots — they're a no-op from an ingestion
/// perspective; a structured Serilog event is sufficient observability.
/// </summary>
public sealed class ReconciliationPoller : IJob
{
    private readonly SomaCoreDbContext _dbContext;
    private readonly IRecoveryIngestionHandler _recoveryHandler;
    private readonly IWhoopSleepIngestionHandler _sleepHandler;
    private readonly IWhoopWorkoutIngestionHandler _workoutHandler;
    private readonly IWhoopApiClient _whoopApi;
    private readonly IWhoopAccessTokenCache _tokens;
    private readonly ILogger<ReconciliationPoller> _logger;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>DI-friendly constructor. Production uses <see cref="DateTimeOffset.UtcNow"/>.</summary>
    public ReconciliationPoller(
        SomaCoreDbContext dbContext,
        IRecoveryIngestionHandler recoveryHandler,
        IWhoopSleepIngestionHandler sleepHandler,
        IWhoopWorkoutIngestionHandler workoutHandler,
        IWhoopApiClient whoopApi,
        IWhoopAccessTokenCache tokens,
        ILogger<ReconciliationPoller> logger)
        : this(dbContext, recoveryHandler, sleepHandler, workoutHandler, whoopApi, tokens, logger,
               clock: () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>
    /// Test seam: lets integration tests inject a fixed "now" so gating
    /// decisions don't depend on the wall clock. Production never calls this.
    /// </summary>
    internal ReconciliationPoller(
        SomaCoreDbContext dbContext,
        IRecoveryIngestionHandler recoveryHandler,
        IWhoopSleepIngestionHandler sleepHandler,
        IWhoopWorkoutIngestionHandler workoutHandler,
        IWhoopApiClient whoopApi,
        IWhoopAccessTokenCache tokens,
        ILogger<ReconciliationPoller> logger,
        Func<DateTimeOffset> clock)
    {
        _dbContext = dbContext;
        _recoveryHandler = recoveryHandler;
        _sleepHandler = sleepHandler;
        _workoutHandler = workoutHandler;
        _whoopApi = whoopApi;
        _tokens = tokens;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Cap how many recent workouts the poller pulls per connection per tick.
    /// WHOOP returns most-recent-first; anything older than this window will
    /// be picked up by Session 5's backfill, not by this safety-net job.
    /// </summary>
    private const int WorkoutsPerConnection = 25;

    /// <summary>
    /// How far back to look when computing typical wake time. WHOOP recovery
    /// has roughly daily cadence; 14 sleeps is a robust median.
    /// </summary>
    private static readonly TimeSpan WakeWindowLookback = TimeSpan.FromDays(14);

    public string Name => JobName.ReconciliationPoller;

    public async Task<JobOutcome> RunAsync(CancellationToken cancellationToken)
    {
        // Load full connection rows (not anonymous projections) so we can
        // update LastPolledAt + LastPollOutcome on each.
        var active = await _dbContext.ExternalConnections
            .Where(c => c.Source == ConnectionSource.Whoop && c.Status == ConnectionStatus.Active)
            .ToListAsync(cancellationToken);

        var counters = new Counters();
        string? firstError = null;

        foreach (var connection in active)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var now = _clock();
            var lookbackStart = now - WakeWindowLookback;

            var latestRecovery = await _dbContext.WhoopRecoveries
                .AsNoTracking()
                .Where(r => r.ExternalConnectionId == connection.Id)
                .OrderByDescending(r => r.CycleStartAt)
                .FirstOrDefaultAsync(cancellationToken);

            var recentSleeps = await _dbContext.WhoopSleeps
                .AsNoTracking()
                .Where(s => s.ExternalConnectionId == connection.Id
                         && s.EndAt >= lookbackStart)
                .OrderByDescending(s => s.EndAt)
                .ToListAsync(cancellationToken);

            var (decision, skipReason) = PollerGating.Evaluate(
                connection,
                latestRecovery,
                recentSleeps,
                now,
                minimumPollInterval: null);

            if (decision == PollDecision.Skip)
            {
                counters.Skipped++;
                connection.LastPolledAt = now;
                connection.LastPollOutcome = PollOutcome.Skipped;
                _logger.LogInformation(
                    "Poller skipped connection {ConnectionId} reason={SkipReason}",
                    connection.Id, skipReason);
                continue;
            }

            var cycleErr = await PullCycleAsync(connection.Id, counters, cancellationToken);
            var workoutErr = await PullWorkoutsAsync(connection.Id, counters, cancellationToken);
            var connectionErr = cycleErr ?? workoutErr;

            connection.LastPolledAt = now;
            connection.LastPollOutcome = connectionErr is null
                ? PollOutcome.Polled
                : PollOutcome.Failed;

            firstError ??= connectionErr;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var success = counters.Failed == 0;
        return new JobOutcome(
            Success: success,
            Error: success ? null : firstError,
            Summary: new
            {
                connections = active.Count,
                counters.Skipped,
                recovery = new { counters.RecoveryInserted, counters.RecoveryUpdated, counters.RecoveryNoOp, counters.RecoverySkipped },
                sleep    = new { counters.SleepInserted,    counters.SleepUpdated,    counters.SleepNoOp,    counters.SleepSkipped },
                workout  = new { counters.WorkoutInserted,  counters.WorkoutUpdated,  counters.WorkoutNoOp,  counters.WorkoutSkipped },
                failed   = counters.Failed,
            });
    }

    /// <summary>
    /// Cycle pull for one connection: open a per-(user, cycle) trace root,
    /// pre-seed all three outcomes to NotInvoked, invoke recovery + sleep
    /// handlers with IngestedVia.Poller. Recovery resolves the cycle id
    /// internally (its "latest" branch); we pick that up from its outcome
    /// and reuse it for sleep so both rows describe the same cycle.
    /// </summary>
    private async Task<string?> PullCycleAsync(Guid connectionId, Counters counters, CancellationToken cancellationToken)
    {
        using var rootSpan = IngestionTracing.StartIngestionScope(
            source: "whoop",
            trigger: "poller",
            eventType: "cycle.pull",
            externalConnectionId: connectionId,
            upstreamTraceId: null);
        IngestionTracing.RecordOutcome(rootSpan, "recovery", IngestionTracing.Outcomes.NotInvoked);
        IngestionTracing.RecordOutcome(rootSpan, "sleep",    IngestionTracing.Outcomes.NotInvoked);
        IngestionTracing.RecordOutcome(rootSpan, "workout",  IngestionTracing.Outcomes.NotInvoked);

        // Recovery first — the handler's "no CycleId, no SleepId" branch
        // resolves the latest cycle internally. We don't pre-resolve here
        // because (unlike the webhook drainer) there's no sleep UUID to
        // anchor the resolution.
        var recoveryRequest = new RecoveryIngestionRequest(
            ExternalConnectionId: connectionId,
            IngestedVia: IngestedVia.Poller,
            CycleId: null,
            SleepId: null,
            TraceId: null);
        var recoveryResult = await _recoveryHandler.IngestAsync(recoveryRequest, cancellationToken);
        if (!recoveryResult.IsSuccess)
        {
            counters.Failed++;
            _logger.LogWarning("Poller cycle/recovery failed for connection {ConnectionId}: {Error}",
                connectionId, recoveryResult.Error);
            return recoveryResult.Error;
        }
        counters.TallyRecovery(recoveryResult.Value!.Status);

        var resolvedCycleId = recoveryResult.Value.CycleId;
        if (resolvedCycleId is null)
        {
            // SkippedNoData on recovery (no recent cycle yet — e.g. brand-new
            // connection on a day with no strap wear). Sleep stays NotInvoked.
            return null;
        }

        var sleepRequest = new SleepIngestionRequest(
            ExternalConnectionId: connectionId,
            IngestedVia: IngestedVia.Poller,
            CycleId: resolvedCycleId,
            SleepId: null,
            TraceId: null);
        var sleepResult = await _sleepHandler.IngestAsync(sleepRequest, cancellationToken);
        if (!sleepResult.IsSuccess)
        {
            counters.Failed++;
            _logger.LogWarning("Poller cycle/sleep failed for connection {ConnectionId}: {Error}",
                connectionId, sleepResult.Error);
            return sleepResult.Error;
        }
        counters.TallySleep(sleepResult.Value!.Status);
        return null;
    }

    /// <summary>
    /// Workout pull for one connection: list recent workouts via
    /// <see cref="IWhoopApiClient.ListRecentWorkoutsAsync"/>, then ingest
    /// each one under its own per-(user, workout) trace root. Single-handler
    /// fan-out — recovery and sleep stay NotInvoked on each root.
    /// </summary>
    private async Task<string?> PullWorkoutsAsync(Guid connectionId, Counters counters, CancellationToken cancellationToken)
    {
        var token = await _tokens.GetAccessTokenAsync(connectionId, cancellationToken);
        if (!token.IsSuccess)
        {
            counters.Failed++;
            _logger.LogWarning("Poller workout-list token acquisition failed for connection {ConnectionId}: {Error}",
                connectionId, token.Error);
            return token.Error;
        }

        var listResult = await _whoopApi.ListRecentWorkoutsAsync(
            token.Value!,
            limit: WorkoutsPerConnection,
            nextToken: null,
            cancellationToken);
        if (!listResult.IsSuccess)
        {
            counters.Failed++;
            _logger.LogWarning("Poller workout-list failed for connection {ConnectionId}: {Error}",
                connectionId, listResult.Error);
            return listResult.Error;
        }

        string? firstError = null;
        foreach (var workout in listResult.Value!.Records)
        {
            if (cancellationToken.IsCancellationRequested) break;

            using var rootSpan = IngestionTracing.StartIngestionScope(
                source: "whoop",
                trigger: "poller",
                eventType: "workout.pull",
                externalConnectionId: connectionId,
                upstreamTraceId: null);
            IngestionTracing.RecordOutcome(rootSpan, "recovery", IngestionTracing.Outcomes.NotInvoked);
            IngestionTracing.RecordOutcome(rootSpan, "sleep",    IngestionTracing.Outcomes.NotInvoked);
            IngestionTracing.RecordOutcome(rootSpan, "workout",  IngestionTracing.Outcomes.NotInvoked);

            var request = new WorkoutIngestionRequest(
                ExternalConnectionId: connectionId,
                IngestedVia: IngestedVia.Poller,
                WorkoutId: workout.Id,
                TraceId: null);
            var result = await _workoutHandler.IngestAsync(request, cancellationToken);
            if (!result.IsSuccess)
            {
                counters.Failed++;
                firstError ??= result.Error;
                _logger.LogWarning("Poller workout ingest failed for connection {ConnectionId} workout {WorkoutId}: {Error}",
                    connectionId, workout.Id, result.Error);
                continue;
            }
            counters.TallyWorkout(result.Value!.Status);
        }

        return firstError;
    }

    private sealed class Counters
    {
        public int Skipped;
        public int RecoveryInserted, RecoveryUpdated, RecoveryNoOp, RecoverySkipped;
        public int SleepInserted, SleepUpdated, SleepNoOp, SleepSkipped;
        public int WorkoutInserted, WorkoutUpdated, WorkoutNoOp, WorkoutSkipped;
        public int Failed;

        public void TallyRecovery(RecoveryIngestionStatus s) { switch (s) {
            case RecoveryIngestionStatus.Inserted:      RecoveryInserted++; break;
            case RecoveryIngestionStatus.Updated:       RecoveryUpdated++;  break;
            case RecoveryIngestionStatus.NoOp:          RecoveryNoOp++;     break;
            case RecoveryIngestionStatus.SkippedNoData: RecoverySkipped++;  break;
        } }
        public void TallySleep(SleepIngestionStatus s) { switch (s) {
            case SleepIngestionStatus.Inserted:      SleepInserted++; break;
            case SleepIngestionStatus.Updated:       SleepUpdated++;  break;
            case SleepIngestionStatus.NoOp:          SleepNoOp++;     break;
            case SleepIngestionStatus.SkippedNoData: SleepSkipped++;  break;
        } }
        public void TallyWorkout(WorkoutIngestionStatus s) { switch (s) {
            case WorkoutIngestionStatus.Inserted:      WorkoutInserted++; break;
            case WorkoutIngestionStatus.Updated:       WorkoutUpdated++;  break;
            case WorkoutIngestionStatus.NoOp:          WorkoutNoOp++;     break;
            case WorkoutIngestionStatus.SkippedNoData: WorkoutSkipped++;  break;
        } }
    }
}

using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.Users;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.IngestionJobs.Jobs;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Recovery;
using SomaCore.Infrastructure.Sleep;
using SomaCore.Infrastructure.Whoop;
using SomaCore.Infrastructure.Workout;
using SomaCore.IntegrationTests.Observability;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// Integration coverage for <see cref="ReconciliationPoller"/> Session 4
/// extensions. The poller is a one-shot <see cref="IJob"/>; the per-user
/// adaptive scheduling lives in the Container Apps Jobs cron, not in this
/// code. Tests assert behavior within a single <see cref="IJob.RunAsync"/>
/// invocation.
/// </summary>
[Collection(nameof(TracingCollection))]
public class ReconciliationPollerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_poller")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    private SomaCoreDbContext _db = null!;
    private Guid _connectionId;
    private IWhoopApiClient _api = null!;
    private IWhoopAccessTokenCache _tokens = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<SomaCoreDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;
        _db = new SomaCoreDbContext(options);
        await _db.Database.MigrateAsync();

        var user = new User
        {
            EntraOid = Guid.NewGuid(),
            EntraTenantId = Guid.NewGuid(),
            Email = "poller-test@example.com",
            DisplayName = "Poller Test",
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var connection = new ExternalConnection
        {
            UserId = user.Id,
            Source = ConnectionSource.Whoop,
            Status = ConnectionStatus.Active,
            KeyVaultSecretName = $"whoop-refresh-{user.Id}",
            Scopes = new[] { "read:recovery", "read:sleep", "read:workout", "offline" },
            ConnectionMetadata = JsonDocument.Parse("""{"whoop_user_id":12345}"""),
        };
        _db.ExternalConnections.Add(connection);
        await _db.SaveChangesAsync();
        _connectionId = connection.Id;

        _api = Substitute.For<IWhoopApiClient>();
        _tokens = Substitute.For<IWhoopAccessTokenCache>();
        _tokens.GetAccessTokenAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("fake-token"));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Cycle_pull_ingests_recovery_and_sleep_for_each_connection_with_correct_trace_shape()
    {
        using var capture = TraceAssertions.Capture();

        var sleepUuid = Guid.NewGuid();
        const long cycleId = 8001;

        // Recovery handler's "no CycleId, no SleepId" branch goes through
        // ListRecentRecoveriesAsync; the latest record becomes the payload.
        _api.ListRecentRecoveriesAsync("fake-token", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<WhoopRecoveryListResponse>.Success(new WhoopRecoveryListResponse(
                new[] { ScoredRecoveryPayload(cycleId, sleepUuid) }, NextToken: null)));
        _api.GetCycleAsync("fake-token", cycleId, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopCyclePayload>.Success(new WhoopCyclePayload(
                cycleId, 12345,
                DateTimeOffset.UtcNow.AddHours(-9), DateTimeOffset.UtcNow.AddHours(-1),
                ScoreState.Scored)));
        _api.GetSleepByCycleAsync("fake-token", cycleId, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopSleepPayload?>.Success(ScoredSleepPayload(sleepUuid)));
        // No workouts.
        _api.ListRecentWorkoutsAsync("fake-token", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<WhoopWorkoutListResponse>.Success(new WhoopWorkoutListResponse(
                Array.Empty<WhoopWorkoutPayload>(), NextToken: null)));

        var poller = BuildPoller();
        var outcome = await poller.RunAsync(CancellationToken.None);

        outcome.Success.Should().BeTrue();
        (await _db.WhoopRecoveries.CountAsync(r => r.WhoopCycleId == cycleId)).Should().Be(1);
        (await _db.WhoopSleeps.CountAsync(s => s.WhoopSleepId == sleepUuid)).Should().Be(1);

        // Cycle pull trace root.
        var cycleRoots = capture.Activities
            .Where(a => a.OperationName == "whoop.ingestion"
                     && a.GetTagItem(IngestionTracing.Tags.IngestionEventType) as string == "cycle.pull")
            .ToList();
        cycleRoots.Should().HaveCount(1, "one cycle.pull root per (user, cycle)");
        var cycleRoot = cycleRoots[0];
        cycleRoot.GetTagItem(IngestionTracing.Tags.IngestionTrigger).Should().Be("poller");
        cycleRoot.GetTagItem("outcomes.recovery").Should().Be(IngestionTracing.Outcomes.Inserted);
        cycleRoot.GetTagItem("outcomes.sleep").Should().Be(IngestionTracing.Outcomes.Inserted);
        cycleRoot.GetTagItem("outcomes.workout").Should().Be(IngestionTracing.Outcomes.NotInvoked);
    }

    [Fact]
    public async Task Workout_pull_emits_one_trace_root_per_workout_with_recovery_and_sleep_NotInvoked()
    {
        using var capture = TraceAssertions.Capture();

        // No cycle to find -> recovery is SkippedNoData on the cycle pull side.
        _api.ListRecentRecoveriesAsync("fake-token", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<WhoopRecoveryListResponse>.Success(new WhoopRecoveryListResponse(
                Array.Empty<WhoopRecoveryPayload>(), NextToken: null)));

        var workoutA = Guid.NewGuid();
        var workoutB = Guid.NewGuid();
        _api.ListRecentWorkoutsAsync("fake-token", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<WhoopWorkoutListResponse>.Success(new WhoopWorkoutListResponse(
                new[] { ScoredWorkoutPayload(workoutA), ScoredWorkoutPayload(workoutB) }, NextToken: null)));
        _api.GetWorkoutByIdAsync("fake-token", workoutA, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopWorkoutPayload?>.Success(ScoredWorkoutPayload(workoutA)));
        _api.GetWorkoutByIdAsync("fake-token", workoutB, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopWorkoutPayload?>.Success(ScoredWorkoutPayload(workoutB)));

        var poller = BuildPoller();
        var outcome = await poller.RunAsync(CancellationToken.None);

        outcome.Success.Should().BeTrue();
        (await _db.WhoopWorkouts.CountAsync()).Should().Be(2);

        var workoutRoots = capture.Activities
            .Where(a => a.OperationName == "whoop.ingestion"
                     && a.GetTagItem(IngestionTracing.Tags.IngestionEventType) as string == "workout.pull")
            .ToList();
        workoutRoots.Should().HaveCount(2, "one workout.pull root per (user, workout)");
        foreach (var root in workoutRoots)
        {
            root.GetTagItem(IngestionTracing.Tags.IngestionTrigger).Should().Be("poller");
            root.GetTagItem("outcomes.recovery").Should().Be(IngestionTracing.Outcomes.NotInvoked);
            root.GetTagItem("outcomes.sleep").Should().Be(IngestionTracing.Outcomes.NotInvoked);
            root.GetTagItem("outcomes.workout").Should().Be(IngestionTracing.Outcomes.Inserted);
        }
    }

    [Fact]
    public async Task Skips_connection_with_recent_SCORED_recovery_and_marks_outcome_Skipped()
    {
        using var capture = TraceAssertions.Capture();

        // Seed a SCORED recovery whose cycle ended an hour before our fixed
        // "now" — well within the 36-hour active-cycle window. Stop-condition
        // gating fires; no ingestion runs.
        var existingRecovery = new SomaCore.Domain.WhoopRecoveries.WhoopRecovery
        {
            UserId = (await _db.ExternalConnections.SingleAsync()).UserId,
            ExternalConnectionId = _connectionId,
            WhoopCycleId = 9999,
            ScoreState = ScoreState.Scored,
            RecoveryScore = 75,
            CycleStartAt = FixedNow.AddHours(-10),
            CycleEndAt = FixedNow.AddHours(-1),
            IngestedVia = IngestedVia.Webhook,
            IngestedAt = FixedNow.AddHours(-1),
            RawPayload = JsonDocument.Parse("""{}"""),
        };
        _db.WhoopRecoveries.Add(existingRecovery);
        await _db.SaveChangesAsync();

        var poller = BuildPoller();
        var outcome = await poller.RunAsync(CancellationToken.None);

        outcome.Success.Should().BeTrue();
        (await _db.WhoopRecoveries.CountAsync()).Should().Be(1, "only the seeded recovery should exist");
        (await _db.WhoopSleeps.CountAsync()).Should().Be(0);
        (await _db.WhoopWorkouts.CountAsync()).Should().Be(0);

        var refreshed = await _db.ExternalConnections.AsNoTracking().SingleAsync(c => c.Id == _connectionId);
        refreshed.LastPolledAt.Should().NotBeNull();
        refreshed.LastPollOutcome.Should().Be(PollOutcome.Skipped);

        // Per ADR 0011 + Session 4.5 contract: skip path emits no ingestion roots.
        capture.Activities.Where(a => a.OperationName == "whoop.ingestion").Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_connection_polled_too_recently_and_marks_outcome_Skipped()
    {
        // Connection was polled 10 min ago; default minimum interval is 30 min.
        var connection = await _db.ExternalConnections.SingleAsync();
        connection.LastPolledAt = FixedNow.AddMinutes(-10);
        connection.LastPollOutcome = PollOutcome.Polled;
        await _db.SaveChangesAsync();

        var poller = BuildPoller();
        var outcome = await poller.RunAsync(CancellationToken.None);

        outcome.Success.Should().BeTrue();
        (await _db.WhoopRecoveries.CountAsync()).Should().Be(0);
        (await _db.WhoopWorkouts.CountAsync()).Should().Be(0);

        var refreshed = await _db.ExternalConnections.AsNoTracking().SingleAsync(c => c.Id == _connectionId);
        refreshed.LastPollOutcome.Should().Be(PollOutcome.Skipped);
        // LastPolledAt should be bumped to FixedNow (the tick happened, just no work).
        refreshed.LastPolledAt.Should().Be(FixedNow);
    }

    [Fact]
    public async Task Polls_cold_start_connection_in_UTC_window_and_marks_outcome_Polled()
    {
        // No recoveries, no sleeps → cold start. FixedNow = 07:00 UTC is
        // inside the cold-start window (4-11 UTC), so gating chooses Poll.
        var sleepUuid = Guid.NewGuid();
        const long cycleId = 8050;

        _api.ListRecentRecoveriesAsync("fake-token", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<WhoopRecoveryListResponse>.Success(new WhoopRecoveryListResponse(
                new[] { ScoredRecoveryPayload(cycleId, sleepUuid) }, NextToken: null)));
        _api.GetCycleAsync("fake-token", cycleId, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopCyclePayload>.Success(new WhoopCyclePayload(
                cycleId, 12345,
                FixedNow.AddHours(-9), FixedNow.AddHours(-1),
                ScoreState.Scored)));
        _api.GetSleepByCycleAsync("fake-token", cycleId, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopSleepPayload?>.Success(ScoredSleepPayload(sleepUuid)));
        _api.ListRecentWorkoutsAsync("fake-token", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<WhoopWorkoutListResponse>.Success(new WhoopWorkoutListResponse(
                Array.Empty<WhoopWorkoutPayload>(), NextToken: null)));

        var poller = BuildPoller();
        var outcome = await poller.RunAsync(CancellationToken.None);

        outcome.Success.Should().BeTrue();
        (await _db.WhoopRecoveries.CountAsync(r => r.WhoopCycleId == cycleId)).Should().Be(1);
        (await _db.WhoopSleeps.CountAsync(s => s.WhoopSleepId == sleepUuid)).Should().Be(1);

        var refreshed = await _db.ExternalConnections.AsNoTracking().SingleAsync(c => c.Id == _connectionId);
        refreshed.LastPolledAt.Should().Be(FixedNow);
        refreshed.LastPollOutcome.Should().Be(PollOutcome.Polled);
    }

    [Fact]
    public async Task Cycle_pull_with_no_sleep_yields_SkippedNoData_for_sleep_on_root()
    {
        using var capture = TraceAssertions.Capture();

        var sleepUuid = Guid.NewGuid();
        const long cycleId = 8888;

        _api.ListRecentRecoveriesAsync("fake-token", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<WhoopRecoveryListResponse>.Success(new WhoopRecoveryListResponse(
                new[] { ScoredRecoveryPayload(cycleId, sleepUuid) }, NextToken: null)));
        _api.GetCycleAsync("fake-token", cycleId, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopCyclePayload>.Success(new WhoopCyclePayload(
                cycleId, 12345,
                DateTimeOffset.UtcNow.AddHours(-9), DateTimeOffset.UtcNow.AddHours(-1),
                ScoreState.Scored)));
        // 404 on sleep -> WHOOP has no sleep for this cycle.
        _api.GetSleepByCycleAsync("fake-token", cycleId, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopSleepPayload?>.Success(null));
        _api.ListRecentWorkoutsAsync("fake-token", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<WhoopWorkoutListResponse>.Success(new WhoopWorkoutListResponse(
                Array.Empty<WhoopWorkoutPayload>(), NextToken: null)));

        var poller = BuildPoller();
        var outcome = await poller.RunAsync(CancellationToken.None);

        outcome.Success.Should().BeTrue();
        (await _db.WhoopRecoveries.CountAsync(r => r.WhoopCycleId == cycleId)).Should().Be(1);
        (await _db.WhoopSleeps.CountAsync()).Should().Be(0);

        var cycleRoot = capture.Activities
            .Single(a => a.OperationName == "whoop.ingestion"
                      && a.GetTagItem(IngestionTracing.Tags.IngestionEventType) as string == "cycle.pull");
        cycleRoot.GetTagItem("outcomes.recovery").Should().Be(IngestionTracing.Outcomes.Inserted);
        cycleRoot.GetTagItem("outcomes.sleep").Should().Be(IngestionTracing.Outcomes.SkippedNoData);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Default fixed "now" for these tests — 07:00 UTC, inside the cold-start
    /// wake window (4-11 UTC). Tests that need a different time pass one in.
    /// Without a fixed clock, gating decisions would depend on the wall-clock
    /// hour and tests would flake at the wrong time of day.
    /// </summary>
    private static readonly DateTimeOffset FixedNow = new(2026, 5, 24, 7, 0, 0, TimeSpan.Zero);

    private ReconciliationPoller BuildPoller(DateTimeOffset? now = null) => new(
        _db,
        new RecoveryIngestionHandler(_db, _api, _tokens, NullLogger<RecoveryIngestionHandler>.Instance),
        new WhoopSleepIngestionHandler(_db, _api, _tokens, NullLogger<WhoopSleepIngestionHandler>.Instance),
        new WhoopWorkoutIngestionHandler(_db, _api, _tokens, NullLogger<WhoopWorkoutIngestionHandler>.Instance),
        _api,
        _tokens,
        NullLogger<ReconciliationPoller>.Instance,
        clock: () => now ?? FixedNow);

    private static WhoopRecoveryPayload ScoredRecoveryPayload(long cycleId, Guid sleepUuid) =>
        new(cycleId, sleepUuid, 12345,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ScoreState.Scored,
            new WhoopRecoveryScore(67, 42.1234m, 55, null, null, false));

    private static WhoopSleepPayload ScoredSleepPayload(Guid id) => new(
        id, 12345, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddHours(-9), DateTimeOffset.UtcNow.AddHours(-1),
        "-07:00", false, ScoreState.Scored,
        new WhoopSleepScore(
            new WhoopSleepStageSummary(30_000_000, 1_500_000, 0, 18_000_000, 6_000_000, 4_500_000, 5, 8),
            87.5m, 80.0m, 95.2m, 15.1m));

    private static WhoopWorkoutPayload ScoredWorkoutPayload(Guid id) => new(
        id, 12345, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1),
        "-07:00", "running", ScoreState.Scored,
        new WhoopWorkoutScore(12.5m, 145m, 178m, 2100.5m));
}

using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using SomaCore.Api.Whoop;
using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.Users;
using SomaCore.Domain.WebhookEvents;
using SomaCore.Domain.WhoopRecoveries;
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
/// End-to-end coverage for <see cref="WhoopWebhookDrainer"/> — deferred from
/// Session 2, landed in Session 4 alongside the poller fan-out (which shares
/// the pre-seed-then-execute trace pattern).
///
/// **Scaffolding decision.** Rather than running the <see cref="BackgroundService"/>
/// lifecycle (slow, requires timing-sensitive polling for the SUT to claim
/// the seeded row), <see cref="WhoopWebhookDrainer.ProcessOneAsync"/> is
/// exposed as <c>internal</c> and tests drive it directly with a controlled
/// <see cref="IServiceProvider"/>. Faster, deterministic, and matches how
/// individual handlers are already tested. Future end-to-end tests for new
/// dispatch paths should follow this pattern.
/// </summary>
[Collection(nameof(TracingCollection))]
public class WhoopWebhookDrainerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_drainer_e2e")
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
            Email = "drainer-e2e@example.com",
            DisplayName = "Drainer E2E",
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
    public async Task Recovery_updated_webhook_ingests_both_recovery_and_sleep_rows_with_correct_trace_shape()
    {
        using var capture = TraceAssertions.Capture();

        var sleepUuid = Guid.NewGuid();
        const long cycleId = 7001;

        _api.ListRecentRecoveriesAsync("fake-token", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<WhoopRecoveryListResponse>.Success(new WhoopRecoveryListResponse(
                new[] { ScoredRecoveryPayload(cycleId, sleepUuid) }, NextToken: null)));
        _api.GetRecoveryByCycleAsync("fake-token", cycleId, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopRecoveryPayload?>.Success(ScoredRecoveryPayload(cycleId, sleepUuid)));
        _api.GetCycleAsync("fake-token", cycleId, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopCyclePayload>.Success(new WhoopCyclePayload(
                cycleId, 12345,
                DateTimeOffset.UtcNow.AddHours(-9), DateTimeOffset.UtcNow.AddHours(-1),
                ScoreState.Scored)));
        _api.GetSleepByCycleAsync("fake-token", cycleId, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopSleepPayload?>.Success(ScoredSleepPayload(sleepUuid)));

        await DriveDrainerAsync(eventType: "recovery.updated", sourceEventId: sleepUuid.ToString());

        // Both rows landed.
        (await _db.WhoopRecoveries.CountAsync(r => r.WhoopCycleId == cycleId)).Should().Be(1);
        (await _db.WhoopSleeps.CountAsync(s => s.WhoopSleepId == sleepUuid)).Should().Be(1);

        // Trace shape: root with all three outcome rollups, recovery and sleep ingest children.
        var root = TraceAssertions.AssertIngestionSpanShape(
            capture.Activities,
            expectedRootName: "whoop.ingestion",
            expectedChildren: new[] { "whoop.cycle.fetch", "recovery.ingest", "sleep.ingest" });
        root.GetTagItem(IngestionTracing.Tags.IngestionTrigger).Should().Be("webhook");
        root.GetTagItem(IngestionTracing.Tags.IngestionEventType).Should().Be("recovery.updated");
        root.GetTagItem("outcomes.recovery").Should().Be(IngestionTracing.Outcomes.Inserted);
        root.GetTagItem("outcomes.sleep").Should().Be(IngestionTracing.Outcomes.Inserted);
        root.GetTagItem("outcomes.workout").Should().Be(IngestionTracing.Outcomes.NotInvoked);

        // Webhook row marked processed.
        var row = await _db.WebhookEvents.SingleAsync();
        row.Status.Should().Be(WebhookEventStatus.Processed);
    }

    [Fact]
    public async Task Sleep_updated_webhook_takes_same_cycle_fan_out_path_as_recovery_updated()
    {
        using var capture = TraceAssertions.Capture();

        var sleepUuid = Guid.NewGuid();
        const long cycleId = 7050;

        _api.ListRecentRecoveriesAsync("fake-token", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<WhoopRecoveryListResponse>.Success(new WhoopRecoveryListResponse(
                new[] { ScoredRecoveryPayload(cycleId, sleepUuid) }, NextToken: null)));
        _api.GetRecoveryByCycleAsync("fake-token", cycleId, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopRecoveryPayload?>.Success(ScoredRecoveryPayload(cycleId, sleepUuid)));
        _api.GetCycleAsync("fake-token", cycleId, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopCyclePayload>.Success(new WhoopCyclePayload(
                cycleId, 12345,
                DateTimeOffset.UtcNow.AddHours(-9), DateTimeOffset.UtcNow.AddHours(-1),
                ScoreState.Scored)));
        _api.GetSleepByCycleAsync("fake-token", cycleId, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopSleepPayload?>.Success(ScoredSleepPayload(sleepUuid)));

        await DriveDrainerAsync(eventType: "sleep.updated", sourceEventId: sleepUuid.ToString());

        (await _db.WhoopRecoveries.CountAsync(r => r.WhoopCycleId == cycleId)).Should().Be(1);
        (await _db.WhoopSleeps.CountAsync(s => s.WhoopSleepId == sleepUuid)).Should().Be(1);

        var root = TraceAssertions.AssertIngestionSpanShape(
            capture.Activities,
            expectedRootName: "whoop.ingestion",
            expectedChildren: new[] { "whoop.cycle.fetch", "recovery.ingest", "sleep.ingest" });
        root.GetTagItem(IngestionTracing.Tags.IngestionEventType).Should().Be("sleep.updated");
        root.GetTagItem("outcomes.workout").Should().Be(IngestionTracing.Outcomes.NotInvoked);
    }

    [Fact]
    public async Task Workout_updated_webhook_ingests_only_workout_row_with_recovery_and_sleep_NotInvoked()
    {
        using var capture = TraceAssertions.Capture();

        var workoutUuid = Guid.NewGuid();
        _api.GetWorkoutByIdAsync("fake-token", workoutUuid, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopWorkoutPayload?>.Success(ScoredWorkoutPayload(workoutUuid)));

        await DriveDrainerAsync(eventType: "workout.updated", sourceEventId: workoutUuid.ToString());

        (await _db.WhoopWorkouts.CountAsync(w => w.WhoopWorkoutId == workoutUuid)).Should().Be(1);
        (await _db.WhoopRecoveries.CountAsync()).Should().Be(0);
        (await _db.WhoopSleeps.CountAsync()).Should().Be(0);

        var root = TraceAssertions.AssertIngestionSpanShape(
            capture.Activities,
            expectedRootName: "whoop.ingestion",
            expectedChildren: new[] { "whoop.workout.fetch", "workout.ingest" });
        root.GetTagItem(IngestionTracing.Tags.IngestionEventType).Should().Be("workout.updated");
        root.GetTagItem("outcomes.recovery").Should().Be(IngestionTracing.Outcomes.NotInvoked);
        root.GetTagItem("outcomes.sleep").Should().Be(IngestionTracing.Outcomes.NotInvoked);
        root.GetTagItem("outcomes.workout").Should().Be(IngestionTracing.Outcomes.Inserted);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Seed a webhook_events row and call <see cref="WhoopWebhookDrainer.ProcessOneAsync"/>
    /// directly. The BackgroundService lifecycle (DrainOnceAsync /
    /// SELECT FOR UPDATE SKIP LOCKED) is not exercised — it's a separate
    /// concern (queue claim semantics) and is covered indirectly by the
    /// per-handler tests that already prove idempotency.
    /// </summary>
    private async Task DriveDrainerAsync(string eventType, string sourceEventId)
    {
        var evt = new WebhookEvent
        {
            Source = WebhookEventSource.Whoop,
            SourceEventId = sourceEventId,
            SourceTraceId = $"trace-{Guid.NewGuid():N}",
            EventType = eventType,
            UserId = _db.ExternalConnections.Single().UserId,
            ExternalConnectionId = _connectionId,
            Status = WebhookEventStatus.Processing,
            ReceivedAt = DateTimeOffset.UtcNow,
            RawBody = JsonDocument.Parse($$"""{"id":"{{sourceEventId}}","type":"{{eventType}}"}"""),
            SignatureHeader = "fake-sig",
            SignatureTimestampHeader = "fake-ts",
        };
        _db.WebhookEvents.Add(evt);
        await _db.SaveChangesAsync();

        // Build a service provider that the drainer's per-event scope reaches
        // into. The drainer normally creates this scope from IServiceScopeFactory;
        // ProcessOneAsync takes the provider directly so we can hand-build it.
        var services = new ServiceCollection();
        services.AddSingleton<SomaCoreDbContext>(_db);
        services.AddSingleton<IWhoopApiClient>(_api);
        services.AddSingleton<IWhoopAccessTokenCache>(_tokens);
        services.AddSingleton<IRecoveryIngestionHandler>(sp => new RecoveryIngestionHandler(
            sp.GetRequiredService<SomaCoreDbContext>(),
            sp.GetRequiredService<IWhoopApiClient>(),
            sp.GetRequiredService<IWhoopAccessTokenCache>(),
            NullLogger<RecoveryIngestionHandler>.Instance));
        services.AddSingleton<IWhoopSleepIngestionHandler>(sp => new WhoopSleepIngestionHandler(
            sp.GetRequiredService<SomaCoreDbContext>(),
            sp.GetRequiredService<IWhoopApiClient>(),
            sp.GetRequiredService<IWhoopAccessTokenCache>(),
            NullLogger<WhoopSleepIngestionHandler>.Instance));
        services.AddSingleton<IWhoopWorkoutIngestionHandler>(sp => new WhoopWorkoutIngestionHandler(
            sp.GetRequiredService<SomaCoreDbContext>(),
            sp.GetRequiredService<IWhoopApiClient>(),
            sp.GetRequiredService<IWhoopAccessTokenCache>(),
            NullLogger<WhoopWorkoutIngestionHandler>.Instance));
        var provider = services.BuildServiceProvider();

        var drainer = new WhoopWebhookDrainer(
            scopeFactory: Substitute.For<IServiceScopeFactory>(), // unused by ProcessOneAsync
            logger: NullLogger<WhoopWebhookDrainer>.Instance);

        await drainer.ProcessOneAsync(provider, evt, CancellationToken.None);
    }

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

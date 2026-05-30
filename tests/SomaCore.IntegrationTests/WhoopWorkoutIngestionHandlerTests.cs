using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.Users;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Whoop;
using SomaCore.Infrastructure.Workout;
using SomaCore.IntegrationTests.Observability;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// Mirrors <see cref="WhoopSleepIngestionHandlerTests"/>. Covers:
///   - Three-path convergence on <c>(external_connection_id, whoop_workout_id)</c>
///   - PENDING_SCORE -> SCORED transition produces Updated, not a second row
///   - SkippedNoData when WHOOP returns 404 for /activity/workout/{id}
///   - ADR 0011 handler-span shape (one <c>workout.ingest</c> span with required tags)
/// </summary>
[Collection(nameof(TracingCollection))]
public class WhoopWorkoutIngestionHandlerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_workout_handler")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    private SomaCoreDbContext _db = null!;
    private Guid _connectionId;

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
            Email = "workout-test@example.com",
            DisplayName = "Workout Test",
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
            Scopes = new[] { "read:workout", "offline" },
            ConnectionMetadata = JsonDocument.Parse("""{"whoop_user_id":12345}"""),
        };
        _db.ExternalConnections.Add(connection);
        await _db.SaveChangesAsync();
        _connectionId = connection.Id;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Three_paths_converge_on_one_row_via_idempotency_on_connection_and_workout_id()
    {
        var apiClient = Substitute.For<IWhoopApiClient>();
        var tokenCache = Substitute.For<IWhoopAccessTokenCache>();
        tokenCache.GetAccessTokenAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("fake-token"));

        var workoutUuid = Guid.NewGuid();
        var payload = ScoredWorkout(workoutUuid);
        apiClient.GetWorkoutByIdAsync("fake-token", workoutUuid, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopWorkoutPayload?>.Success(payload));

        var handler = new WhoopWorkoutIngestionHandler(
            _db, apiClient, tokenCache, NullLogger<WhoopWorkoutIngestionHandler>.Instance);

        var webhook = await handler.IngestAsync(
            new WorkoutIngestionRequest(_connectionId, IngestedVia.Webhook, WorkoutId: workoutUuid, TraceId: "t1"),
            CancellationToken.None);
        var poller = await handler.IngestAsync(
            new WorkoutIngestionRequest(_connectionId, IngestedVia.Poller, WorkoutId: workoutUuid, TraceId: "t2"),
            CancellationToken.None);
        var onOpen = await handler.IngestAsync(
            new WorkoutIngestionRequest(_connectionId, IngestedVia.OnOpenPull, WorkoutId: workoutUuid, TraceId: "t3"),
            CancellationToken.None);

        webhook.IsSuccess.Should().BeTrue();
        webhook.Value!.Status.Should().Be(WorkoutIngestionStatus.Inserted);
        poller.Value!.Status.Should().Be(WorkoutIngestionStatus.NoOp);
        onOpen.Value!.Status.Should().Be(WorkoutIngestionStatus.NoOp);

        var count = await _db.WhoopWorkouts
            .Where(w => w.ExternalConnectionId == _connectionId && w.WhoopWorkoutId == workoutUuid)
            .CountAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task Pending_score_row_gets_Updated_when_score_lands()
    {
        var apiClient = Substitute.For<IWhoopApiClient>();
        var tokenCache = Substitute.For<IWhoopAccessTokenCache>();
        tokenCache.GetAccessTokenAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("tok"));

        var workoutUuid = Guid.NewGuid();
        var pending = ScoredWorkout(workoutUuid) with { ScoreState = ScoreState.PendingScore, Score = null };
        var scored = ScoredWorkout(workoutUuid);

        apiClient.GetWorkoutByIdAsync("tok", workoutUuid, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopWorkoutPayload?>.Success(pending),
                     Result<WhoopWorkoutPayload?>.Success(scored));

        var handler = new WhoopWorkoutIngestionHandler(
            _db, apiClient, tokenCache, NullLogger<WhoopWorkoutIngestionHandler>.Instance);

        var first = await handler.IngestAsync(
            new WorkoutIngestionRequest(_connectionId, IngestedVia.Webhook, workoutUuid, TraceId: "a"),
            CancellationToken.None);
        first.Value!.Status.Should().Be(WorkoutIngestionStatus.Inserted);
        first.Value.ScoreState.Should().Be(ScoreState.PendingScore);

        var second = await handler.IngestAsync(
            new WorkoutIngestionRequest(_connectionId, IngestedVia.Poller, workoutUuid, TraceId: "b"),
            CancellationToken.None);
        second.Value!.Status.Should().Be(WorkoutIngestionStatus.Updated);
        second.Value.ScoreState.Should().Be(ScoreState.Scored);

        var rows = await _db.WhoopWorkouts
            .Where(w => w.ExternalConnectionId == _connectionId && w.WhoopWorkoutId == workoutUuid)
            .ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].ScoreState.Should().Be(ScoreState.Scored);
        rows[0].Strain.Should().Be(12.5m);
        rows[0].AverageHeartRate.Should().Be(145);
        rows[0].MaxHeartRate.Should().Be(178);
        rows[0].IngestedVia.Should().Be(IngestedVia.Poller);
    }

    [Fact]
    public async Task Returns_SkippedNoData_when_WHOOP_returns_404_for_the_workout()
    {
        var apiClient = Substitute.For<IWhoopApiClient>();
        var tokenCache = Substitute.For<IWhoopAccessTokenCache>();
        tokenCache.GetAccessTokenAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("tok"));

        var workoutUuid = Guid.NewGuid();
        apiClient.GetWorkoutByIdAsync("tok", workoutUuid, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopWorkoutPayload?>.Success(null));

        var handler = new WhoopWorkoutIngestionHandler(
            _db, apiClient, tokenCache, NullLogger<WhoopWorkoutIngestionHandler>.Instance);

        var result = await handler.IngestAsync(
            new WorkoutIngestionRequest(_connectionId, IngestedVia.Webhook, workoutUuid, TraceId: "z"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(WorkoutIngestionStatus.SkippedNoData);
        (await _db.WhoopWorkouts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Emits_workout_ingest_span_with_ADR_0011_required_tags_and_rollup()
    {
        using var capture = TraceAssertions.Capture();

        var apiClient = Substitute.For<IWhoopApiClient>();
        var tokenCache = Substitute.For<IWhoopAccessTokenCache>();
        tokenCache.GetAccessTokenAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("tok"));
        var workoutUuid = Guid.NewGuid();
        apiClient.GetWorkoutByIdAsync("tok", workoutUuid, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopWorkoutPayload?>.Success(ScoredWorkout(workoutUuid)));

        var handler = new WhoopWorkoutIngestionHandler(
            _db, apiClient, tokenCache, NullLogger<WhoopWorkoutIngestionHandler>.Instance);

        // Open the root scope inline (as the drainer would). Pre-seed all
        // three rollup outcomes per the amended ADR 0011 contract; the
        // handler then overwrites outcomes.workout.
        using (var rootScope = IngestionTracing.StartIngestionScope(
            source: "whoop",
            trigger: "webhook",
            eventType: "workout.updated",
            externalConnectionId: _connectionId,
            upstreamTraceId: "trace-workout-test"))
        {
            IngestionTracing.RecordOutcome(rootScope, "recovery", IngestionTracing.Outcomes.NotInvoked);
            IngestionTracing.RecordOutcome(rootScope, "sleep",    IngestionTracing.Outcomes.NotInvoked);
            IngestionTracing.RecordOutcome(rootScope, "workout",  IngestionTracing.Outcomes.NotInvoked);

            var result = await handler.IngestAsync(
                new WorkoutIngestionRequest(_connectionId, IngestedVia.Webhook, workoutUuid, TraceId: "trace-workout-test"),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            result.Value!.Status.Should().Be(WorkoutIngestionStatus.Inserted);
        }

        var root = TraceAssertions.AssertIngestionSpanShape(
            capture.Activities,
            expectedRootName: "whoop.ingestion",
            expectedChildren: new[] { "workout.ingest" });

        // All three rollup tags present (the amended NotInvoked semantics).
        root.GetTagItem("outcomes.workout").Should().Be(IngestionTracing.Outcomes.Inserted);
        root.GetTagItem("outcomes.recovery").Should().Be(IngestionTracing.Outcomes.NotInvoked);
        root.GetTagItem("outcomes.sleep").Should().Be(IngestionTracing.Outcomes.NotInvoked);

        var handlerSpan = capture.Activities.Single(a => a.OperationName == "workout.ingest");
        handlerSpan.GetTagItem(IngestionTracing.Tags.HandlerName).Should().Be("workout_ingestion");
        handlerSpan.GetTagItem(IngestionTracing.Tags.HandlerOutcome).Should().Be(IngestionTracing.Outcomes.Inserted);
        handlerSpan.GetTagItem(IngestionTracing.Tags.EntityNaturalKey).Should().Be(workoutUuid.ToString());
        handlerSpan.GetTagItem(IngestionTracing.Tags.ScoreState).Should().Be(ScoreState.Scored);
    }

    private static WhoopWorkoutPayload ScoredWorkout(Guid id) => new(
        Id: id,
        UserId: 12345,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        Start: DateTimeOffset.UtcNow.AddHours(-2),
        End: DateTimeOffset.UtcNow.AddHours(-1),
        TimezoneOffset: "-07:00",
        SportName: "running",
        ScoreState: ScoreState.Scored,
        Score: new WhoopWorkoutScore(
            Strain: 12.5m,
            AverageHeartRate: 145m,
            MaxHeartRate: 178m,
            Kilojoule: 2100.5m));
}

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
using SomaCore.Infrastructure.Sleep;
using SomaCore.Infrastructure.Whoop;
using SomaCore.IntegrationTests.Observability;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// Mirrors <see cref="RecoveryIngestionHandlerTests"/> shape. Covers:
///   - Three-path convergence: webhook + poller + on_open_pull all dedupe on
///     <c>(external_connection_id, whoop_sleep_id)</c>.
///   - score_state transition: PENDING_SCORE -> SCORED produces Updated, not
///     a second row.
///   - SkippedNoData when WHOOP returns 404 for /cycle/{id}/sleep.
///   - ADR 0011 handler-span shape (one <c>sleep.ingest</c> span with required tags).
/// </summary>
[Collection(nameof(TracingCollection))]
public class WhoopSleepIngestionHandlerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_sleep_handler")
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
            Email = "sleep-test@example.com",
            DisplayName = "Sleep Test",
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
            Scopes = new[] { "read:sleep", "offline" },
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
    public async Task Three_paths_converge_on_one_row_via_idempotency_on_connection_and_sleep_id()
    {
        var apiClient = Substitute.For<IWhoopApiClient>();
        var tokenCache = Substitute.For<IWhoopAccessTokenCache>();
        tokenCache.GetAccessTokenAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("fake-token"));

        var sleepUuid = Guid.NewGuid();
        var payload = ScoredSleep(sleepUuid);
        apiClient.GetSleepByCycleAsync("fake-token", 5001, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopSleepPayload?>.Success(payload));

        var handler = new WhoopSleepIngestionHandler(
            _db, apiClient, tokenCache, NullLogger<WhoopSleepIngestionHandler>.Instance);

        var webhookResult = await handler.IngestAsync(
            new SleepIngestionRequest(_connectionId, IngestedVia.Webhook, CycleId: 5001, SleepId: sleepUuid, TraceId: "t1"),
            CancellationToken.None);
        var pollerResult = await handler.IngestAsync(
            new SleepIngestionRequest(_connectionId, IngestedVia.Poller, CycleId: 5001, SleepId: sleepUuid, TraceId: "t2"),
            CancellationToken.None);
        var onOpenResult = await handler.IngestAsync(
            new SleepIngestionRequest(_connectionId, IngestedVia.OnOpenPull, CycleId: 5001, SleepId: sleepUuid, TraceId: "t3"),
            CancellationToken.None);

        webhookResult.IsSuccess.Should().BeTrue();
        webhookResult.Value!.Status.Should().Be(SleepIngestionStatus.Inserted);
        pollerResult.Value!.Status.Should().Be(SleepIngestionStatus.NoOp);
        onOpenResult.Value!.Status.Should().Be(SleepIngestionStatus.NoOp);

        var rowCount = await _db.WhoopSleeps
            .Where(s => s.ExternalConnectionId == _connectionId && s.WhoopSleepId == sleepUuid)
            .CountAsync();
        rowCount.Should().Be(1);
    }

    [Fact]
    public async Task Pending_score_row_gets_Updated_when_score_lands()
    {
        var apiClient = Substitute.For<IWhoopApiClient>();
        var tokenCache = Substitute.For<IWhoopAccessTokenCache>();
        tokenCache.GetAccessTokenAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("tok"));

        var sleepUuid = Guid.NewGuid();
        var pending = ScoredSleep(sleepUuid) with { ScoreState = ScoreState.PendingScore, Score = null };
        var scored = ScoredSleep(sleepUuid);

        apiClient.GetSleepByCycleAsync("tok", 5050, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopSleepPayload?>.Success(pending),
                     Result<WhoopSleepPayload?>.Success(scored));

        var handler = new WhoopSleepIngestionHandler(
            _db, apiClient, tokenCache, NullLogger<WhoopSleepIngestionHandler>.Instance);

        var first = await handler.IngestAsync(
            new SleepIngestionRequest(_connectionId, IngestedVia.Webhook, CycleId: 5050, SleepId: sleepUuid, TraceId: "a"),
            CancellationToken.None);
        first.Value!.Status.Should().Be(SleepIngestionStatus.Inserted);
        first.Value.ScoreState.Should().Be(ScoreState.PendingScore);

        var second = await handler.IngestAsync(
            new SleepIngestionRequest(_connectionId, IngestedVia.Poller, CycleId: 5050, SleepId: sleepUuid, TraceId: "b"),
            CancellationToken.None);
        second.Value!.Status.Should().Be(SleepIngestionStatus.Updated);
        second.Value.ScoreState.Should().Be(ScoreState.Scored);

        var rows = await _db.WhoopSleeps
            .Where(s => s.ExternalConnectionId == _connectionId && s.WhoopSleepId == sleepUuid)
            .ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].ScoreState.Should().Be(ScoreState.Scored);
        rows[0].SleepPerformancePercentage.Should().Be(87.5m);
        rows[0].IngestedVia.Should().Be(IngestedVia.Poller);
    }

    [Fact]
    public async Task Returns_SkippedNoData_when_WHOOP_has_no_sleep_for_the_cycle()
    {
        var apiClient = Substitute.For<IWhoopApiClient>();
        var tokenCache = Substitute.For<IWhoopAccessTokenCache>();
        tokenCache.GetAccessTokenAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("tok"));

        apiClient.GetSleepByCycleAsync("tok", 9999, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopSleepPayload?>.Success(null));

        var handler = new WhoopSleepIngestionHandler(
            _db, apiClient, tokenCache, NullLogger<WhoopSleepIngestionHandler>.Instance);

        var result = await handler.IngestAsync(
            new SleepIngestionRequest(_connectionId, IngestedVia.Webhook, CycleId: 9999, SleepId: null, TraceId: "z"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(SleepIngestionStatus.SkippedNoData);
        (await _db.WhoopSleeps.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Emits_sleep_ingest_span_with_ADR_0011_required_tags()
    {
        using var capture = TraceAssertions.Capture();

        var apiClient = Substitute.For<IWhoopApiClient>();
        var tokenCache = Substitute.For<IWhoopAccessTokenCache>();
        tokenCache.GetAccessTokenAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("tok"));
        var sleepUuid = Guid.NewGuid();
        apiClient.GetSleepByCycleAsync("tok", 7000, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopSleepPayload?>.Success(ScoredSleep(sleepUuid)));

        var handler = new WhoopSleepIngestionHandler(
            _db, apiClient, tokenCache, NullLogger<WhoopSleepIngestionHandler>.Instance);

        // Open a root scope so RecordOutcome's parent-walk has somewhere to
        // land its rolled-up outcomes.sleep tag. The drainer normally provides
        // this; here we simulate it inline.
        using (var rootScope = IngestionTracing.StartIngestionScope(
            source: "whoop",
            trigger: "webhook",
            eventType: "sleep.updated",
            externalConnectionId: _connectionId,
            upstreamTraceId: "trace-sleep-test"))
        {
            var result = await handler.IngestAsync(
                new SleepIngestionRequest(_connectionId, IngestedVia.Webhook, CycleId: 7000, SleepId: sleepUuid, TraceId: "trace-sleep-test"),
                CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            result.Value!.Status.Should().Be(SleepIngestionStatus.Inserted);
        }

        var root = TraceAssertions.AssertIngestionSpanShape(
            capture.Activities,
            expectedRootName: "whoop.ingestion",
            expectedChildren: new[] { "sleep.ingest" });

        // Rolled-up outcomes tag.
        root.GetTagItem("outcomes.sleep").Should().Be(IngestionTracing.Outcomes.Inserted);

        // Handler span tags.
        var handlerSpan = capture.Activities.Single(a => a.OperationName == "sleep.ingest");
        handlerSpan.GetTagItem(IngestionTracing.Tags.HandlerName).Should().Be("sleep_ingestion");
        handlerSpan.GetTagItem(IngestionTracing.Tags.HandlerOutcome).Should().Be(IngestionTracing.Outcomes.Inserted);
        handlerSpan.GetTagItem(IngestionTracing.Tags.EntityNaturalKey).Should().Be(sleepUuid.ToString());
        handlerSpan.GetTagItem(IngestionTracing.Tags.ScoreState).Should().Be(ScoreState.Scored);
    }

    private static WhoopSleepPayload ScoredSleep(Guid id) => new(
        Id: id,
        UserId: 12345,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        Start: DateTimeOffset.UtcNow.AddHours(-9),
        End: DateTimeOffset.UtcNow.AddHours(-1),
        TimezoneOffset: "-07:00",
        Nap: false,
        ScoreState: ScoreState.Scored,
        Score: new WhoopSleepScore(
            StageSummary: new WhoopSleepStageSummary(
                TotalInBedTimeMilli: 30_000_000,
                TotalAwakeTimeMilli: 1_500_000,
                TotalNoDataTimeMilli: 0,
                TotalLightSleepTimeMilli: 18_000_000,
                TotalSlowWaveSleepTimeMilli: 6_000_000,
                TotalRemSleepTimeMilli: 4_500_000,
                SleepCycleCount: 5,
                DisturbanceCount: 8),
            SleepPerformancePercentage: 87.5m,
            SleepConsistencyPercentage: 80.0m,
            SleepEfficiencyPercentage: 95.2m,
            RespiratoryRate: 15.1m));
}

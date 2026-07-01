using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.OAuthAudit;
using SomaCore.Domain.Users;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Infrastructure.Backfill;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Recovery;
using SomaCore.Infrastructure.Sleep;
using SomaCore.Infrastructure.Whoop;
using SomaCore.Infrastructure.Workout;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// Session 5 backfill end-to-end. Covers:
///   - One full pass over recovery + sleep + workout list endpoints lands
///     rows tagged <c>ingested_via='backfill'</c> and writes one
///     <c>oauth_audit</c> row with <c>action='backfill'</c>.
///   - Re-running the same window is idempotent: every entity hits NoOp on
///     the second pass and no new rows are inserted.
///   - Connection-level guards return Failure (not found, not active) and
///     do NOT write data or an audit row.
/// </summary>
public class WhoopBackfillServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_backfill")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    private SomaCoreDbContext _db = null!;
    private Guid _userId;
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
            Email = "backfill-test@example.com",
            DisplayName = "Backfill Test",
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        _userId = user.Id;

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
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Single_pass_ingests_recoveries_sleeps_and_workouts_and_writes_one_audit_row()
    {
        var apiClient = BuildApiWithPayloads(
            recoveries: new[] { Recovery(cycleId: 100), Recovery(cycleId: 101) },
            sleeps: new[] { Sleep(), Sleep() },
            workouts: new[] { Workout(), Workout(), Workout() });
        var tokenCache = BuildTokenCache();

        var service = BuildService(apiClient, tokenCache);

        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-30);

        var result = await service.RunAsync(_connectionId, start, end, CancellationToken.None);

        result.IsSuccess.Should().BeTrue($"backfill must succeed but failed: {result.Error}");
        var s = result.Value!;
        s.RecoveriesInserted.Should().Be(2);
        s.SleepsInserted.Should().Be(2);
        s.WorkoutsInserted.Should().Be(3);
        s.FailedEntities.Should().Be(0);

        (await _db.WhoopRecoveries.CountAsync(r => r.ExternalConnectionId == _connectionId)).Should().Be(2);
        (await _db.WhoopSleeps.CountAsync(x => x.ExternalConnectionId == _connectionId)).Should().Be(2);
        (await _db.WhoopWorkouts.CountAsync(x => x.ExternalConnectionId == _connectionId)).Should().Be(3);

        // Everything tagged with the new ingested_via.
        (await _db.WhoopRecoveries.AllAsync(r => r.IngestedVia == IngestedVia.Backfill))
            .Should().BeTrue();
        (await _db.WhoopSleeps.AllAsync(x => x.IngestedVia == IngestedVia.Backfill))
            .Should().BeTrue();
        (await _db.WhoopWorkouts.AllAsync(x => x.IngestedVia == IngestedVia.Backfill))
            .Should().BeTrue();

        // Exactly one audit row, action=backfill, success=true, references the connection.
        var audits = await _db.OAuthAuditEntries
            .Where(a => a.Action == OAuthAuditAction.Backfill && a.UserId == _userId)
            .ToListAsync();
        audits.Should().HaveCount(1);
        audits[0].Success.Should().BeTrue();
        audits[0].ExternalConnectionId.Should().Be(_connectionId);
    }

    [Fact]
    public async Task Rerun_over_same_window_is_idempotent_all_NoOp_and_no_new_rows()
    {
        var recoveries = new[] { Recovery(cycleId: 200) };
        var sleeps = new[] { Sleep() };
        var workouts = new[] { Workout() };

        var apiClient = BuildApiWithPayloads(recoveries, sleeps, workouts);
        var tokenCache = BuildTokenCache();

        var service = BuildService(apiClient, tokenCache);

        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-7);

        var first = await service.RunAsync(_connectionId, start, end, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        first.Value!.RecoveriesInserted.Should().Be(1);
        first.Value!.SleepsInserted.Should().Be(1);
        first.Value!.WorkoutsInserted.Should().Be(1);

        var rowSnapshot = new
        {
            Recoveries = await _db.WhoopRecoveries.CountAsync(),
            Sleeps = await _db.WhoopSleeps.CountAsync(),
            Workouts = await _db.WhoopWorkouts.CountAsync(),
        };

        // Second run, same window, same payloads.
        var second = await service.RunAsync(_connectionId, start, end, CancellationToken.None);
        second.IsSuccess.Should().BeTrue();
        var s = second.Value!;
        s.RecoveriesInserted.Should().Be(0);
        s.RecoveriesUpdated.Should().Be(0);
        s.RecoveriesNoOp.Should().Be(1);
        s.SleepsInserted.Should().Be(0);
        s.SleepsUpdated.Should().Be(0);
        s.SleepsNoOp.Should().Be(1);
        s.WorkoutsInserted.Should().Be(0);
        s.WorkoutsUpdated.Should().Be(0);
        s.WorkoutsNoOp.Should().Be(1);

        (await _db.WhoopRecoveries.CountAsync()).Should().Be(rowSnapshot.Recoveries);
        (await _db.WhoopSleeps.CountAsync()).Should().Be(rowSnapshot.Sleeps);
        (await _db.WhoopWorkouts.CountAsync()).Should().Be(rowSnapshot.Workouts);

        // Two audit rows now — one per run.
        (await _db.OAuthAuditEntries.CountAsync(a => a.Action == OAuthAuditAction.Backfill))
            .Should().Be(2);
    }

    [Fact]
    public async Task Returns_Failure_when_connection_id_does_not_exist()
    {
        var service = BuildService(
            BuildApiWithPayloads(Array.Empty<WhoopRecoveryPayload>(),
                                 Array.Empty<WhoopSleepPayload>(),
                                 Array.Empty<WhoopWorkoutPayload>()),
            BuildTokenCache());

        var missingId = Guid.NewGuid();
        var result = await service.RunAsync(
            missingId,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain(missingId.ToString());
        (await _db.OAuthAuditEntries.AnyAsync(a => a.Action == OAuthAuditAction.Backfill))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Returns_Failure_when_connection_is_not_active()
    {
        var conn = await _db.ExternalConnections.FirstAsync(c => c.Id == _connectionId);
        conn.Status = ConnectionStatus.RefreshFailed;
        await _db.SaveChangesAsync();

        var service = BuildService(
            BuildApiWithPayloads(Array.Empty<WhoopRecoveryPayload>(),
                                 Array.Empty<WhoopSleepPayload>(),
                                 Array.Empty<WhoopWorkoutPayload>()),
            BuildTokenCache());

        var result = await service.RunAsync(
            _connectionId,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not active");
        (await _db.OAuthAuditEntries.AnyAsync(a => a.Action == OAuthAuditAction.Backfill))
            .Should().BeFalse();
    }

    // --- helpers -----------------------------------------------------------

    private WhoopBackfillService BuildService(IWhoopApiClient api, IWhoopAccessTokenCache tokens)
    {
        var recoveryHandler = new RecoveryIngestionHandler(
            _db, api, tokens, NullLogger<RecoveryIngestionHandler>.Instance);
        var sleepHandler = new WhoopSleepIngestionHandler(
            _db, api, tokens, NullLogger<WhoopSleepIngestionHandler>.Instance);
        var workoutHandler = new WhoopWorkoutIngestionHandler(
            _db, api, tokens, NullLogger<WhoopWorkoutIngestionHandler>.Instance);

        return new WhoopBackfillService(
            _db, api, tokens,
            recoveryHandler, sleepHandler, workoutHandler,
            NullLogger<WhoopBackfillService>.Instance);
    }

    private IWhoopAccessTokenCache BuildTokenCache()
    {
        var cache = Substitute.For<IWhoopAccessTokenCache>();
        cache.GetAccessTokenAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("fake-token"));
        return cache;
    }

    private static IWhoopApiClient BuildApiWithPayloads(
        IReadOnlyList<WhoopRecoveryPayload> recoveries,
        IReadOnlyList<WhoopSleepPayload> sleeps,
        IReadOnlyList<WhoopWorkoutPayload> workouts)
    {
        var api = Substitute.For<IWhoopApiClient>();

        api.ListRecoveriesAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>())
            .Returns(Result<WhoopRecoveryListResponse>.Success(
                new WhoopRecoveryListResponse(recoveries, NextToken: null)));

        api.ListSleepsAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>())
            .Returns(Result<WhoopSleepListResponse>.Success(
                new WhoopSleepListResponse(sleeps, NextToken: null)));

        api.ListWorkoutsAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(),
            Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<CancellationToken>())
            .Returns(Result<WhoopWorkoutListResponse>.Success(
                new WhoopWorkoutListResponse(workouts, NextToken: null)));

        // Backfill resolves the cycle envelope per recovery via GetCycleAsync;
        // return a synthetic envelope keyed by cycle_id so each recovery's
        // CycleStartAt is populated deterministically.
        api.GetCycleAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var cycleId = call.Arg<long>();
                var start = DateTimeOffset.UtcNow.Date.AddDays(-cycleId).ToUniversalTime();
                return Result<WhoopCyclePayload>.Success(new WhoopCyclePayload(
                    Id: cycleId,
                    UserId: 12345,
                    Start: start,
                    End: start.AddHours(24),
                    ScoreState: ScoreState.Scored));
            });

        return api;
    }

    private static WhoopRecoveryPayload Recovery(long cycleId) => new(
        CycleId: cycleId,
        SleepId: Guid.NewGuid(),
        UserId: 12345,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        ScoreState: ScoreState.Scored,
        Score: new WhoopRecoveryScore(
            RecoveryScore: 72m,
            HrvRmssdMilli: 41.3m,
            RestingHeartRate: 52m,
            Spo2Percentage: 97m,
            SkinTempCelsius: 33.4m,
            UserCalibrating: false));

    private static WhoopSleepPayload Sleep() => new(
        Id: Guid.NewGuid(),
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
            SleepPerformancePercentage: 88m,
            SleepConsistencyPercentage: 80m,
            SleepEfficiencyPercentage: 95m,
            RespiratoryRate: 15.1m));

    private static WhoopWorkoutPayload Workout() => new(
        Id: Guid.NewGuid(),
        UserId: 12345,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        Start: DateTimeOffset.UtcNow.AddHours(-3),
        End: DateTimeOffset.UtcNow.AddHours(-2),
        TimezoneOffset: "-07:00",
        SportName: "running",
        ScoreState: ScoreState.Scored,
        Score: new WhoopWorkoutScore(
            Strain: 12.4m,
            AverageHeartRate: 138m,
            MaxHeartRate: 172m,
            Kilojoule: 1234.5m));
}

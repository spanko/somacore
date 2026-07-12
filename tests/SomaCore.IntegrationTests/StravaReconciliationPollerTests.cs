using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.JobRuns;
using SomaCore.Domain.StravaActivities;
using SomaCore.Domain.Users;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Strava;
using SomaCore.IngestionJobs.Jobs;
using SomaCore.IntegrationTests.Observability;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// S5 coverage: the Strava reconciliation poller fills webhook gaps via the
/// S4 ingest service, skips revoked connections, and — unlike the WHOOP
/// poller's known coverage gap — has its job_runs row asserted through a
/// real <see cref="JobDispatcher"/> dispatch.
/// </summary>
[Collection(nameof(TracingCollection))]
public class StravaReconciliationPollerTests : IAsyncLifetime
{
    private const long AthleteId = 424242;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_strava_poller")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    private SomaCoreDbContext _db = null!;
    private ExternalConnection _connection = null!;
    private IStravaApiClient _api = null!;
    private IStravaAccessTokenCache _tokens = null!;

    private static readonly IOptions<StravaOptions> TestOptions = Options.Create(new StravaOptions
    {
        Enabled = true,
        DetailFetchMinSeconds = 1200,
    });

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
            Email = "strava-poller-test@example.com",
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _connection = new ExternalConnection
        {
            UserId = user.Id,
            Source = ConnectionSource.Strava,
            Status = ConnectionStatus.Active,
            KeyVaultSecretName = $"strava-refresh-{user.Id}",
            Scopes = new[] { "activity:read_all" },
            ConnectionMetadata = JsonDocument.Parse($$"""{"strava_athlete_id":{{AthleteId}}}"""),
        };
        _db.ExternalConnections.Add(_connection);
        await _db.SaveChangesAsync();

        _api = Substitute.For<IStravaApiClient>();
        _tokens = Substitute.For<IStravaAccessTokenCache>();
        _tokens.GetAccessTokenAsync(_connection.Id, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("fake-token"));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private StravaReconciliationPoller BuildPoller()
        => new(
            _db,
            _api,
            _tokens,
            new StravaActivityIngestService(
                _db, _api, _tokens, TestOptions,
                NullLogger<StravaActivityIngestService>.Instance),
            NullLogger<StravaReconciliationPoller>.Instance);

    private static StravaActivityFetch Fetch(long id, DateTimeOffset startedAt, int elapsedSeconds = 900)
    {
        var json = $$"""
            {
                "id": {{id}},
                "athlete": {"id": {{AthleteId}}},
                "sport_type": "Run",
                "start_date": "{{startedAt:yyyy-MM-ddTHH:mm:ssZ}}",
                "elapsed_time": {{elapsedSeconds}},
                "distance": 5000.0
            }
            """;
        return new StravaActivityFetch(
            JsonSerializer.Deserialize<StravaActivityPayload>(json)!,
            JsonDocument.Parse(json));
    }

    private async Task SeedActivityAsync(long stravaActivityId, DateTimeOffset startedAt)
    {
        _db.StravaActivities.Add(new StravaActivity
        {
            UserId = _connection.UserId,
            ExternalConnectionId = _connection.Id,
            StravaActivityId = stravaActivityId,
            StravaAthleteId = AthleteId,
            ActivityType = "Run",
            StartedAt = startedAt,
            ElapsedSeconds = 1800,
            IngestedVia = "webhook",
            IngestedAt = startedAt.AddMinutes(40),
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Poller_fills_a_deliberately_missed_activity()
    {
        using var capture = TraceAssertions.Capture();

        var knownStart = DateTimeOffset.UtcNow.AddDays(-2);
        var missedStart = DateTimeOffset.UtcNow.AddDays(-1);
        await SeedActivityAsync(111, knownStart);

        // Strava's listing returns both the known and the missed activity.
        _api.ListAthleteActivitiesAsync("fake-token", Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<StravaActivityFetch>>.Success(new[]
            {
                Fetch(111, knownStart),
                Fetch(222, missedStart),
            }));
        _api.GetActivityAsync("fake-token", 222, Arg.Any<CancellationToken>())
            .Returns(Result<StravaActivityFetch?>.Success(Fetch(222, missedStart)));

        var outcome = await BuildPoller().RunAsync(CancellationToken.None);

        outcome.Success.Should().BeTrue();

        // The gap (222) was ingested; the known activity (111) was not re-fetched.
        (await _db.StravaActivities.AsNoTracking().CountAsync(a => a.StravaActivityId == 222))
            .Should().Be(1, "the poller ingests the activity the webhook path missed");
        await _api.Received(1).GetActivityAsync("fake-token", 222, Arg.Any<CancellationToken>());
        await _api.DidNotReceive().GetActivityAsync("fake-token", 111, Arg.Any<CancellationToken>());

        // The listing started from max(started_at) of what we already had.
        await _api.Received(1).ListAthleteActivitiesAsync(
            "fake-token", knownStart.ToUnixTimeSeconds(), Arg.Any<CancellationToken>());

        // Connection poll bookkeeping mirrors the WHOOP poller.
        var fresh = await _db.ExternalConnections.AsNoTracking().SingleAsync(c => c.Id == _connection.Id);
        fresh.LastPolledAt.Should().NotBeNull();
        fresh.LastPollOutcome.Should().Be(PollOutcome.Polled);

        // ADR 0011: one root per pulled activity with source=strava, trigger=poller.
        var root = TraceAssertions.AssertIngestionSpanShape(
            capture.Activities, "strava.ingestion", new[] { "strava.activity.fetch" });
        root.GetTagItem(IngestionTracing.Tags.IngestionSource).Should().Be("strava");
        root.GetTagItem(IngestionTracing.Tags.IngestionTrigger).Should().Be("poller");
        root.GetTagItem(IngestionTracing.Tags.OutcomesPrefix + "activity")
            .Should().Be(IngestionTracing.Outcomes.Inserted);
    }

    [Fact]
    public async Task Poller_skips_revoked_connections()
    {
        _connection.Status = ConnectionStatus.Revoked;
        await _db.SaveChangesAsync();

        var outcome = await BuildPoller().RunAsync(CancellationToken.None);

        outcome.Success.Should().BeTrue();
        _api.ReceivedCalls().Should().BeEmpty("a revoked connection must never be polled");
        await _tokens.DidNotReceiveWithAnyArgs().GetAccessTokenAsync(default, default);
    }

    [Fact]
    public async Task Dispatcher_writes_a_job_runs_row_for_the_strava_poller()
    {
        // The WHOOP poller has a known job_runs coverage gap (track-a
        // checklist) — this test exists so the Strava poller doesn't copy it.
        _api.ListAthleteActivitiesAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<StravaActivityFetch>>.Success(Array.Empty<StravaActivityFetch>()));

        var dispatcher = new JobDispatcher(
            new IJob[] { BuildPoller() },
            _db,
            NullLogger<JobDispatcher>.Instance);

        var exitCode = await dispatcher.DispatchAsync(
            JobName.StravaReconciliationPoller, CancellationToken.None);

        exitCode.Should().Be(0);

        var run = await _db.JobRuns.AsNoTracking()
            .SingleAsync(r => r.JobName == JobName.StravaReconciliationPoller);
        run.Success.Should().BeTrue();
        run.EndedAt.Should().NotBeNull();
        run.Summary.RootElement.GetProperty("connections").GetInt32().Should().Be(1);
    }
}

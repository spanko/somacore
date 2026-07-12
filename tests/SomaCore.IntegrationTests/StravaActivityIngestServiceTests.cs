using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.Users;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Strava;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// S4 coverage: upsert idempotency on strava_activity_id, the >20-min
/// detail-fetch policy (both branches), and detail-failure resilience —
/// all against a real Postgres with a canned <see cref="IStravaApiClient"/>.
/// </summary>
public class StravaActivityIngestServiceTests : IAsyncLifetime
{
    private const long AthleteId = 424242;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_strava_ingest")
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
            Email = "strava-ingest-test@example.com",
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

    private StravaActivityIngestService BuildService()
        => new(_db, _api, _tokens, TestOptions, NullLogger<StravaActivityIngestService>.Instance);

    private static StravaActivityFetch ActivityFetch(
        long id,
        int elapsedSeconds,
        decimal? averageHr = 150.4m,
        bool withSplitsAndLaps = true)
    {
        var splits = withSplitsAndLaps
            ? """[{"split":1,"average_speed":3.3},{"split":2,"average_speed":3.5}]"""
            : "null";
        var laps = withSplitsAndLaps
            ? """[{"lap_index":1,"average_heartrate":151.2}]"""
            : "null";
        var json = $$"""
            {
                "id": {{id}},
                "athlete": {"id": {{AthleteId}}},
                "sport_type": "Run",
                "start_date": "2026-07-10T13:00:00Z",
                "elapsed_time": {{elapsedSeconds}},
                "moving_time": {{elapsedSeconds - 60}},
                "distance": 8046.1,
                "total_elevation_gain": 120.5,
                "average_speed": 3.35,
                "max_speed": 4.2,
                "average_heartrate": {{(averageHr?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")}},
                "max_heartrate": 176.0,
                "average_cadence": 84.2,
                "kudos_count": 3,
                "calories": 640.2,
                "splits_metric": {{splits}},
                "laps": {{laps}}
            }
            """;
        var payload = JsonSerializer.Deserialize<StravaActivityPayload>(json)!;
        return new StravaActivityFetch(payload, JsonDocument.Parse(json));
    }

    private static JsonDocument ZonesDoc()
        => JsonDocument.Parse(
            """[{"type":"heartrate","distribution_buckets":[{"min":0,"max":120,"time":300},{"min":120,"max":150,"time":900}]}]""");

    [Fact]
    public async Task Ingesting_the_same_activity_twice_yields_one_row_with_updated_fields()
    {
        _api.GetActivityAsync("fake-token", 777, Arg.Any<CancellationToken>())
            .Returns(
                Result<StravaActivityFetch?>.Success(ActivityFetch(777, elapsedSeconds: 900, averageHr: 150.4m)),
                Result<StravaActivityFetch?>.Success(ActivityFetch(777, elapsedSeconds: 900, averageHr: 158.7m)));

        var service = BuildService();

        var first = await service.IngestAsync(_connection.Id, 777, "webhook", "trace-1", CancellationToken.None);
        var second = await service.IngestAsync(_connection.Id, 777, "webhook", "trace-2", CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        first.Value!.Outcome.Should().Be(IngestionTracing.Outcomes.Inserted);
        second.IsSuccess.Should().BeTrue();
        second.Value!.Outcome.Should().Be(IngestionTracing.Outcomes.Updated);

        var rows = await _db.StravaActivities.AsNoTracking()
            .Where(a => a.StravaActivityId == 777)
            .ToListAsync();
        rows.Should().HaveCount(1, "upsert keys on strava_activity_id");
        rows[0].AverageHr.Should().Be(159, "the second ingest's fields win (158.7 rounds to 159)");
        rows[0].TraceId.Should().Be("trace-2");
        rows[0].StravaAthleteId.Should().Be(AthleteId);
        rows[0].ActivityType.Should().Be("Run");
    }

    [Fact]
    public async Task Short_activity_skips_the_detail_fetch()
    {
        _api.GetActivityAsync("fake-token", 801, Arg.Any<CancellationToken>())
            .Returns(Result<StravaActivityFetch?>.Success(ActivityFetch(801, elapsedSeconds: 600)));

        var result = await BuildService().IngestAsync(
            _connection.Id, 801, "webhook", null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await _api.DidNotReceiveWithAnyArgs().GetActivityZonesAsync(default!, default, default);

        var row = await _db.StravaActivities.AsNoTracking().SingleAsync(a => a.StravaActivityId == 801);
        row.DetailFetchedAt.Should().BeNull("a 10-minute activity doesn't warrant the extra detail call");
        row.HrZones.Should().BeNull();
        row.Splits.Should().BeNull();
        row.RawSummaryPayload.Should().NotBeNull("the summary payload is always preserved");
    }

    [Fact]
    public async Task Long_activity_fetches_detail_synchronously()
    {
        _api.GetActivityAsync("fake-token", 802, Arg.Any<CancellationToken>())
            .Returns(Result<StravaActivityFetch?>.Success(ActivityFetch(802, elapsedSeconds: 2400)));
        _api.GetActivityZonesAsync("fake-token", 802, Arg.Any<CancellationToken>())
            .Returns(Result<JsonDocument?>.Success(ZonesDoc()));

        var result = await BuildService().IngestAsync(
            _connection.Id, 802, "webhook", null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await _api.Received(1).GetActivityZonesAsync("fake-token", 802, Arg.Any<CancellationToken>());

        var row = await _db.StravaActivities.AsNoTracking().SingleAsync(a => a.StravaActivityId == 802);
        row.DetailFetchedAt.Should().NotBeNull();
        row.HrZones.Should().NotBeNull();
        row.HrZones!.RootElement.GetArrayLength().Should().Be(1);
        row.Splits.Should().NotBeNull("splits ride along with the detail unit");
        row.Splits!.RootElement.GetArrayLength().Should().Be(2);
        row.Laps.Should().NotBeNull();
    }

    [Fact]
    public async Task Detail_fetch_failure_keeps_a_usable_summary_row_and_does_not_fail_the_ingest()
    {
        _api.GetActivityAsync("fake-token", 803, Arg.Any<CancellationToken>())
            .Returns(Result<StravaActivityFetch?>.Success(ActivityFetch(803, elapsedSeconds: 2400)));
        _api.GetActivityZonesAsync("fake-token", 803, Arg.Any<CancellationToken>())
            .Returns(Result<JsonDocument?>.Failure("Strava activity/803/zones returned 429."));

        var result = await BuildService().IngestAsync(
            _connection.Id, 803, "webhook", null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("a detail-fetch failure must never fail the ingest");

        var row = await _db.StravaActivities.AsNoTracking().SingleAsync(a => a.StravaActivityId == 803);
        row.DetailFetchedAt.Should().BeNull("null marks the detail unit as not-yet-done for a later retry");
        row.HrZones.Should().BeNull();
        row.ElapsedSeconds.Should().Be(2400);
        row.DistanceMeters.Should().NotBeNull("the summary fields are all present and usable");
    }

    [Fact]
    public async Task Activity_deleted_at_strava_between_event_and_fetch_is_skipped_cleanly()
    {
        _api.GetActivityAsync("fake-token", 804, Arg.Any<CancellationToken>())
            .Returns(Result<StravaActivityFetch?>.Success(null));

        var result = await BuildService().IngestAsync(
            _connection.Id, 804, "webhook", null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Outcome.Should().Be(IngestionTracing.Outcomes.SkippedNoData);
        (await _db.StravaActivities.AsNoTracking().CountAsync(a => a.StravaActivityId == 804))
            .Should().Be(0);
    }
}

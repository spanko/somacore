using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.StravaActivities;
using SomaCore.Domain.Users;
using SomaCore.Domain.WhoopWorkouts;
using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Persistence;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// S6 coverage: the merged-workout view in the agent snapshot (Strava brief
/// §1.8/§1.9). The privacy-strip test is the Section D commitment — a Strava
/// activity seeded with GPS/polyline/kudos/gear/description must contribute
/// NONE of those strings to the snapshot JSON.
/// </summary>
public class StravaSnapshotMergeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_snapshot_merge")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    private SomaCoreDbContext _db = null!;
    private User _user = null!;
    private ExternalConnection _stravaConnection = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<SomaCoreDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;
        _db = new SomaCoreDbContext(options);
        await _db.Database.MigrateAsync();

        _user = new User
        {
            EntraOid = Guid.NewGuid(),
            EntraTenantId = Guid.NewGuid(),
            Email = "snapshot-merge-test@example.com",
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _db.Users.Add(_user);
        await _db.SaveChangesAsync();

        _stravaConnection = new ExternalConnection
        {
            UserId = _user.Id,
            Source = ConnectionSource.Strava,
            Status = ConnectionStatus.Active,
            KeyVaultSecretName = $"strava-refresh-{_user.Id}",
            Scopes = new[] { "activity:read_all" },
            ConnectionMetadata = JsonDocument.Parse("""{"strava_athlete_id":424242}"""),
        };
        _db.ExternalConnections.Add(_stravaConnection);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private StravaActivity StravaRun(
        long id,
        DateTimeOffset start,
        int elapsed = 2520,
        DateTimeOffset? deletedAt = null)
        => new()
        {
            UserId = _user.Id,
            ExternalConnectionId = _stravaConnection.Id,
            StravaActivityId = id,
            StravaAthleteId = 424242,
            ActivityType = "Run",
            StartedAt = start,
            ElapsedSeconds = elapsed,
            MovingSeconds = elapsed - 120,
            DistanceMeters = 8046.1m,
            TotalElevationGainM = 120.5m,
            AverageSpeedMps = 3.35m,
            AverageHr = 158,
            MaxHr = 176,
            AverageCadence = 84.2m,
            AverageWatts = 210.5m,
            KudosCount = 7,
            HrZones = JsonDocument.Parse(
                """[{"type":"heartrate","distribution_buckets":[{"min":0,"max":120,"time":300},{"min":120,"max":150,"time":900},{"min":150,"max":170,"time":1200},{"min":170,"max":185,"time":100},{"min":185,"max":-1,"time":20}]}]"""),
            Splits = JsonDocument.Parse(
                """[{"split":1,"distance":1000.0,"moving_time":290,"average_speed":3.45},{"split":2,"distance":1000.0,"moving_time":310,"average_speed":3.22},{"split":3,"distance":1000.0,"moving_time":300,"average_speed":3.33}]"""),
            // The fields the privacy commitment strips, planted where they'd
            // be if anything serialized raw payloads into the snapshot.
            RawSummaryPayload = JsonDocument.Parse(
                """{"id":991,"start_latlng":[37.77,-122.43],"end_latlng":[37.78,-122.44],"map":{"summary_polyline":"a~l~Fjk~uOwHJy@P"},"kudos_count":7,"gear_id":"g12345","description":"Morning run from home"}"""),
            RawDetailPayload = JsonDocument.Parse(
                """[{"type":"heartrate","distribution_buckets":[]}]"""),
            DetailFetchedAt = start.AddMinutes(45),
            IngestedVia = "webhook",
            IngestedAt = start.AddMinutes(44),
            DeletedAt = deletedAt,
        };

    private WhoopWorkout WhoopRun(Guid id, DateTimeOffset start, int elapsedSeconds = 2400)
        => new()
        {
            UserId = _user.Id,
            WhoopWorkoutId = id,
            StartAt = start,
            EndAt = start.AddSeconds(elapsedSeconds),
            TimezoneOffset = "-07:00",
            SportName = "running",
            ScoreState = "SCORED",
            Strain = 12.5m,
            AverageHeartRate = 152,
            MaxHeartRate = 174,
            IngestedVia = "webhook",
            IngestedAt = start.AddHours(1),
            RawPayload = JsonDocument.Parse("""{"id":"whoop-workout"}"""),
        };

    private async Task<JsonDocument> BuildSnapshotAsync()
    {
        var snapshot = await AgentInputSnapshotBuilder.BuildAsync(
            _db, _user.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        return JsonDocument.Parse(snapshot.Json);
    }

    [Fact]
    public async Task Same_run_captured_by_whoop_and_strava_merges_into_one_workout()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        _db.StravaActivities.Add(StravaRun(991, start));
        // WHOOP capture starts 3 minutes later (user started the strap late).
        _db.WhoopWorkouts.Add(WhoopRun(Guid.NewGuid(), start.AddMinutes(3)));
        await _db.SaveChangesAsync();

        using var doc = await BuildSnapshotAsync();
        var workouts = doc.RootElement.GetProperty("workouts");

        workouts.GetArrayLength().Should().Be(1,
            "the same physical run captured by both sources must merge into ONE workout");

        var merged = workouts[0];

        // Strava wins the detail fields.
        merged.GetProperty("distance_meters").GetDecimal().Should().Be(8046.1m);
        merged.GetProperty("elevation_gain_m").GetDecimal().Should().Be(120.5m);
        merged.GetProperty("avg_cadence").GetDecimal().Should().Be(84.2m);
        merged.GetProperty("avg_hr").GetInt32().Should().Be(158, "Strava HR wins when present");
        merged.GetProperty("activity_type").GetString().Should().Be("Run", "Strava's type wins");

        // WHOOP wins strain.
        merged.GetProperty("strain").GetDecimal().Should().Be(12.5m);

        // Duration is the max across sources (2520 vs 2400).
        merged.GetProperty("elapsed_seconds").GetInt32().Should().Be(2520);

        // Provenance.
        var sources = merged.GetProperty("sources").EnumerateArray().Select(s => s.GetString()).ToList();
        sources.Should().Equal("strava", "whoop");

        // Summaries are rollups, not raw arrays.
        var zones = merged.GetProperty("hr_zones_summary");
        zones.GetArrayLength().Should().Be(5);
        zones[2].GetProperty("pct").GetDecimal().Should().Be(47.6m, "1200s of 2520s total is 47.6%");

        var splits = merged.GetProperty("splits_summary");
        splits.GetProperty("split_count").GetInt32().Should().Be(3);
        splits.GetProperty("fastest_split_pace_seconds_per_km").GetDecimal().Should().Be(290m);
        splits.GetProperty("slowest_split_pace_seconds_per_km").GetDecimal().Should().Be(310m);
    }

    [Fact]
    public async Task Snapshot_contains_no_location_or_social_fields_from_strava()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        _db.StravaActivities.Add(StravaRun(992, start));
        await _db.SaveChangesAsync();

        var snapshot = await AgentInputSnapshotBuilder.BuildAsync(
            _db, _user.Id, DateTimeOffset.UtcNow, CancellationToken.None);

        // Section D commitment (hard FAIL if any appear): no GPS, no routes,
        // no social/gear metadata. The seeded raw payloads contain ALL of
        // them, so any raw-payload leak into the snapshot trips this.
        snapshot.Json.Should().NotContain("start_latlng");
        snapshot.Json.Should().NotContain("end_latlng");
        snapshot.Json.Should().NotContain("polyline");
        snapshot.Json.Should().NotContain("\"map\"");
        snapshot.Json.Should().NotContain("kudos");
        snapshot.Json.Should().NotContain("gear_id");
        snapshot.Json.Should().NotContain("description");
        snapshot.Json.Should().NotContain("Morning run from home");
        snapshot.Json.Should().NotContain("37.77");
    }

    [Fact]
    public async Task Single_source_activities_pass_through_unmerged()
    {
        var stravaStart = DateTimeOffset.UtcNow.AddDays(-2);
        var whoopStart = DateTimeOffset.UtcNow.AddDays(-1);

        // Same family but 24h apart — must NOT merge. Plus a WHOOP-only
        // strength session in a different family.
        _db.StravaActivities.Add(StravaRun(993, stravaStart));
        _db.WhoopWorkouts.Add(WhoopRun(Guid.NewGuid(), whoopStart));
        var strength = WhoopRun(Guid.NewGuid(), whoopStart.AddHours(-5), elapsedSeconds: 1800);
        strength.SportName = "weightlifting";
        _db.WhoopWorkouts.Add(strength);
        await _db.SaveChangesAsync();

        using var doc = await BuildSnapshotAsync();
        var workouts = doc.RootElement.GetProperty("workouts");

        workouts.GetArrayLength().Should().Be(3, "none of the three are the same physical workout");

        var byType = workouts.EnumerateArray()
            .Select(w => (
                Type: w.GetProperty("activity_type").GetString(),
                Sources: w.GetProperty("sources").EnumerateArray().Select(s => s.GetString()).ToList()))
            .ToList();

        byType.Should().ContainSingle(w => w.Type == "Run" && w.Sources.SequenceEqual(new[] { "strava" }));
        byType.Should().ContainSingle(w => w.Type == "running" && w.Sources.SequenceEqual(new[] { "whoop" }));
        byType.Should().ContainSingle(w => w.Type == "weightlifting" && w.Sources.SequenceEqual(new[] { "whoop" }));
    }

    [Fact]
    public async Task Soft_deleted_strava_activities_stay_out_of_the_snapshot()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        _db.StravaActivities.Add(StravaRun(994, start, deletedAt: DateTimeOffset.UtcNow));
        await _db.SaveChangesAsync();

        using var doc = await BuildSnapshotAsync();
        doc.RootElement.GetProperty("workouts").GetArrayLength().Should().Be(0,
            "a deleted-at-Strava activity must not reach the coach");
    }
}

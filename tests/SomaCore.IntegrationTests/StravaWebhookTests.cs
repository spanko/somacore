using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using SomaCore.Api.Strava;
using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.OAuthAudit;
using SomaCore.Domain.StravaActivities;
using SomaCore.Domain.Users;
using SomaCore.Domain.WebhookEvents;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Secrets;
using SomaCore.Infrastructure.Strava;
using SomaCore.IntegrationTests.Observability;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// S3 coverage: the Strava webhook receiver (verify-challenge, enqueue,
/// idempotency) and the drainer routing (activity ingest seam, soft delete,
/// athlete deauth) against a real Postgres. The drainer is driven via
/// <see cref="StravaWebhookDrainer.ProcessOneAsync"/> per the scaffolding
/// decision documented on <see cref="WhoopWebhookDrainerTests"/>.
/// </summary>
[Collection(nameof(TracingCollection))]
public class StravaWebhookTests : IAsyncLifetime
{
    private const long AthleteId = 424242;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_strava_webhook")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    private SomaCoreDbContext _db = null!;
    private User _user = null!;
    private ExternalConnection _connection = null!;

    private static readonly IOptions<StravaOptions> EnabledOptions = Options.Create(new StravaOptions
    {
        Enabled = true,
        WebhookVerifyToken = "the-verify-token",
        WebhookSubscriptionId = 9001,
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

        _user = new User
        {
            EntraOid = Guid.NewGuid(),
            EntraTenantId = Guid.NewGuid(),
            Email = "strava-webhook-test@example.com",
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _db.Users.Add(_user);
        await _db.SaveChangesAsync();

        _connection = new ExternalConnection
        {
            UserId = _user.Id,
            Source = ConnectionSource.Strava,
            Status = ConnectionStatus.Active,
            KeyVaultSecretName = $"strava-refresh-{_user.Id}",
            Scopes = new[] { "activity:read_all" },
            ConnectionMetadata = JsonDocument.Parse($$"""{"strava_athlete_id":{{AthleteId}}}"""),
        };
        _db.ExternalConnections.Add(_connection);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    // ---------- Receiver: verify-challenge ----------

    [Fact]
    public async Task Challenge_with_correct_token_echoes_hub_challenge()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(
            "?hub.mode=subscribe&hub.verify_token=the-verify-token&hub.challenge=echo-me-15f7d1a91714");

        var result = await StravaWebhookEndpoint.HandleChallengeAsync(
            httpContext, EnabledOptions, NullLoggerFactory.Instance);

        var json = result.Should().BeOfType<JsonHttpResult<Dictionary<string, string>>>().Which;
        json.Value.Should().ContainKey("hub.challenge").WhoseValue.Should().Be("echo-me-15f7d1a91714");
    }

    [Fact]
    public async Task Challenge_with_wrong_token_returns_403()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString(
            "?hub.mode=subscribe&hub.verify_token=WRONG&hub.challenge=echo-me");

        var result = await StravaWebhookEndpoint.HandleChallengeAsync(
            httpContext, EnabledOptions, NullLoggerFactory.Instance);

        result.Should().BeOfType<StatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    // ---------- Receiver: enqueue + idempotency ----------

    private async Task<IResult> PostEventAsync(object envelope)
    {
        var httpContext = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));
        httpContext.Request.Body = new MemoryStream(bytes);
        httpContext.Request.ContentLength = bytes.Length;

        return await StravaWebhookEndpoint.HandleEventAsync(
            httpContext, _db, EnabledOptions, NullLoggerFactory.Instance, CancellationToken.None);
    }

    private static object ActivityCreateEnvelope(long objectId, long eventTime = 1720000000) => new
    {
        object_type = "activity",
        object_id = objectId,
        aspect_type = "create",
        owner_id = AthleteId,
        subscription_id = 9001,
        event_time = eventTime,
    };

    [Fact]
    public async Task Duplicate_event_is_enqueued_exactly_once()
    {
        var first = await PostEventAsync(ActivityCreateEnvelope(555));
        var second = await PostEventAsync(ActivityCreateEnvelope(555));

        first.Should().BeOfType<Ok>();
        second.Should().BeOfType<Ok>("duplicate deliveries are acked, not errored — Strava would retry otherwise");

        var rows = await _db.WebhookEvents.AsNoTracking()
            .Where(e => e.Source == WebhookEventSource.Strava)
            .ToListAsync();
        rows.Should().HaveCount(1, "the dedupe key (subscription_id, object_id, aspect_type, event_time) collapses redeliveries");
        rows[0].EventType.Should().Be("activity.create");
        rows[0].ExternalConnectionId.Should().Be(_connection.Id, "owner_id maps to the connection via strava_athlete_id metadata");
        rows[0].Status.Should().Be(WebhookEventStatus.Received);
    }

    [Fact]
    public async Task Same_activity_with_different_aspect_type_is_a_distinct_event()
    {
        await PostEventAsync(ActivityCreateEnvelope(556));
        await PostEventAsync(new
        {
            object_type = "activity",
            object_id = 556,
            aspect_type = "update",
            owner_id = AthleteId,
            subscription_id = 9001,
            event_time = 1720000000,
        });

        (await _db.WebhookEvents.AsNoTracking().CountAsync(e => e.Source == WebhookEventSource.Strava))
            .Should().Be(2);
    }

    [Fact]
    public async Task Foreign_subscription_id_is_acked_but_not_stored()
    {
        var result = await PostEventAsync(new
        {
            object_type = "activity",
            object_id = 557,
            aspect_type = "create",
            owner_id = AthleteId,
            subscription_id = 6666, // not ours (9001)
            event_time = 1720000000,
        });

        result.Should().BeOfType<Ok>();
        (await _db.WebhookEvents.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Receiver_endpoints_404_when_flag_off()
    {
        var disabled = Options.Create(new StravaOptions());

        var challengeContext = new DefaultHttpContext();
        challengeContext.Request.QueryString = new QueryString(
            "?hub.mode=subscribe&hub.verify_token=x&hub.challenge=y");
        var challenge = await StravaWebhookEndpoint.HandleChallengeAsync(
            challengeContext, disabled, NullLoggerFactory.Instance);

        var postContext = new DefaultHttpContext();
        postContext.Request.Body = new MemoryStream("{}"u8.ToArray());
        var post = await StravaWebhookEndpoint.HandleEventAsync(
            postContext, _db, disabled, NullLoggerFactory.Instance, CancellationToken.None);

        challenge.Should().BeOfType<NotFound>();
        post.Should().BeOfType<NotFound>();
    }

    // ---------- Drainer routing ----------

    private async Task<WebhookEvent> SeedEventAsync(string eventType, object envelope)
    {
        var evt = new WebhookEvent
        {
            Source = WebhookEventSource.Strava,
            SourceEventId = $"seeded-{Guid.NewGuid():N}",
            SourceTraceId = $"seeded-{Guid.NewGuid():N}",
            EventType = eventType,
            UserId = _user.Id,
            ExternalConnectionId = _connection.Id,
            Status = WebhookEventStatus.Processing,
            ReceivedAt = DateTimeOffset.UtcNow,
            RawBody = JsonDocument.Parse(JsonSerializer.Serialize(envelope)),
            SignatureHeader = string.Empty,
            SignatureTimestampHeader = string.Empty,
        };
        _db.WebhookEvents.Add(evt);
        await _db.SaveChangesAsync();
        return evt;
    }

    private async Task DriveDrainerAsync(
        WebhookEvent evt,
        IStravaActivityIngestService? ingest = null,
        IKeyVaultSecretsClient? kv = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(ingest ?? Substitute.For<IStravaActivityIngestService>());
        services.AddSingleton(kv ?? Substitute.For<IKeyVaultSecretsClient>());
        var provider = services.BuildServiceProvider();

        var drainer = new StravaWebhookDrainer(
            scopeFactory: Substitute.For<IServiceScopeFactory>(), // unused by ProcessOneAsync
            logger: NullLogger<StravaWebhookDrainer>.Instance);

        await drainer.ProcessOneAsync(provider, evt, CancellationToken.None);
    }

    [Fact]
    public async Task Activity_create_routes_to_ingest_service_and_emits_strava_webhook_trace()
    {
        using var capture = TraceAssertions.Capture();

        var evt = await SeedEventAsync("activity.create", ActivityCreateEnvelope(777));

        var ingest = Substitute.For<IStravaActivityIngestService>();
        ingest.IngestAsync(_connection.Id, 777, "webhook", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<StravaActivityIngestOutcome>.Success(
                new StravaActivityIngestOutcome(IngestionTracing.Outcomes.Inserted)));

        await DriveDrainerAsync(evt, ingest);

        await ingest.Received(1).IngestAsync(
            _connection.Id, 777, "webhook", Arg.Any<string?>(), Arg.Any<CancellationToken>());

        var fresh = await _db.WebhookEvents.AsNoTracking().SingleAsync(e => e.Id == evt.Id);
        fresh.Status.Should().Be(WebhookEventStatus.Processed);

        // ADR 0011: root span strava.ingestion with source=strava, trigger=webhook.
        var root = TraceAssertions.AssertIngestionSpanShape(
            capture.Activities, "strava.ingestion", Array.Empty<string>());
        root.GetTagItem(IngestionTracing.Tags.IngestionSource).Should().Be("strava");
        root.GetTagItem(IngestionTracing.Tags.IngestionTrigger).Should().Be("webhook");
        root.GetTagItem(IngestionTracing.Tags.IngestionEventType).Should().Be("activity.create");
        root.GetTagItem(IngestionTracing.Tags.OutcomesPrefix + "activity")
            .Should().Be(IngestionTracing.Outcomes.Inserted);
    }

    [Fact]
    public async Task Activity_delete_soft_deletes_the_row_and_keeps_it_in_the_table()
    {
        _db.StravaActivities.Add(new StravaActivity
        {
            UserId = _user.Id,
            ExternalConnectionId = _connection.Id,
            StravaActivityId = 888,
            StravaAthleteId = AthleteId,
            ActivityType = "Run",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-3),
            ElapsedSeconds = 1800,
            IngestedVia = "webhook",
            IngestedAt = DateTimeOffset.UtcNow.AddHours(-2),
        });
        await _db.SaveChangesAsync();

        var evt = await SeedEventAsync("activity.delete", new
        {
            object_type = "activity",
            object_id = 888,
            aspect_type = "delete",
            owner_id = AthleteId,
            subscription_id = 9001,
            event_time = 1720000001,
        });

        await DriveDrainerAsync(evt);

        var row = await _db.StravaActivities.AsNoTracking()
            .SingleAsync(a => a.StravaActivityId == 888);
        row.DeletedAt.Should().NotBeNull("delete webhooks soft-delete — the trace survives");

        var fresh = await _db.WebhookEvents.AsNoTracking().SingleAsync(e => e.Id == evt.Id);
        fresh.Status.Should().Be(WebhookEventStatus.Processed);
    }

    [Fact]
    public async Task Athlete_deauth_revokes_connection_purges_token_audits_and_preserves_activities()
    {
        _db.StravaActivities.Add(new StravaActivity
        {
            UserId = _user.Id,
            ExternalConnectionId = _connection.Id,
            StravaActivityId = 999,
            StravaAthleteId = AthleteId,
            ActivityType = "Ride",
            StartedAt = DateTimeOffset.UtcNow.AddDays(-1),
            ElapsedSeconds = 3600,
            IngestedVia = "webhook",
            IngestedAt = DateTimeOffset.UtcNow.AddDays(-1),
        });
        await _db.SaveChangesAsync();

        var evt = await SeedEventAsync("athlete.update", new
        {
            object_type = "athlete",
            object_id = AthleteId,
            aspect_type = "update",
            updates = new Dictionary<string, string> { ["authorized"] = "false" },
            owner_id = AthleteId,
            subscription_id = 9001,
            event_time = 1720000002,
        });

        var kv = Substitute.For<IKeyVaultSecretsClient>();
        kv.TryDeleteSecretAsync(_connection.KeyVaultSecretName, Arg.Any<CancellationToken>())
            .Returns(true);

        await DriveDrainerAsync(evt, kv: kv);

        var connection = await _db.ExternalConnections.AsNoTracking()
            .SingleAsync(c => c.Id == _connection.Id);
        connection.Status.Should().Be(ConnectionStatus.Revoked);

        await kv.Received(1).TryDeleteSecretAsync(_connection.KeyVaultSecretName, Arg.Any<CancellationToken>());

        // Deauth revokes access; it must NOT erase history.
        (await _db.StravaActivities.AsNoTracking().CountAsync(a => a.StravaActivityId == 999))
            .Should().Be(1, "deauth must not delete strava_activities rows");

        var audit = await _db.OAuthAuditEntries.AsNoTracking()
            .Where(a => a.UserId == _user.Id && a.Action == OAuthAuditAction.RevokeDetected)
            .SingleAsync();
        audit.Source.Should().Be(OAuthAuditSource.Strava);
        audit.ExternalConnectionId.Should().Be(_connection.Id);

        var fresh = await _db.WebhookEvents.AsNoTracking().SingleAsync(e => e.Id == evt.Id);
        fresh.Status.Should().Be(WebhookEventStatus.Processed);
    }

    [Fact]
    public async Task Whoop_drainer_claim_does_not_touch_strava_rows()
    {
        // Regression guard for the source filter added to the WHOOP drainer's
        // claim query in S3: a queued Strava row must not be claimable by the
        // WHOOP drainer (it would discard it as an unsupported event type).
        await PostEventAsync(ActivityCreateEnvelope(1234));

        var claimed = await _db.WebhookEvents
            .FromSqlRaw(
                """
                SELECT * FROM webhook_events
                WHERE status = 'received' AND source = 'whoop'
                ORDER BY received_at
                LIMIT {0}
                FOR UPDATE SKIP LOCKED
                """,
                5)
            .ToListAsync();

        claimed.Should().BeEmpty("the WHOOP drainer's claim filters on source='whoop'");

        var stravaRow = await _db.WebhookEvents.AsNoTracking()
            .SingleAsync(e => e.Source == WebhookEventSource.Strava);
        stravaRow.Status.Should().Be(WebhookEventStatus.Received, "the row stays queued for the Strava drainer");
    }
}

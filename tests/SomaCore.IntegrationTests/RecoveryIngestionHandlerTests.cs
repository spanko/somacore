using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.Users;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Recovery;
using SomaCore.Infrastructure.Whoop;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

public class RecoveryIngestionHandlerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_handler")
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

        // Seed a user + active WHOOP connection.
        var user = new User
        {
            EntraOid = Guid.NewGuid(),
            EntraTenantId = Guid.NewGuid(),
            Email = "test@example.com",
            DisplayName = "Test",
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
            Scopes = new[] { "read:recovery", "offline" },
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
    public async Task Should_insert_then_no_op_on_duplicate_ingest()
    {
        var apiClient = Substitute.For<IWhoopApiClient>();
        var tokenCache = Substitute.For<IWhoopAccessTokenCache>();

        tokenCache.GetAccessTokenAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("fake-access-token"));

        var payload = new WhoopRecoveryPayload(
            CycleId: 999,
            SleepId: Guid.NewGuid(),
            UserId: 12345,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ScoreState: "SCORED",
            Score: new WhoopRecoveryScore(67, 42.1234m, 55, null, null, false));

        apiClient.ListRecentRecoveriesAsync("fake-access-token", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<WhoopRecoveryListResponse>.Success(
                new WhoopRecoveryListResponse(new[] { payload }, NextToken: null)));

        var cycle = new WhoopCyclePayload(
            Id: 999,
            UserId: 12345,
            Start: DateTimeOffset.UtcNow.AddHours(-9),
            End: DateTimeOffset.UtcNow.AddHours(-1),
            ScoreState: "SCORED");
        apiClient.GetCycleAsync("fake-access-token", 999, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopCyclePayload>.Success(cycle));

        var handler = new RecoveryIngestionHandler(
            _db, apiClient, tokenCache, NullLogger<RecoveryIngestionHandler>.Instance);

        var first = await handler.IngestAsync(
            new RecoveryIngestionRequest(_connectionId, "webhook", null, payload.SleepId, "trace-1"),
            CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        first.Value!.Status.Should().Be(RecoveryIngestionStatus.Inserted);

        // Same payload again -> NoOp; the unique index would reject a duplicate insert anyway.
        var second = await handler.IngestAsync(
            new RecoveryIngestionRequest(_connectionId, "webhook", null, payload.SleepId, "trace-2"),
            CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        second.Value!.Status.Should().Be(RecoveryIngestionStatus.NoOp);

        var rowCount = await _db.WhoopRecoveries
            .Where(r => r.ExternalConnectionId == _connectionId && r.WhoopCycleId == 999)
            .CountAsync();
        rowCount.Should().Be(1);
    }

    [Fact]
    public async Task Should_update_an_existing_row_when_score_state_progresses()
    {
        var apiClient = Substitute.For<IWhoopApiClient>();
        var tokenCache = Substitute.For<IWhoopAccessTokenCache>();
        tokenCache.GetAccessTokenAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("tok"));

        var sleep = Guid.NewGuid();
        var pending = new WhoopRecoveryPayload(
            CycleId: 1001,
            SleepId: sleep,
            UserId: 12345,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            ScoreState: "PENDING_SCORE",
            Score: null);
        var scored = pending with
        {
            ScoreState = "SCORED",
            Score = new WhoopRecoveryScore(72, 38.5m, 58, null, null, false),
        };
        var cycle = new WhoopCyclePayload(1001, 12345,
            DateTimeOffset.UtcNow.AddHours(-9),
            DateTimeOffset.UtcNow.AddHours(-1),
            "SCORED");

        apiClient.ListRecentRecoveriesAsync("tok", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<WhoopRecoveryListResponse>.Success(
                new WhoopRecoveryListResponse(new[] { pending }, null)));
        apiClient.GetRecoveryByCycleAsync("tok", 1001, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopRecoveryPayload?>.Success(scored));
        apiClient.GetCycleAsync("tok", 1001, Arg.Any<CancellationToken>())
            .Returns(Result<WhoopCyclePayload>.Success(cycle));

        var handler = new RecoveryIngestionHandler(
            _db, apiClient, tokenCache, NullLogger<RecoveryIngestionHandler>.Instance);

        var first = await handler.IngestAsync(
            new RecoveryIngestionRequest(_connectionId, "webhook", null, sleep, "trace-a"),
            CancellationToken.None);
        first.Value!.Status.Should().Be(RecoveryIngestionStatus.Inserted);
        first.Value.ScoreState.Should().Be("PENDING_SCORE");

        var second = await handler.IngestAsync(
            new RecoveryIngestionRequest(_connectionId, "poller", 1001, sleep, "trace-b"),
            CancellationToken.None);
        second.Value!.Status.Should().Be(RecoveryIngestionStatus.Updated);
        second.Value.ScoreState.Should().Be("SCORED");

        var stored = await _db.WhoopRecoveries
            .Where(r => r.ExternalConnectionId == _connectionId && r.WhoopCycleId == 1001)
            .ToListAsync();
        stored.Should().HaveCount(1);
        stored[0].ScoreState.Should().Be("SCORED");
        stored[0].RecoveryScore.Should().Be(72);
        stored[0].IngestedVia.Should().Be("poller");
    }
}

using System.Net.Http;
using System.Text.Json;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.Users;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Secrets;
using SomaCore.Infrastructure.Whoop;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// Verifies the WhoopAccessTokenCache's permanent-vs-transient policy:
/// a 4xx from WHOOP (Result.Failure) flips the connection to refresh_failed,
/// but a 5xx or transport exception is treated as transient — the row stays
/// active so the next sweep retries.
/// </summary>
public class WhoopAccessTokenCacheTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_cache")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    private ServiceProvider _services = null!;
    private Guid _connectionId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var services = new ServiceCollection();
        services.AddDbContext<SomaCoreDbContext>(opts => opts
            .UseNpgsql(_postgres.GetConnectionString())
            .UseSnakeCaseNamingConvention());

        _services = services.BuildServiceProvider();

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();
            await db.Database.MigrateAsync();

            var user = new User
            {
                EntraOid = Guid.NewGuid(),
                EntraTenantId = Guid.NewGuid(),
                Email = "cache-test@example.com",
                LastSeenAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var connection = new ExternalConnection
            {
                UserId = user.Id,
                Source = ConnectionSource.Whoop,
                Status = ConnectionStatus.Active,
                KeyVaultSecretName = $"whoop-refresh-{user.Id}",
                Scopes = new[] { "read:recovery", "offline" },
                ConnectionMetadata = JsonDocument.Parse("""{"whoop_user_id":999}"""),
            };
            db.ExternalConnections.Add(connection);
            await db.SaveChangesAsync();

            _connectionId = connection.Id;
        }
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Should_flip_status_to_refresh_failed_when_whoop_returns_4xx_invalid_grant()
    {
        var kv = Substitute.For<IKeyVaultSecretsClient>();
        kv.TryGetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("the-old-refresh-token");

        var whoop = Substitute.For<IWhoopOAuthClient>();
        // Real client returns Result.Failure for any 4xx. Simulate that here.
        whoop.RefreshTokenAsync("the-old-refresh-token", Arg.Any<CancellationToken>())
            .Returns(Result<WhoopTokenResponse>.Failure("WHOOP refresh returned 401."));

        var cache = new WhoopAccessTokenCache(
            kv,
            whoop,
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WhoopAccessTokenCache>.Instance);

        var result = await cache.GetAccessTokenAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();
        var fresh = await db.ExternalConnections.AsNoTracking()
            .SingleAsync(c => c.Id == _connectionId);
        fresh.Status.Should().Be(ConnectionStatus.RefreshFailed,
            "a 4xx from WHOOP means the refresh token is dead — flip status so /me renders the Reconnect banner");
        fresh.RefreshFailureCount.Should().Be(1);
        fresh.LastRefreshError.Should().Contain("401");
    }

    [Fact]
    public async Task Should_leave_status_active_when_whoop_throws_transport_exception_5xx()
    {
        var kv = Substitute.For<IKeyVaultSecretsClient>();
        kv.TryGetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("the-old-refresh-token");

        var whoop = Substitute.For<IWhoopOAuthClient>();
        // Real client throws HttpRequestException on 5xx (via EnsureSuccessStatusCode).
        whoop.RefreshTokenAsync("the-old-refresh-token", Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("WHOOP unreachable: 503"));

        var cache = new WhoopAccessTokenCache(
            kv,
            whoop,
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WhoopAccessTokenCache>.Instance);

        var result = await cache.GetAccessTokenAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeFalse("the access token request failed");

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();
        var fresh = await db.ExternalConnections.AsNoTracking()
            .SingleAsync(c => c.Id == _connectionId);
        fresh.Status.Should().Be(ConnectionStatus.Active,
            "a 5xx/transport error is transient — never flip status, the next sweep retries");
        fresh.RefreshFailureCount.Should().Be(0);
        fresh.LastRefreshError.Should().BeNull();
    }

    [Fact]
    public async Task Should_recover_when_kv_refresh_token_was_rotated_by_concurrent_caller()
    {
        // Race-rescue scenario: the API + IngestionJobs processes each kept
        // their own in-memory cache, both saw an expired token, both POSTed
        // to /oauth/oauth2/token with the same RT. The other process won,
        // wrote the new RT to KV, and is using its new access token. Our
        // process's call returned 4xx because the RT we sent is now dead.
        // The patch: re-read KV, see a different RT, retry once, succeed,
        // do NOT flip status to refresh_failed.
        var kv = Substitute.For<IKeyVaultSecretsClient>();
        kv.TryGetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            // 1st read (initial fetch): the RT this process held.
            // 2nd read (race-rescue): the fresh RT the other process wrote.
            .Returns("rt-old-that-other-process-just-used", "rt-fresh-from-other-process");

        var whoop = Substitute.For<IWhoopOAuthClient>();
        whoop.RefreshTokenAsync("rt-old-that-other-process-just-used", Arg.Any<CancellationToken>())
            .Returns(Result<WhoopTokenResponse>.Failure("WHOOP refresh returned 400."));
        whoop.RefreshTokenAsync("rt-fresh-from-other-process", Arg.Any<CancellationToken>())
            .Returns(Result<WhoopTokenResponse>.Success(new WhoopTokenResponse(
                AccessToken: "fresh-access-token",
                RefreshToken: "rt-after-our-rescue-rotation",
                ExpiresInSeconds: 3600,
                Scope: "read:recovery offline",
                TokenType: "Bearer")));

        var cache = new WhoopAccessTokenCache(
            kv,
            whoop,
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WhoopAccessTokenCache>.Instance);

        var result = await cache.GetAccessTokenAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the race-rescue retry succeeded");
        result.Value.Should().Be("fresh-access-token");

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();
        var fresh = await db.ExternalConnections.AsNoTracking()
            .SingleAsync(c => c.Id == _connectionId);
        fresh.Status.Should().Be(ConnectionStatus.Active,
            "the 4xx was a race, not a permanent invalidation — status must stay active");
        fresh.RefreshFailureCount.Should().Be(0,
            "rescue success resets the failure counter on the success path");
        fresh.LastRefreshError.Should().BeNull();

        // The rescue rotation should have written the new RT back to KV.
        await kv.Received().SetSecretAsync(
            Arg.Any<string>(), "rt-after-our-rescue-rotation", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_flip_to_refresh_failed_when_both_initial_and_rescue_return_4xx()
    {
        // Confirms the rescue doesn't mask a TRUE permanent failure: when the
        // KV secret actually has changed but WHOOP still rejects both the old
        // and the new RT, that's an honest invalidation. Flip status.
        var kv = Substitute.For<IKeyVaultSecretsClient>();
        kv.TryGetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("rt-old", "rt-newer-but-also-dead");

        var whoop = Substitute.For<IWhoopOAuthClient>();
        whoop.RefreshTokenAsync("rt-old", Arg.Any<CancellationToken>())
            .Returns(Result<WhoopTokenResponse>.Failure("WHOOP refresh returned 400."));
        whoop.RefreshTokenAsync("rt-newer-but-also-dead", Arg.Any<CancellationToken>())
            .Returns(Result<WhoopTokenResponse>.Failure("WHOOP refresh returned 400."));

        var cache = new WhoopAccessTokenCache(
            kv,
            whoop,
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WhoopAccessTokenCache>.Instance);

        var result = await cache.GetAccessTokenAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();
        var fresh = await db.ExternalConnections.AsNoTracking()
            .SingleAsync(c => c.Id == _connectionId);
        fresh.Status.Should().Be(ConnectionStatus.RefreshFailed,
            "both attempts permanently failed — this is a real invalidation");
        fresh.RefreshFailureCount.Should().Be(1);
    }

    [Fact]
    public async Task Should_leave_status_active_when_whoop_times_out()
    {
        var kv = Substitute.For<IKeyVaultSecretsClient>();
        kv.TryGetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("the-old-refresh-token");

        var whoop = Substitute.For<IWhoopOAuthClient>();
        // HttpClient surfaces a request timeout as TaskCanceledException with a
        // TimeoutException inner (when the user's CT is not cancelled).
        var inner = new TimeoutException("HttpClient timeout");
        whoop.RefreshTokenAsync("the-old-refresh-token", Arg.Any<CancellationToken>())
            .Throws(new TaskCanceledException("timeout", inner));

        var cache = new WhoopAccessTokenCache(
            kv,
            whoop,
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WhoopAccessTokenCache>.Instance);

        var result = await cache.GetAccessTokenAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();
        var fresh = await db.ExternalConnections.AsNoTracking()
            .SingleAsync(c => c.Id == _connectionId);
        fresh.Status.Should().Be(ConnectionStatus.Active,
            "a request timeout is transient — leave status as-is");
    }
}

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
using SomaCore.Domain.OAuthAudit;
using SomaCore.Domain.Users;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Secrets;
using SomaCore.Infrastructure.Strava;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// Mirrors <see cref="WhoopAccessTokenCacheTests"/> for the Strava cache:
/// permanent-vs-transient refresh policy, the refresh-rotation race-rescue,
/// and — beyond the WHOOP cache — the oauth_audit rows the S2 DoD requires
/// on refresh outcomes.
/// </summary>
public class StravaAccessTokenCacheTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_strava_cache")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    private ServiceProvider _services = null!;
    private Guid _connectionId;
    private Guid _userId;

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
                Email = "strava-cache-test@example.com",
                LastSeenAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            _userId = user.Id;

            var connection = new ExternalConnection
            {
                UserId = user.Id,
                Source = ConnectionSource.Strava,
                Status = ConnectionStatus.Active,
                KeyVaultSecretName = $"strava-refresh-{user.Id}",
                Scopes = new[] { "activity:read_all" },
                ConnectionMetadata = JsonDocument.Parse("""{"strava_athlete_id":424242}"""),
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

    private StravaAccessTokenCache BuildCache(IKeyVaultSecretsClient kv, IStravaOAuthClient strava)
        => new(
            kv,
            strava,
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StravaAccessTokenCache>.Instance);

    private static StravaTokenResponse Token(string accessToken, string refreshToken)
        => new(
            TokenType: "Bearer",
            AccessToken: accessToken,
            RefreshToken: refreshToken,
            ExpiresInSeconds: 21600,
            ExpiresAtEpoch: DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds(),
            Athlete: null);

    [Fact]
    public async Task Should_recover_when_kv_refresh_token_was_rotated_by_concurrent_caller()
    {
        // Race-rescue: the API + IngestionJobs processes both refreshed the
        // same connection at once; the other process won the rotation and
        // wrote the new RT to KV. Our 4xx is a race, not a revocation —
        // re-read KV, see a different RT, retry once, succeed.
        var kv = Substitute.For<IKeyVaultSecretsClient>();
        kv.TryGetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("rt-old-that-other-process-just-used", "rt-fresh-from-other-process");

        var strava = Substitute.For<IStravaOAuthClient>();
        strava.RefreshTokenAsync("rt-old-that-other-process-just-used", Arg.Any<CancellationToken>())
            .Returns(Result<StravaTokenResponse>.Failure("Strava refresh returned 400."));
        strava.RefreshTokenAsync("rt-fresh-from-other-process", Arg.Any<CancellationToken>())
            .Returns(Result<StravaTokenResponse>.Success(
                Token("fresh-access-token", "rt-after-our-rescue-rotation")));

        var cache = BuildCache(kv, strava);

        var result = await cache.GetAccessTokenAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the race-rescue retry succeeded");
        result.Value.Should().Be("fresh-access-token");

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();
        var fresh = await db.ExternalConnections.AsNoTracking()
            .SingleAsync(c => c.Id == _connectionId);
        fresh.Status.Should().Be(ConnectionStatus.Active,
            "the 4xx was a race, not a permanent invalidation — status must stay active");
        fresh.RefreshFailureCount.Should().Be(0);
        fresh.LastRefreshError.Should().BeNull();

        // The rescue rotation should have written the new RT back to KV.
        await kv.Received().SetSecretAsync(
            Arg.Any<string>(), "rt-after-our-rescue-rotation", Arg.Any<CancellationToken>());

        // Refresh success is audited (S2 DoD — the WHOOP cache lacks this).
        var audit = await db.OAuthAuditEntries.AsNoTracking()
            .Where(a => a.UserId == _userId && a.Action == OAuthAuditAction.TokenRefreshSuccess)
            .SingleAsync();
        audit.Source.Should().Be(OAuthAuditSource.Strava);
        audit.Success.Should().BeTrue();
        audit.ExternalConnectionId.Should().Be(_connectionId);
    }

    [Fact]
    public async Task Should_flip_status_and_audit_failure_when_strava_returns_4xx_invalid_grant()
    {
        var kv = Substitute.For<IKeyVaultSecretsClient>();
        kv.TryGetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("the-old-refresh-token");

        var strava = Substitute.For<IStravaOAuthClient>();
        // Real client returns Result.Failure for any 4xx. KV returns the SAME
        // RT on the rescue re-read, so the rescue correctly does not fire.
        strava.RefreshTokenAsync("the-old-refresh-token", Arg.Any<CancellationToken>())
            .Returns(Result<StravaTokenResponse>.Failure("Strava refresh returned 401."));

        var cache = BuildCache(kv, strava);

        var result = await cache.GetAccessTokenAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();
        var fresh = await db.ExternalConnections.AsNoTracking()
            .SingleAsync(c => c.Id == _connectionId);
        fresh.Status.Should().Be(ConnectionStatus.RefreshFailed,
            "a 4xx from Strava means the refresh token is dead — flip status so /me renders the Reconnect banner");
        fresh.RefreshFailureCount.Should().Be(1);
        fresh.LastRefreshError.Should().Contain("401");

        // Permanent failure is audited (S2 DoD).
        var audit = await db.OAuthAuditEntries.AsNoTracking()
            .Where(a => a.UserId == _userId && a.Action == OAuthAuditAction.TokenRefreshFailed)
            .SingleAsync();
        audit.Source.Should().Be(OAuthAuditSource.Strava);
        audit.Success.Should().BeFalse();
        audit.ErrorMessage.Should().Contain("401");
    }

    [Fact]
    public async Task Should_leave_status_active_and_skip_audit_when_strava_throws_transport_exception_5xx()
    {
        var kv = Substitute.For<IKeyVaultSecretsClient>();
        kv.TryGetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("the-old-refresh-token");

        var strava = Substitute.For<IStravaOAuthClient>();
        // Real client throws HttpRequestException on 5xx (via EnsureSuccessStatusCode).
        strava.RefreshTokenAsync("the-old-refresh-token", Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Strava unreachable: 503"));

        var cache = BuildCache(kv, strava);

        var result = await cache.GetAccessTokenAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeFalse("the access token request failed");

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SomaCoreDbContext>();
        var fresh = await db.ExternalConnections.AsNoTracking()
            .SingleAsync(c => c.Id == _connectionId);
        fresh.Status.Should().Be(ConnectionStatus.Active,
            "a 5xx/transport error is transient — never flip status, the next pass retries");
        fresh.RefreshFailureCount.Should().Be(0);

        // Transient failures don't audit — no state changed.
        var auditCount = await db.OAuthAuditEntries.AsNoTracking()
            .CountAsync(a => a.UserId == _userId);
        auditCount.Should().Be(0);
    }

    [Fact]
    public async Task Should_flip_to_refresh_failed_when_both_initial_and_rescue_return_4xx()
    {
        // The rescue must not mask a TRUE permanent failure: KV moved but
        // Strava rejects both the old and the new RT.
        var kv = Substitute.For<IKeyVaultSecretsClient>();
        kv.TryGetSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("rt-old", "rt-newer-but-also-dead");

        var strava = Substitute.For<IStravaOAuthClient>();
        strava.RefreshTokenAsync("rt-old", Arg.Any<CancellationToken>())
            .Returns(Result<StravaTokenResponse>.Failure("Strava refresh returned 400."));
        strava.RefreshTokenAsync("rt-newer-but-also-dead", Arg.Any<CancellationToken>())
            .Returns(Result<StravaTokenResponse>.Failure("Strava refresh returned 400."));

        var cache = BuildCache(kv, strava);

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
}

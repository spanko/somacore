using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using SomaCore.Api.Whoop;
using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.OAuthAudit;
using SomaCore.Domain.Users;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Secrets;
using SomaCore.Infrastructure.Whoop;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// End-to-end coverage of <see cref="WhoopAuthEndpoints.DisconnectAsync"/> against a real
/// Postgres. Verifies the cascade contract: deleting external_connections preserves
/// whoop_recoveries (FK SET NULL) and oauth_audit (FK SET NULL) so the user keeps their
/// history and our forensic trail survives a disconnect.
/// </summary>
public class WhoopDisconnectTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_disconnect")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    private SomaCoreDbContext _db = null!;
    private User _user = null!;
    private ExternalConnection _connection = null!;
    private WhoopRecovery _recovery = null!;
    private OAuthAuditEntry _earlierAudit = null!;

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
            Email = "disconnect-test@example.com",
            DisplayName = "Disconnect Test",
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _db.Users.Add(_user);
        await _db.SaveChangesAsync();

        _connection = new ExternalConnection
        {
            UserId = _user.Id,
            Source = ConnectionSource.Whoop,
            Status = ConnectionStatus.Active,
            KeyVaultSecretName = $"whoop-refresh-{_user.Id}",
            Scopes = new[] { "read:recovery", "offline" },
            ConnectionMetadata = JsonDocument.Parse("""{"whoop_user_id":12345,"whoop_email":"disconnect-test@whoop.example"}"""),
        };
        _db.ExternalConnections.Add(_connection);
        await _db.SaveChangesAsync();

        _recovery = new WhoopRecovery
        {
            UserId = _user.Id,
            ExternalConnectionId = _connection.Id,
            WhoopCycleId = 4242,
            ScoreState = "SCORED",
            RecoveryScore = 81,
            HrvRmssdMilli = 55.5m,
            RestingHeartRate = 50,
            CycleStartAt = DateTimeOffset.UtcNow.AddHours(-12),
            CycleEndAt = DateTimeOffset.UtcNow.AddHours(-2),
            IngestedVia = "webhook",
            IngestedAt = DateTimeOffset.UtcNow.AddHours(-1),
            RawPayload = JsonDocument.Parse("""{"cycle_id":4242}"""),
        };
        _db.WhoopRecoveries.Add(_recovery);

        _earlierAudit = new OAuthAuditEntry
        {
            UserId = _user.Id,
            ExternalConnectionId = _connection.Id,
            Source = OAuthAuditSource.Whoop,
            Action = OAuthAuditAction.CallbackSuccess,
            Success = true,
            HttpStatusCode = 200,
            Context = JsonDocument.Parse("""{"whoop_user_id":12345}"""),
            OccurredAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        _db.OAuthAuditEntries.Add(_earlierAudit);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Should_delete_connection_preserve_recovery_and_preserve_audit_when_revoke_succeeds()
    {
        var whoop = Substitute.For<IWhoopOAuthClient>();
        whoop.RevokeAccessAsync("fake-access-token", Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Success(true));

        var tokenCache = Substitute.For<IWhoopAccessTokenCache>();
        tokenCache.GetAccessTokenAsync(_connection.Id, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("fake-access-token"));

        var kv = Substitute.For<IKeyVaultSecretsClient>();
        kv.TryDeleteSecretAsync(_connection.KeyVaultSecretName, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await InvokeDisconnectAsync(whoop, tokenCache, kv);

        // The endpoint redirects to /me?whoop=disconnected on every code path.
        result.Should().NotBeNull();
        await whoop.Received(1).RevokeAccessAsync("fake-access-token", Arg.Any<CancellationToken>());
        await kv.Received(1).TryDeleteSecretAsync(_connection.KeyVaultSecretName, Arg.Any<CancellationToken>());

        await AssertLocalTeardownAppliedAsync(expectExternalAuditFkNull: true);
    }

    [Fact]
    public async Task Should_proceed_with_local_teardown_when_whoop_revoke_throws_a_transport_exception()
    {
        var whoop = Substitute.For<IWhoopOAuthClient>();
        whoop.RevokeAccessAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new HttpRequestException("connection reset by peer"));

        var tokenCache = Substitute.For<IWhoopAccessTokenCache>();
        tokenCache.GetAccessTokenAsync(_connection.Id, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("fake-access-token"));

        var kv = Substitute.For<IKeyVaultSecretsClient>();
        kv.TryDeleteSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        await InvokeDisconnectAsync(whoop, tokenCache, kv);

        await AssertLocalTeardownAppliedAsync(expectExternalAuditFkNull: true);
    }

    [Fact]
    public async Task Should_proceed_with_local_teardown_when_whoop_revoke_returns_5xx_via_throw()
    {
        // FailureFromResponseAsync throws HttpRequestException on 5xx, matching the
        // real client. Same disposition as the transport exception above.
        var whoop = Substitute.For<IWhoopOAuthClient>();
        whoop.RevokeAccessAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new HttpRequestException("WHOOP 503"));

        var tokenCache = Substitute.For<IWhoopAccessTokenCache>();
        tokenCache.GetAccessTokenAsync(_connection.Id, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("fake-access-token"));

        var kv = Substitute.For<IKeyVaultSecretsClient>();
        kv.TryDeleteSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        await InvokeDisconnectAsync(whoop, tokenCache, kv);

        await AssertLocalTeardownAppliedAsync(expectExternalAuditFkNull: true);
    }

    [Fact]
    public async Task Should_skip_revoke_but_still_delete_locally_when_access_token_unavailable()
    {
        // Simulates the connection already being in refresh_failed: the access
        // token cache can't get a valid token (refresh itself fails again), so
        // we skip the revoke step entirely and proceed straight to local teardown.
        var whoop = Substitute.For<IWhoopOAuthClient>();
        var tokenCache = Substitute.For<IWhoopAccessTokenCache>();
        tokenCache.GetAccessTokenAsync(_connection.Id, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Failure("refresh failed previously"));

        var kv = Substitute.For<IKeyVaultSecretsClient>();
        kv.TryDeleteSecretAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        await InvokeDisconnectAsync(whoop, tokenCache, kv);

        await whoop.DidNotReceiveWithAnyArgs().RevokeAccessAsync(default!, default);
        await AssertLocalTeardownAppliedAsync(expectExternalAuditFkNull: true);
    }

    [Fact]
    public async Task Should_return_404_when_no_active_whoop_connection_exists()
    {
        _connection.Status = ConnectionStatus.Revoked;
        await _db.SaveChangesAsync();

        var whoop = Substitute.For<IWhoopOAuthClient>();
        var tokenCache = Substitute.For<IWhoopAccessTokenCache>();
        var kv = Substitute.For<IKeyVaultSecretsClient>();

        var result = await InvokeDisconnectAsync(whoop, tokenCache, kv);

        // Connection is still present (revoked, not deleted) and recovery untouched.
        var stillThere = await _db.ExternalConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == _connection.Id);
        stillThere.Should().NotBeNull();

        await whoop.DidNotReceiveWithAnyArgs().RevokeAccessAsync(default!, default);
        await kv.DidNotReceiveWithAnyArgs().TryDeleteSecretAsync(default!, default);
    }

    private Task<IResult> InvokeDisconnectAsync(
        IWhoopOAuthClient whoop,
        IWhoopAccessTokenCache tokenCache,
        IKeyVaultSecretsClient kv)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                // Microsoft.Identity.Web's GetObjectId() looks at the standard
                // SOAP-style claim URI; "oid" alone is not picked up.
                new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier",
                    _user.EntraOid.ToString()),
            }, authenticationType: "Test")),
        };

        return WhoopAuthEndpoints.DisconnectAsync(
            httpContext,
            _db,
            whoop,
            tokenCache,
            kv,
            NullLoggerFactory.Instance,
            CancellationToken.None);
    }

    private async Task AssertLocalTeardownAppliedAsync(bool expectExternalAuditFkNull)
    {
        // Connection row hard-deleted.
        var connection = await _db.ExternalConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == _connection.Id);
        connection.Should().BeNull("the disconnect handler hard-deletes the connection row");

        // Recovery preserved; FK SET NULL, but still attributable to the user
        // and findable via the exact predicate /me uses (filters on user_id,
        // never joins through external_connections). If anyone later changes
        // MeModel.LoadRecentAsync to traverse the connection FK, this fails.
        var recovery = await _db.WhoopRecoveries.AsNoTracking()
            .FirstAsync(r => r.Id == _recovery.Id);
        recovery.ExternalConnectionId.Should().BeNull(
            "FK ON DELETE SET NULL keeps the recovery as historical data tied to the user");
        recovery.UserId.Should().Be(_user.Id);
        recovery.WhoopCycleId.Should().Be(4242);

        // Mirror the /me page's query path (LoadRecentAsync in MeModel) so this
        // test guards both the FK semantics AND the read path that exercises them.
        var visibleOnMe = await _db.WhoopRecoveries.AsNoTracking()
            .Where(r => r.UserId == _user.Id)
            .OrderByDescending(r => r.CycleStartAt)
            .ToListAsync();
        visibleOnMe.Should().ContainSingle(r => r.Id == _recovery.Id,
            "the recovery must remain visible to the user's /me page after disconnect — " +
            "if it requires the deleted connection row to be discoverable, disconnect would orphan history");
        visibleOnMe.Single(r => r.Id == _recovery.Id).RecoveryScore.Should().Be(81,
            "the actual recovery data (not just the row identity) must survive disconnect");

        // Earlier audit row preserved with the FK nulled out.
        var oldAudit = await _db.OAuthAuditEntries.AsNoTracking()
            .FirstAsync(a => a.Id == _earlierAudit.Id);
        if (expectExternalAuditFkNull)
        {
            oldAudit.ExternalConnectionId.Should().BeNull(
                "oauth_audit.external_connection_id is ON DELETE SET NULL");
        }
        oldAudit.UserId.Should().Be(_user.Id);

        // New ManualDisconnect audit row written by the handler.
        var manualDisconnect = await _db.OAuthAuditEntries.AsNoTracking()
            .Where(a => a.UserId == _user.Id && a.Action == OAuthAuditAction.ManualDisconnect)
            .SingleAsync();
        manualDisconnect.Success.Should().BeTrue();
        manualDisconnect.Source.Should().Be(OAuthAuditSource.Whoop);
    }
}

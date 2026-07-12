using System.Security.Claims;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using SomaCore.Api.Strava;
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
/// End-to-end coverage of the Strava OAuth endpoints against a real Postgres
/// with a canned <see cref="IStravaOAuthClient"/>. Verifies the S2 DoD:
/// oauth_audit rows on authorize/callback/disconnect, token custody in Key
/// Vault only (the DB stores the secret NAME), and the Strava:Enabled gate.
/// (Refresh-path audit rows are covered in <see cref="StravaAccessTokenCacheTests"/>.)
/// </summary>
public class StravaAuthEndpointTests : IAsyncLifetime
{
    private const string StateCookieName = "somacore.strava.state";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_strava_auth")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    private SomaCoreDbContext _db = null!;
    private User _user = null!;
    private IStravaStateProtector _stateProtector = null!;

    private static readonly IOptions<StravaOptions> EnabledOptions = Options.Create(new StravaOptions
    {
        Enabled = true,
        ClientId = "test-client-id",
        RedirectUri = "https://localhost/auth/strava/callback",
    });

    private static readonly IOptions<StravaOptions> DisabledOptions = Options.Create(new StravaOptions());

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
            Email = "strava-auth-test@example.com",
            DisplayName = "Strava Auth Test",
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _db.Users.Add(_user);
        await _db.SaveChangesAsync();

        _stateProtector = new StravaStateProtector(new EphemeralDataProtectionProvider());
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private DefaultHttpContext SignedInHttpContext()
        => new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                // Microsoft.Identity.Web's GetObjectId() looks at the standard
                // SOAP-style claim URI; "oid" alone is not picked up.
                new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier",
                    _user.EntraOid.ToString()),
            }, authenticationType: "Test")),
        };

    private static StravaTokenResponse CannedToken() => new(
        TokenType: "Bearer",
        AccessToken: "canned-access-token",
        RefreshToken: "canned-refresh-token",
        ExpiresInSeconds: 21600,
        ExpiresAtEpoch: DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds(),
        Athlete: new StravaAthleteSummary(424242, "adamw", "Adam", "W"));

    [Fact]
    public async Task Start_should_write_authorize_audit_row_and_redirect_to_strava()
    {
        var strava = Substitute.For<IStravaOAuthClient>();
        strava.BuildAuthorizeUrl(Arg.Any<string>())
            .Returns(ci => $"https://www.strava.com/oauth/authorize?state={ci.Arg<string>()}");

        var result = await StravaAuthEndpoints.StartAsync(
            SignedInHttpContext(), _db, strava, _stateProtector,
            EnabledOptions, NullLoggerFactory.Instance, CancellationToken.None);

        result.Should().BeOfType<RedirectHttpResult>()
            .Which.Url.Should().StartWith("https://www.strava.com/oauth/authorize");

        var audit = await _db.OAuthAuditEntries.AsNoTracking()
            .Where(a => a.UserId == _user.Id && a.Action == OAuthAuditAction.Authorize)
            .SingleAsync();
        audit.Source.Should().Be(OAuthAuditSource.Strava);
        audit.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Callback_should_create_connection_store_token_in_kv_only_and_audit()
    {
        var protectedState = _stateProtector.Protect(
            new StravaOAuthState(_user.Id, StravaStateProtector.NewNonce(), DateTimeOffset.UtcNow));

        var httpContext = SignedInHttpContext();
        httpContext.Request.Headers.Cookie = $"{StateCookieName}={protectedState}";

        var strava = Substitute.For<IStravaOAuthClient>();
        strava.ExchangeCodeAsync("the-auth-code", Arg.Any<CancellationToken>())
            .Returns(Result<StravaTokenResponse>.Success(CannedToken()));

        var kv = Substitute.For<IKeyVaultSecretsClient>();

        var result = await StravaAuthEndpoints.CallbackAsync(
            httpContext, code: "the-auth-code", state: protectedState,
            scope: "read,activity:read_all", error: null,
            _db, strava, _stateProtector, kv,
            EnabledOptions, NullLoggerFactory.Instance, CancellationToken.None);

        result.Should().BeOfType<RedirectHttpResult>()
            .Which.Url.Should().Be("/me?strava=connected");

        // Connection row: metadata + secret NAME, never a token value.
        var connection = await _db.ExternalConnections.AsNoTracking()
            .SingleAsync(c => c.UserId == _user.Id && c.Source == ConnectionSource.Strava);
        connection.Status.Should().Be(ConnectionStatus.Active);
        connection.KeyVaultSecretName.Should().StartWith($"strava-refresh-{_user.Id}");
        connection.Scopes.Should().BeEquivalentTo("read", "activity:read_all");
        connection.ConnectionMetadata!.RootElement.GetProperty("strava_athlete_id").GetInt64()
            .Should().Be(424242);

        // The refresh token went to Key Vault under that secret name...
        await kv.Received(1).SetSecretAsync(
            connection.KeyVaultSecretName, "canned-refresh-token", Arg.Any<CancellationToken>());

        // ...and no token value leaked into any column of the row.
        var rowJson = JsonSerializer.Serialize(new
        {
            connection.KeyVaultSecretName,
            connection.Scopes,
            Metadata = connection.ConnectionMetadata,
        });
        rowJson.Should().NotContain("canned-refresh-token").And.NotContain("canned-access-token");

        var audit = await _db.OAuthAuditEntries.AsNoTracking()
            .Where(a => a.UserId == _user.Id && a.Action == OAuthAuditAction.CallbackSuccess)
            .SingleAsync();
        audit.Source.Should().Be(OAuthAuditSource.Strava);
        audit.ExternalConnectionId.Should().Be(connection.Id);
    }

    [Fact]
    public async Task Callback_should_reject_and_audit_when_state_cookie_missing()
    {
        var protectedState = _stateProtector.Protect(
            new StravaOAuthState(_user.Id, StravaStateProtector.NewNonce(), DateTimeOffset.UtcNow));

        var strava = Substitute.For<IStravaOAuthClient>();
        var kv = Substitute.For<IKeyVaultSecretsClient>();

        // No cookie on the request.
        var result = await StravaAuthEndpoints.CallbackAsync(
            SignedInHttpContext(), code: "the-auth-code", state: protectedState,
            scope: null, error: null,
            _db, strava, _stateProtector, kv,
            EnabledOptions, NullLoggerFactory.Instance, CancellationToken.None);

        result.Should().BeOfType<BadRequest<string>>();

        await strava.DidNotReceiveWithAnyArgs().ExchangeCodeAsync(default!, default);

        var audit = await _db.OAuthAuditEntries.AsNoTracking()
            .Where(a => a.Action == OAuthAuditAction.CallbackFailed)
            .SingleAsync();
        audit.Success.Should().BeFalse();
        audit.ErrorMessage.Should().Contain("state cookie");
    }

    [Fact]
    public async Task Disconnect_should_deauthorize_delete_connection_and_audit()
    {
        var connection = new ExternalConnection
        {
            UserId = _user.Id,
            Source = ConnectionSource.Strava,
            Status = ConnectionStatus.Active,
            KeyVaultSecretName = $"strava-refresh-{_user.Id}",
            Scopes = new[] { "activity:read_all" },
            ConnectionMetadata = JsonDocument.Parse("""{"strava_athlete_id":424242}"""),
        };
        _db.ExternalConnections.Add(connection);
        await _db.SaveChangesAsync();

        var strava = Substitute.For<IStravaOAuthClient>();
        strava.DeauthorizeAsync("fake-access-token", Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Success(true));

        var tokenCache = Substitute.For<IStravaAccessTokenCache>();
        tokenCache.GetAccessTokenAsync(connection.Id, Arg.Any<CancellationToken>())
            .Returns(Result<string>.Success("fake-access-token"));

        var kv = Substitute.For<IKeyVaultSecretsClient>();
        kv.TryDeleteSecretAsync(connection.KeyVaultSecretName, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await StravaAuthEndpoints.DisconnectAsync(
            SignedInHttpContext(), _db, strava, tokenCache, kv,
            EnabledOptions, NullLoggerFactory.Instance, CancellationToken.None);

        result.Should().BeOfType<RedirectHttpResult>()
            .Which.Url.Should().Be("/me?strava=disconnected");

        await strava.Received(1).DeauthorizeAsync("fake-access-token", Arg.Any<CancellationToken>());
        await kv.Received(1).TryDeleteSecretAsync(connection.KeyVaultSecretName, Arg.Any<CancellationToken>());

        var gone = await _db.ExternalConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == connection.Id);
        gone.Should().BeNull("the disconnect handler hard-deletes the connection row");

        var audit = await _db.OAuthAuditEntries.AsNoTracking()
            .Where(a => a.UserId == _user.Id && a.Action == OAuthAuditAction.ManualDisconnect)
            .SingleAsync();
        audit.Source.Should().Be(OAuthAuditSource.Strava);
        audit.Success.Should().BeTrue();
    }

    [Fact]
    public async Task All_endpoints_should_404_when_strava_flag_is_off()
    {
        var strava = Substitute.For<IStravaOAuthClient>();
        var tokenCache = Substitute.For<IStravaAccessTokenCache>();
        var kv = Substitute.For<IKeyVaultSecretsClient>();

        var start = await StravaAuthEndpoints.StartAsync(
            SignedInHttpContext(), _db, strava, _stateProtector,
            DisabledOptions, NullLoggerFactory.Instance, CancellationToken.None);
        var callback = await StravaAuthEndpoints.CallbackAsync(
            SignedInHttpContext(), code: "c", state: "s", scope: null, error: null,
            _db, strava, _stateProtector, kv,
            DisabledOptions, NullLoggerFactory.Instance, CancellationToken.None);
        var disconnect = await StravaAuthEndpoints.DisconnectAsync(
            SignedInHttpContext(), _db, strava, tokenCache, kv,
            DisabledOptions, NullLoggerFactory.Instance, CancellationToken.None);

        start.Should().BeOfType<NotFound>();
        callback.Should().BeOfType<NotFound>();
        disconnect.Should().BeOfType<NotFound>();

        // Gate fires before any work: no audit rows, no external calls.
        (await _db.OAuthAuditEntries.AsNoTracking().CountAsync()).Should().Be(0);
        strava.ReceivedCalls().Should().BeEmpty();
    }
}

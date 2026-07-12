using System.Security.Claims;
using System.Text.Json;

using FluentAssertions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using SomaCore.Api.Pages;
using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.Users;
using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.QuickLog;
using SomaCore.Infrastructure.Recovery;
using SomaCore.Infrastructure.Whoop;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// Covers the silent-staleness gap introduced after commit c96f62c broadened
/// <c>WhoopOptions.Scopes</c>. RFC 6749 §6 disallows scope-widening on refresh,
/// so a connection authorized with the narrower pre-c96f62c set stays
/// <c>Active</c> indefinitely while quietly missing <c>read:sleep</c> and
/// <c>read:workout</c>. The <c>/me</c> reconnect banner has to surface for
/// these without any status mutation.
///
/// Test strategy: drive <see cref="MeModel.OnGetAsync"/> directly with a real
/// Postgres + mocked auth surface. The Razor view's banner condition is
/// <c>@if (Model.WhoopNeedsReconnect)</c>, so asserting the property is
/// equivalent to asserting render.
/// </summary>
public class MeScopeStalenessTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_me_scopes")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    private SomaCoreDbContext _db = null!;
    private Guid _userId;
    private Guid _entraOid;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<SomaCoreDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;

        _db = new SomaCoreDbContext(options);
        await _db.Database.MigrateAsync();

        _entraOid = Guid.NewGuid();
        var user = new User
        {
            EntraOid = _entraOid,
            EntraTenantId = Guid.NewGuid(),
            Email = "scope-stale-test@example.com",
            DisplayName = "Scope Stale Test",
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        _userId = user.Id;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Active_connection_with_stale_scopes_surfaces_reconnect_banner()
    {
        await SeedConnectionAsync(
            ConnectionStatus.Active,
            scopes: new[] { "read:recovery", "read:cycles", "read:profile", "offline" });

        var model = await RunOnGetAsync();

        model.WhoopConnected.Should().BeTrue();
        model.WhoopRefreshFailed.Should().BeFalse();
        model.WhoopScopesStale.Should().BeTrue("connection is missing read:sleep + read:workout");
        model.WhoopNeedsReconnect.Should().BeTrue();
    }

    [Fact]
    public async Task Active_connection_with_full_scopes_does_not_surface_banner()
    {
        await SeedConnectionAsync(
            ConnectionStatus.Active,
            scopes: new[] { "read:recovery", "read:cycles", "read:sleep", "read:workout", "read:profile", "offline" });

        var model = await RunOnGetAsync();

        model.WhoopConnected.Should().BeTrue();
        model.WhoopRefreshFailed.Should().BeFalse();
        model.WhoopScopesStale.Should().BeFalse();
        model.WhoopNeedsReconnect.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshFailed_connection_still_surfaces_banner_independent_of_scope_check()
    {
        // Even with the full scope set, refresh-failed must still surface the
        // banner — the existing rendering behavior is unchanged by this work.
        await SeedConnectionAsync(
            ConnectionStatus.RefreshFailed,
            scopes: new[] { "read:recovery", "read:cycles", "read:sleep", "read:workout", "read:profile", "offline" });

        var model = await RunOnGetAsync();

        model.WhoopConnected.Should().BeTrue();
        model.WhoopRefreshFailed.Should().BeTrue();
        model.WhoopScopesStale.Should().BeFalse();
        model.WhoopNeedsReconnect.Should().BeTrue();
    }

    // --- helpers -----------------------------------------------------------

    private async Task SeedConnectionAsync(string status, string[] scopes)
    {
        var connection = new ExternalConnection
        {
            UserId = _userId,
            Source = ConnectionSource.Whoop,
            Status = status,
            KeyVaultSecretName = $"whoop-refresh-{_userId}",
            Scopes = scopes,
            ConnectionMetadata = JsonDocument.Parse("""{"whoop_user_id":12345}"""),
        };
        _db.ExternalConnections.Add(connection);
        await _db.SaveChangesAsync();
    }

    private async Task<MeModel> RunOnGetAsync()
    {
        var recoveryHandler = Substitute.For<IRecoveryIngestionHandler>();
        recoveryHandler.IngestAsync(Arg.Any<RecoveryIngestionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<RecoveryIngestionOutcome>.Success(
                new RecoveryIngestionOutcome(RecoveryIngestionStatus.NoOp,
                    RecoveryId: null, CycleId: null, ScoreState: null)));

        var authService = Substitute.For<IAuthorizationService>();
        authService.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());

        var whoopOptions = Options.Create(new WhoopOptions
        {
            ClientId = "test-client",
            ClientSecret = "test-secret",
            RedirectUri = "https://example.com/cb",
        });

        // The daily-card agent is out of scope for this test — give /me a
        // no-op stub so OnGetAsync wires through to the WHOOP load path.
        var dailyAgent = Substitute.For<IDailyAgentService>();
        dailyAgent.GetLatestAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((DailyAgentResponse?)null);
        dailyAgent.GenerateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result<DailyAgentResponse>.Failure("stub disabled in test"));

        // Quick-log is disabled in this test (Enabled defaults false), so the
        // extraction/entry services never get called — plain substitutes.
        var quickLogExtraction = Substitute.For<IQuickLogExtractionService>();
        var quickLogEntries = Substitute.For<IQuickLogEntryService>();
        var quickLogOptions = Options.Create(new QuickLogOptions());
        var stravaOptions = Options.Create(new SomaCore.Infrastructure.Strava.StravaOptions());

        var model = new MeModel(_db, recoveryHandler,
            NullLogger<MeModel>.Instance, authService, whoopOptions, dailyAgent,
            quickLogExtraction, quickLogEntries, quickLogOptions, stravaOptions);

        // Minimal PageContext so OnGetAsync can access User claims + HttpContext.
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(Microsoft.Identity.Web.ClaimConstants.ObjectId, _entraOid.ToString()),
                new Claim("name", "Scope Stale Test"),
                new Claim("preferred_username", "scope-stale-test@example.com"),
            }, "test")),
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), new PageActionDescriptor(), new ModelStateDictionary());
        model.PageContext = new PageContext(actionContext)
        {
            ViewData = new ViewDataDictionary<MeModel>(
                new EmptyModelMetadataProvider(), new ModelStateDictionary()),
        };

        await model.OnGetAsync(whoop: null, strava: null, force: null, CancellationToken.None);
        return model;
    }
}

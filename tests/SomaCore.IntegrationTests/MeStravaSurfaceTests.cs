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
using SomaCore.Infrastructure.Strava;
using SomaCore.Infrastructure.Whoop;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// S7 coverage: the /me Strava card is flag-gated. Every Strava element in
/// Me.cshtml sits under <c>@if (Model.StravaEnabled)</c>, so asserting the
/// model properties is equivalent to asserting render (same strategy as
/// <see cref="MeScopeStalenessTests"/>).
/// </summary>
public class MeStravaSurfaceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_me_strava")
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
            Email = "me-strava-test@example.com",
            DisplayName = "Me Strava Test",
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

    private async Task SeedStravaConnectionAsync(string status = ConnectionStatus.Active)
    {
        _db.ExternalConnections.Add(new ExternalConnection
        {
            UserId = _userId,
            Source = ConnectionSource.Strava,
            Status = status,
            KeyVaultSecretName = $"strava-refresh-{_userId}",
            Scopes = new[] { "activity:read_all" },
            ConnectionMetadata = JsonDocument.Parse(
                """{"strava_athlete_id":424242,"strava_username":"adamw"}"""),
        });
        await _db.SaveChangesAsync();
    }

    private async Task<MeModel> RunOnGetAsync(bool stravaEnabled, string? stravaQuery = null)
    {
        var recoveryHandler = Substitute.For<IRecoveryIngestionHandler>();

        var authService = Substitute.For<IAuthorizationService>();
        authService.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());

        var whoopOptions = Options.Create(new WhoopOptions
        {
            ClientId = "test-client",
            ClientSecret = "test-secret",
            RedirectUri = "https://example.com/cb",
        });

        var dailyAgent = Substitute.For<IDailyAgentService>();
        dailyAgent.GetLatestAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((DailyAgentResponse?)null);
        dailyAgent.GenerateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result<DailyAgentResponse>.Failure("stub disabled in test"));

        var model = new MeModel(
            _db,
            recoveryHandler,
            NullLogger<MeModel>.Instance,
            authService,
            whoopOptions,
            dailyAgent,
            Substitute.For<IQuickLogExtractionService>(),
            Substitute.For<IQuickLogEntryService>(),
            Options.Create(new QuickLogOptions()),
            Options.Create(new StravaOptions { Enabled = stravaEnabled }));

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(Microsoft.Identity.Web.ClaimConstants.ObjectId, _entraOid.ToString()),
                new Claim("name", "Me Strava Test"),
                new Claim("preferred_username", "me-strava-test@example.com"),
            }, "test")),
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), new PageActionDescriptor(), new ModelStateDictionary());
        model.PageContext = new PageContext(actionContext)
        {
            ViewData = new ViewDataDictionary<MeModel>(
                new EmptyModelMetadataProvider(), new ModelStateDictionary()),
        };

        await model.OnGetAsync(whoop: null, strava: stravaQuery, force: null, CancellationToken.None);
        return model;
    }

    [Fact]
    public async Task Flag_off_renders_no_strava_ui_even_with_a_live_connection()
    {
        // Even with a connected Strava row in the DB, the default (off) flag
        // must suppress the whole surface — StravaEnabled is the razor gate,
        // and the model must not even load the connection state.
        await SeedStravaConnectionAsync();

        var model = await RunOnGetAsync(stravaEnabled: false, stravaQuery: "connected");

        model.StravaEnabled.Should().BeFalse("Strava:Enabled defaults false — the loop never enables it");
        model.StravaConnected.Should().BeFalse("the flag-off path must not load Strava state");
        model.StravaBanner.Should().BeNull("banners are part of the gated surface");
    }

    [Fact]
    public async Task Flag_on_shows_connection_state_and_banner()
    {
        await SeedStravaConnectionAsync();

        var model = await RunOnGetAsync(stravaEnabled: true, stravaQuery: "connected");

        model.StravaEnabled.Should().BeTrue();
        model.StravaConnected.Should().BeTrue();
        model.StravaAthleteId.Should().Be(424242);
        model.StravaUsername.Should().Be("adamw");
        model.StravaRefreshFailed.Should().BeFalse();
        model.StravaBanner.Should().Be("Strava connected.");
    }

    [Fact]
    public async Task Flag_on_without_connection_offers_connect()
    {
        var model = await RunOnGetAsync(stravaEnabled: true);

        model.StravaEnabled.Should().BeTrue();
        model.StravaConnected.Should().BeFalse("no connection row exists — the card offers Connect");
    }

    [Fact]
    public async Task Refresh_failed_connection_surfaces_reconnect_state()
    {
        await SeedStravaConnectionAsync(ConnectionStatus.RefreshFailed);

        var model = await RunOnGetAsync(stravaEnabled: true);

        model.StravaConnected.Should().BeTrue();
        model.StravaRefreshFailed.Should().BeTrue("refresh-failed drives the reconnect banner");
    }
}

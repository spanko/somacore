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

using NSubstitute;

using SomaCore.Api.Pages.Admin;
using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.Users;
using SomaCore.Infrastructure.Backfill;
using SomaCore.Infrastructure.Persistence;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

/// <summary>
/// Regression cover for the BackfillModel page-load query. The original
/// implementation did
/// <c>.Join(...).Select(record-ctor).OrderBy(o =&gt; o.Email)</c> which EF
/// can't translate to SQL — the page returned a 500 in dev (visible only
/// at runtime against a real Npgsql provider; in-memory tests would have
/// happily evaluated it client-side). This test exercises
/// <c>OnGetAsync</c> against a real Postgres so any future LINQ
/// translation regression fails the build instead of the user's browser.
/// </summary>
public class AdminBackfillPageTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_admin_backfill")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    private SomaCoreDbContext _db = null!;
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

        // Two users: one with active WHOOP, one with no connection. The page
        // must surface only the active one and order alphabetically by email.
        var adam = new User
        {
            EntraOid = _entraOid,
            EntraTenantId = Guid.NewGuid(),
            Email = "z-adam@example.com",
            DisplayName = "Adam",
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        var tai = new User
        {
            EntraOid = Guid.NewGuid(),
            EntraTenantId = Guid.NewGuid(),
            Email = "a-tai@example.com",
            DisplayName = "Tai",
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _db.Users.AddRange(adam, tai);
        await _db.SaveChangesAsync();

        _db.ExternalConnections.AddRange(
            new ExternalConnection
            {
                UserId = adam.Id,
                Source = ConnectionSource.Whoop,
                Status = ConnectionStatus.Active,
                KeyVaultSecretName = $"whoop-refresh-{adam.Id}",
                Scopes = new[] { "read:recovery", "offline" },
                ConnectionMetadata = JsonDocument.Parse("""{"whoop_user_id":1}"""),
            },
            new ExternalConnection
            {
                UserId = tai.Id,
                Source = ConnectionSource.Whoop,
                Status = ConnectionStatus.Active,
                KeyVaultSecretName = $"whoop-refresh-{tai.Id}",
                Scopes = new[] { "read:recovery", "offline" },
                ConnectionMetadata = JsonDocument.Parse("""{"whoop_user_id":2}"""),
            });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task OnGet_loads_active_connections_ordered_by_email_without_throwing()
    {
        var model = await RunOnGetAsync();

        model.Connections.Should().HaveCount(2);
        model.Connections.Select(c => c.Email).Should().ContainInOrder(
            "a-tai@example.com", "z-adam@example.com");
        model.Days.Should().Be(30, "the default window should be 30 days");
        model.LastSummary.Should().BeNull("no run has happened yet on a GET");
        model.LastError.Should().BeNull();
    }

    private async Task<BackfillModel> RunOnGetAsync()
    {
        var backfill = Substitute.For<IWhoopBackfillService>();
        var model = new BackfillModel(_db, backfill, NullLogger<BackfillModel>.Instance);

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(Microsoft.Identity.Web.ClaimConstants.ObjectId, _entraOid.ToString()),
                new Claim("name", "Admin"),
                new Claim("preferred_username", "admin@example.com"),
            }, "test")),
        };
        var actionContext = new ActionContext(
            httpContext, new RouteData(), new PageActionDescriptor(), new ModelStateDictionary());
        model.PageContext = new PageContext(actionContext)
        {
            ViewData = new ViewDataDictionary<BackfillModel>(
                new EmptyModelMetadataProvider(), new ModelStateDictionary()),
        };

        await model.OnGetAsync(CancellationToken.None);
        return model;
    }
}

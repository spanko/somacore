using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

using Serilog;
using Serilog.Formatting.Compact;

using SomaCore.Api;
using SomaCore.Api.Authentication;
using SomaCore.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

const string LocalDevConnectionString =
    "Host=localhost;Port=5432;Database=somacore;Username=somacore;Password=devonly";

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? LocalDevConnectionString;

builder.Services.AddSomaCoreInfrastructure(connectionString);

// Container Apps ingress terminates TLS; the container sees plain HTTP. Honor
// X-Forwarded-Proto/Host so OIDC redirect_uri composition uses https://app-dev...
// rather than http://app-dev... (which Entra rejects).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
    // Trust the platform's proxy regardless of source IP — Container Apps ingress
    // is the only thing in front of us.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Microsoft Entra ID OIDC sign-in for the Razor Pages surface.
builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

// Default policy = "must be authenticated"; individual endpoints opt out via [AllowAnonymous].
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services
    .AddRazorPages(options =>
    {
        // Sign-in/sign-out endpoints from Microsoft.Identity.Web.UI are open by design.
        options.Conventions.AllowAnonymousToAreaFolder("MicrosoftIdentity", "/Account");
    })
    .AddMicrosoftIdentityUI();

builder.Services.AddScoped<IUserProvisioningService, UserProvisioningService>();

var app = builder.Build();

// MUST come first so subsequent middleware sees the original scheme/host.
app.UseForwardedHeaders();

app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<JitUserProvisioningMiddleware>();

app.MapGet("/", () => Results.Ok(ServiceInfo.Default))
    .AllowAnonymous();

app.MapGet("/admin/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous();

app.MapRazorPages();

app.Run();

namespace SomaCore.Api
{
    public static class ServiceInfo
    {
        public static object Default { get; } = new { service = "somacore-api", version = "0.1.0" };
    }
}

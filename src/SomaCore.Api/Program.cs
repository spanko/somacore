using Azure.Monitor.OpenTelemetry.Exporter;

using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

using OpenTelemetry.Trace;

using Serilog;
using Serilog.Formatting.Compact;

using SomaCore.Api;
using SomaCore.Api.Authentication;
using SomaCore.Api.Whoop;
using SomaCore.Infrastructure;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Whoop;

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
builder.Services.AddSomaCoreKeyVault(builder.Configuration);
builder.Services.AddSomaCoreWhoop(builder.Configuration);
builder.Services.AddSomaCoreAgent(builder.Configuration);
builder.Services.AddSomaCoreTelemetry(builder.Configuration);
builder.Services.AddSingleton<IWhoopStateProtector, WhoopStateProtector>();

// Application Insights exporter for the ingestion trace contract (ADR 0011).
// Opt-in via Telemetry:ApplicationInsightsConnectionString (typically a Key
// Vault reference). When unset (local dev), spans still emit through the
// ActivitySource but nothing leaves the process.
var aiConnectionString = builder.Configuration[$"{TelemetryOptions.SectionName}:ApplicationInsightsConnectionString"];
if (!string.IsNullOrWhiteSpace(aiConnectionString))
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t.AddAzureMonitorTraceExporter(o => o.ConnectionString = aiConnectionString));
}
builder.Services.AddHostedService<SomaCore.Api.Whoop.WhoopWebhookDrainer>();

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

// Use the OIDC authorization-code flow with PKCE. MIW defaults to implicit
// id_token (response_type=id_token); the SomaCore Web app reg has implicit
// flows disabled per security best practice, so we opt into code flow which
// uses the back-channel /token endpoint with the client secret.
builder.Services.Configure<OpenIdConnectOptions>(
    OpenIdConnectDefaults.AuthenticationScheme,
    options =>
    {
        options.ResponseType = "code";
        options.UsePkce = true;
        options.SaveTokens = false; // /me reads claims, not tokens; nothing to persist
    });

// Bind the admin allowlist (Entra OIDs) so the "Admin" policy can read it.
builder.Services
    .AddOptions<SomaCore.Api.Authentication.AdminOptions>()
    .Bind(builder.Configuration.GetSection(SomaCore.Api.Authentication.AdminOptions.SectionName));

// Snapshot the admin OID list at startup so the policy assertion is allocation-free.
// Add or remove admins via env var + redeploy.
var adminOids = builder.Configuration
    .GetSection(SomaCore.Api.Authentication.AdminOptions.SectionName)
    .Get<SomaCore.Api.Authentication.AdminOptions>()
    ?.ParseUserOids() ?? Array.Empty<Guid>();

// Default policy = "must be authenticated"; individual endpoints opt out via [AllowAnonymous].
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    options.AddPolicy("Admin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            var oidStr = context.User.FindFirst(Microsoft.Identity.Web.ClaimConstants.ObjectId)?.Value
                ?? context.User.FindFirst("oid")?.Value;
            return Guid.TryParse(oidStr, out var entraOid) && adminOids.Contains(entraOid);
        });
    });
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
// Antiforgery middleware: validates the __RequestVerificationToken on POSTs
// that carry form data (Razor Pages' form tag helper auto-emits the token
// for any <form method="post"> on a .cshtml view). The WHOOP webhook is
// application/json + AllowAnonymous, so it's unaffected.
app.UseAntiforgery();
app.UseMiddleware<JitUserProvisioningMiddleware>();

app.MapGet("/", () => Results.Ok(ServiceInfo.Default))
    .AllowAnonymous();

// Simple liveness probe — anonymous, no DB. The rich /admin/health Razor page
// (admin-gated) shows webhook/recovery/job-run counts.
app.MapGet("/admin/health/live", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous();

app.MapRazorPages();
app.MapWhoopAuthEndpoints();
app.MapWhoopWebhookEndpoint();

app.Run();

namespace SomaCore.Api
{
    public static class ServiceInfo
    {
        public static object Default { get; } = new { service = "somacore-api", version = "0.1.0" };
    }
}

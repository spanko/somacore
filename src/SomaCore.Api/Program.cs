using Serilog;
using Serilog.Formatting.Compact;

using SomaCore.Api;
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

var app = builder.Build();

app.UseSerilogRequestLogging();

app.MapGet("/", () => Results.Ok(ServiceInfo.Default));
app.MapGet("/admin/health", () => Results.Ok(new { status = "ok" }));

app.Run();

namespace SomaCore.Api
{
    public static class ServiceInfo
    {
        public static object Default { get; } = new { service = "somacore-api", version = "0.1.0" };
    }
}

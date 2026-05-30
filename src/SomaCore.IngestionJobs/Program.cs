using Azure.Monitor.OpenTelemetry.Exporter;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using OpenTelemetry.Trace;

using Serilog;
using Serilog.Formatting.Compact;

using SomaCore.Infrastructure;
using SomaCore.Infrastructure.Observability;
using SomaCore.IngestionJobs.Jobs;

var jobName = ParseJobArg(args);
if (string.IsNullOrWhiteSpace(jobName))
{
    Console.Error.WriteLine("Usage: SomaCore.IngestionJobs --job=<name>");
    return 64;
}

const string LocalDevConnectionString =
    "Host=localhost;Port=5432;Database=somacore;Username=somacore;Password=devonly";

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("job", jobName)
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? LocalDevConnectionString;
builder.Services.AddSomaCoreInfrastructure(connectionString);
builder.Services.AddSomaCoreKeyVault(builder.Configuration);
builder.Services.AddSomaCoreWhoop(builder.Configuration);
builder.Services.AddSomaCoreTelemetry(builder.Configuration);

// Application Insights exporter for the ingestion trace contract (ADR 0011).
// Mirrors the Api Program.cs pattern: opt-in via configuration, only enables
// the exporter when a connection string is set. The ActivitySource is
// registered transitively by AddSomaCoreTelemetry; tests in-process can still
// observe spans via an ActivityListener even when no exporter is wired.
var aiConnectionString = builder.Configuration[$"{TelemetryOptions.SectionName}:ApplicationInsightsConnectionString"];
if (!string.IsNullOrWhiteSpace(aiConnectionString))
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t.AddAzureMonitorTraceExporter(o => o.ConnectionString = aiConnectionString));
}

builder.Services.AddScoped<JobDispatcher>();
builder.Services.AddScoped<IJob, ReconciliationPoller>();
builder.Services.AddScoped<IJob, TokenRefreshSweeper>();

using var host = builder.Build();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var scope = host.Services.CreateScope();
var dispatcher = scope.ServiceProvider.GetRequiredService<JobDispatcher>();
return await dispatcher.DispatchAsync(jobName, cts.Token);

static string? ParseJobArg(string[] args)
{
    const string prefix = "--job=";
    foreach (var arg in args)
    {
        if (arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            return arg[prefix.Length..];
        }
    }
    return null;
}

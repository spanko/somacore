using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Serilog;
using Serilog.Formatting.Compact;

using SomaCore.Infrastructure;
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

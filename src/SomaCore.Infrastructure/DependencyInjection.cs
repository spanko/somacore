using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Backfill;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Persistence.Interceptors;
using SomaCore.Infrastructure.Recovery;
using SomaCore.Infrastructure.Secrets;
using SomaCore.Infrastructure.Sleep;
using SomaCore.Infrastructure.Whoop;
using SomaCore.Infrastructure.Workout;

namespace SomaCore.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSomaCoreInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddSingleton<TimestampInterceptor>();

        services.AddDbContext<SomaCoreDbContext>((sp, options) =>
        {
            options
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>());
        });

        return services;
    }

    public static IServiceCollection AddSomaCoreKeyVault(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<KeyVaultOptions>()
            .Bind(configuration.GetSection(KeyVaultOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IKeyVaultSecretsClient, AzureKeyVaultSecretsClient>();
        return services;
    }

    public static IServiceCollection AddSomaCoreWhoop(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<WhoopOptions>()
            .Bind(configuration.GetSection(WhoopOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services
            .AddHttpClient<IWhoopOAuthClient, WhoopOAuthClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            });

        services
            .AddHttpClient<IWhoopApiClient, WhoopApiClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            });

        services.AddSingleton<IWhoopWebhookSignatureValidator, WhoopWebhookSignatureValidator>();

        // Token cache is a singleton so the in-memory entries survive across requests;
        // it pulls a scoped DbContext via IServiceScopeFactory when it needs to write.
        services.AddSingleton<IWhoopAccessTokenCache, WhoopAccessTokenCache>();
        services.AddScoped<IRecoveryIngestionHandler, RecoveryIngestionHandler>();
        services.AddScoped<IWhoopSleepIngestionHandler, WhoopSleepIngestionHandler>();
        services.AddScoped<IWhoopWorkoutIngestionHandler, WhoopWorkoutIngestionHandler>();
        services.AddScoped<IWhoopBackfillService, WhoopBackfillService>();

        return services;
    }

    /// <summary>
    /// Daily-card agent (ADR 0012). Phase-1 stub implementation; the Fable 5
    /// backed version registers via this same extension method once
    /// persona + bounds + privacy review land.
    /// </summary>
    public static IServiceCollection AddSomaCoreAgent(
        this IServiceCollection services)
    {
        services.AddScoped<IDailyAgentService, StubDailyAgentService>();
        return services;
    }

    /// <summary>
    /// Registers the OpenTelemetry tracer provider with the ingestion
    /// ActivitySource from ADR 0011. Source-side only — exporters (Azure
    /// Monitor / Application Insights) are wired in the host project that
    /// owns the connection string, to avoid pulling Azure.Monitor into the
    /// IngestionJobs and Infrastructure projects when they don't export.
    /// </summary>
    public static IServiceCollection AddSomaCoreTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<TelemetryOptions>()
            .Bind(configuration.GetSection(TelemetryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var telemetry = configuration
            .GetSection(TelemetryOptions.SectionName)
            .Get<TelemetryOptions>() ?? new TelemetryOptions();

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName: telemetry.ServiceName))
            .WithTracing(tracing => tracing.AddSource(IngestionTracing.SourceName));

        return services;
    }
}

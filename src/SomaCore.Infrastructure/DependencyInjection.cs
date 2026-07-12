using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SomaCore.Infrastructure.Agent;
using SomaCore.Infrastructure.Backfill;
using SomaCore.Infrastructure.Coach;
using SomaCore.Infrastructure.Labs;
using SomaCore.Infrastructure.Mfp;
using SomaCore.Infrastructure.Observability;
using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Persistence.Interceptors;
using SomaCore.Infrastructure.QuickLog;
using SomaCore.Infrastructure.Recovery;
using SomaCore.Infrastructure.Secrets;
using SomaCore.Infrastructure.Sleep;
using SomaCore.Infrastructure.Strava;
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
    /// Strava direct-API integration (Track D Session 3, S2). Unlike WHOOP's
    /// registration, options carry no [Required] validation — the section is
    /// expected to be absent while <see cref="StravaOptions.Enabled"/> is
    /// false, and every endpoint 404s on the flag. Registrations are
    /// unconditional (same as WHOOP's) so a flag flip is config-only.
    /// </summary>
    public static IServiceCollection AddSomaCoreStrava(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<StravaOptions>()
            .Bind(configuration.GetSection(StravaOptions.SectionName));

        services
            .AddHttpClient<IStravaOAuthClient, StravaOAuthClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            });

        // Token cache is a singleton so the in-memory entries survive across requests;
        // it pulls a scoped DbContext via IServiceScopeFactory when it needs to write.
        services.AddSingleton<IStravaAccessTokenCache, StravaAccessTokenCache>();

        services
            .AddHttpClient<IStravaApiClient, StravaApiClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            });

        // S3/S4 seam: the webhook drainer + reconciliation poller dispatch
        // activity fetch+upsert through this.
        services.AddScoped<IStravaActivityIngestService, StravaActivityIngestService>();

        return services;
    }

    /// <summary>
    /// Daily-card agent (ADR 0012).
    ///
    /// The router fronts <see cref="IDailyAgentService"/>. The stub always
    /// registers. The Anthropic-backed live service registers only when
    /// <see cref="AnthropicOptions.Enabled"/> is true AND
    /// <see cref="AnthropicOptions.ApiKey"/> is populated — so a
    /// half-configured environment keeps showing the stub instead of
    /// crashing at first invocation.
    ///
    /// Per-user opt-in lives on <c>users.agent_opt_in</c>. The router reads
    /// it per call so flips take effect without a redeploy.
    /// </summary>
    public static IServiceCollection AddSomaCoreAgent(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<AnthropicOptions>()
            .Bind(configuration.GetSection(AnthropicOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<StubDailyAgentService>();

        var anthropic = configuration
            .GetSection(AnthropicOptions.SectionName)
            .Get<AnthropicOptions>() ?? new AnthropicOptions();

        if (anthropic.Enabled && !string.IsNullOrWhiteSpace(anthropic.ApiKey))
        {
            // Single long-lived HttpClient + Singleton service. Our request
            // volume is tiny (one call per /me load per opted-in user) so
            // socket-exhaustion isn't a real risk and we trade IHttpClientFactory's
            // handler-rotation for a simpler captive-dependency-free shape.
            services.AddSingleton(sp =>
            {
                var opts = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
                var http = new HttpClient();
                return new AnthropicMessagesClient(
                    AnthropicMessagesClient.ConfigureHttp(
                        http, opts.ApiKey, TimeSpan.FromSeconds(opts.RequestTimeoutSeconds)));
            });
            services.AddSingleton<LiveDailyAgentService>();
        }

        services.AddScoped<IDailyAgentService, DailyAgentRouter>();

        // Quick-log (session-quick-log.md). The extraction service is always
        // registered; it no-ops with a Failure when QuickLog:Enabled=false
        // (privacy Part 4 gate) or when the Anthropic client isn't around —
        // same nullable-dependency pattern as DailyAgentRouter's live arm.
        services
            .AddOptions<QuickLogOptions>()
            .Bind(configuration.GetSection(QuickLogOptions.SectionName));
        services.AddScoped<IQuickLogExtractionService, QuickLogExtractionService>();
        services.AddScoped<IQuickLogEntryService, QuickLogEntryService>();

        // Coach conversation + documents (/me/coach). Same nullable-client
        // pattern: the services no-op with a Failure when CoachChat:Enabled
        // is false or the Anthropic client isn't registered.
        services
            .AddOptions<CoachChatOptions>()
            .Bind(configuration.GetSection(CoachChatOptions.SectionName));
        services.AddScoped<IUserDocumentService, UserDocumentService>();
        services.AddScoped<ICoachChatService, CoachChatService>();

        // Lab uploads (/me/labs, session-function-health-integration.md).
        services
            .AddOptions<LabsOptions>()
            .Bind(configuration.GetSection(LabsOptions.SectionName));
        services.AddScoped<ILabUploadService, LabUploadService>();

        // MFP CSV upload (/me/food, session-myfitnesspal-integration.md §1.3).
        // No Anthropic dependency — the export is parsed deterministically.
        services
            .AddOptions<MfpOptions>()
            .Bind(configuration.GetSection(MfpOptions.SectionName));
        services.AddScoped<IMfpCsvUploadService, MfpCsvUploadService>();

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

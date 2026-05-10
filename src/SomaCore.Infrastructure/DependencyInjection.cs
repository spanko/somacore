using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Persistence.Interceptors;
using SomaCore.Infrastructure.Secrets;
using SomaCore.Infrastructure.Whoop;

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

        return services;
    }
}

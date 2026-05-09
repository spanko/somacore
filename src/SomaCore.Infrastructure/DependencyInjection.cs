using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SomaCore.Infrastructure.Persistence;
using SomaCore.Infrastructure.Persistence.Interceptors;

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
}

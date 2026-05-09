using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SomaCore.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> tooling to construct a DbContext at design time
/// (e.g., when generating migrations). Never used at runtime.
/// </summary>
public sealed class SomaCoreDbContextDesignTimeFactory : IDesignTimeDbContextFactory<SomaCoreDbContext>
{
    public SomaCoreDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SOMACORE_DESIGN_TIME_PG")
            ?? "Host=localhost;Port=5432;Database=somacore;Username=somacore;Password=devonly";

        var options = new DbContextOptionsBuilder<SomaCoreDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new SomaCoreDbContext(options);
    }
}

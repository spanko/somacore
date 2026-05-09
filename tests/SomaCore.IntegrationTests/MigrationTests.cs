using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using SomaCore.Infrastructure.Persistence;

using Testcontainers.PostgreSql;

namespace SomaCore.IntegrationTests;

public class MigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("somacore_test")
        .WithUsername("somacore")
        .WithPassword("devonly")
        .Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Should_apply_initial_migration_to_a_real_postgres()
    {
        var options = new DbContextOptionsBuilder<SomaCoreDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var dbContext = new SomaCoreDbContext(options);

        await dbContext.Database.MigrateAsync();

        var tables = await dbContext.Database
            .SqlQueryRaw<string>(
                "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name")
            .ToListAsync();

        tables.Should().Contain(new[]
        {
            "users",
            "external_connections",
            "whoop_recoveries",
            "webhook_events",
            "oauth_audit",
        });

        var indexes = await dbContext.Database
            .SqlQueryRaw<string>(
                "SELECT indexname AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' ORDER BY indexname")
            .ToListAsync();

        indexes.Should().Contain(new[]
        {
            "idx_users_email",
            "idx_external_connections_user_source_active",
            "idx_external_connections_next_refresh",
            "idx_external_connections_user_id",
            "idx_whoop_recoveries_connection_cycle",
            "idx_whoop_recoveries_user_cycle_start",
            "idx_whoop_recoveries_sleep_id",
            "idx_webhook_events_dedupe",
            "idx_webhook_events_pending",
            "idx_webhook_events_user_received",
            "idx_oauth_audit_user_occurred",
            "idx_oauth_audit_connection_occurred",
        });

        var emailIndexDef = await dbContext.Database
            .SqlQueryRaw<string>(
                "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'idx_users_email'")
            .SingleAsync();

        emailIndexDef.Should().Contain("lower(email)");
    }
}

using Microsoft.EntityFrameworkCore;

using SomaCore.Domain.Agent;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.JobRuns;
using SomaCore.Domain.OAuthAudit;
using SomaCore.Domain.Users;
using SomaCore.Domain.WebhookEvents;
using SomaCore.Domain.WhoopRecoveries;
using SomaCore.Domain.WhoopSleeps;
using SomaCore.Domain.WhoopWorkouts;

namespace SomaCore.Infrastructure.Persistence;

public class SomaCoreDbContext : DbContext
{
    public SomaCoreDbContext(DbContextOptions<SomaCoreDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();

    public DbSet<ExternalConnection> ExternalConnections => Set<ExternalConnection>();

    public DbSet<WhoopRecovery> WhoopRecoveries => Set<WhoopRecovery>();

    public DbSet<WhoopSleep> WhoopSleeps => Set<WhoopSleep>();

    public DbSet<WhoopWorkout> WhoopWorkouts => Set<WhoopWorkout>();

    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    public DbSet<OAuthAuditEntry> OAuthAuditEntries => Set<OAuthAuditEntry>();

    public DbSet<JobRun> JobRuns => Set<JobRun>();

    public DbSet<AgentInvocation> AgentInvocations => Set<AgentInvocation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pgcrypto");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SomaCoreDbContext).Assembly);
    }
}

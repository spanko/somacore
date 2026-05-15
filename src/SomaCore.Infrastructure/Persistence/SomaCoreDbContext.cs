using Microsoft.EntityFrameworkCore;

using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.JobRuns;
using SomaCore.Domain.OAuthAudit;
using SomaCore.Domain.Users;
using SomaCore.Domain.WebhookEvents;
using SomaCore.Domain.WhoopRecoveries;

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

    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    public DbSet<OAuthAuditEntry> OAuthAuditEntries => Set<OAuthAuditEntry>();

    public DbSet<JobRun> JobRuns => Set<JobRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pgcrypto");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SomaCoreDbContext).Assembly);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.WhoopSleeps;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class WhoopSleepConfiguration : IEntityTypeConfiguration<WhoopSleep>
{
    public void Configure(EntityTypeBuilder<WhoopSleep> builder)
    {
        builder.ToTable("whoop_sleeps", t =>
        {
            t.HasCheckConstraint(
                "chk_whoop_sleeps_score_state",
                "score_state IN ('SCORED', 'PENDING_SCORE', 'UNSCORABLE')");
            t.HasCheckConstraint(
                "chk_whoop_sleeps_ingested_via",
                "ingested_via IN ('webhook', 'poller', 'on_open_pull', 'backfill')");
            t.HasCheckConstraint(
                "chk_whoop_sleeps_perf_range",
                "sleep_performance_percentage IS NULL OR (sleep_performance_percentage BETWEEN 0 AND 100)");
            t.HasCheckConstraint(
                "chk_whoop_sleeps_eff_range",
                "sleep_efficiency_percentage IS NULL OR (sleep_efficiency_percentage BETWEEN 0 AND 100)");
            t.HasCheckConstraint(
                "chk_whoop_sleeps_cons_range",
                "sleep_consistency_percentage IS NULL OR (sleep_consistency_percentage BETWEEN 0 AND 100)");
        });

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(s => s.UserId)
            .IsRequired();

        builder.Property(s => s.ExternalConnectionId);

        builder.Property(s => s.WhoopSleepId)
            .IsRequired();

        builder.Property(s => s.StartAt)
            .IsRequired();

        builder.Property(s => s.EndAt)
            .IsRequired();

        builder.Property(s => s.TimezoneOffset)
            .IsRequired();

        builder.Property(s => s.Nap)
            .IsRequired();

        builder.Property(s => s.ScoreState)
            .IsRequired();

        builder.Property(s => s.SleepPerformancePercentage)
            .HasPrecision(5, 2);

        builder.Property(s => s.SleepEfficiencyPercentage)
            .HasPrecision(5, 2);

        builder.Property(s => s.SleepConsistencyPercentage)
            .HasPrecision(5, 2);

        builder.Property(s => s.TotalInBedTimeMilli);

        builder.Property(s => s.TotalSleepTimeMilli);

        builder.Property(s => s.Score)
            .HasColumnType("jsonb");

        builder.Property(s => s.IngestedVia)
            .IsRequired();

        builder.Property(s => s.IngestedAt)
            .HasDefaultValueSql("now()");

        builder.Property(s => s.RawPayload)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(s => s.CreatedAt)
            .HasDefaultValueSql("now()");

        builder.Property(s => s.UpdatedAt)
            .HasDefaultValueSql("now()");

        builder.HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.ExternalConnection)
            .WithMany()
            .HasForeignKey(s => s.ExternalConnectionId)
            // SET NULL: disconnect severs the integration, not the data.
            // Sleep rows are tied to the user via UserId (cascade-deleted on
            // full account deletion); the connection relationship is severable.
            .OnDelete(DeleteBehavior.SetNull);

        // Idempotency / dedupe: one row per (connection, whoop sleep id).
        builder.HasIndex(s => new { s.ExternalConnectionId, s.WhoopSleepId })
            .IsUnique()
            .HasDatabaseName("idx_whoop_sleeps_connection_sleep");

        // Hot-path query: this user's recent sleeps (rules engine + /me).
        builder.HasIndex(s => new { s.UserId, s.StartAt })
            .IsDescending(false, true)
            .HasDatabaseName("idx_whoop_sleeps_user_start");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.WhoopRecoveries;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class WhoopRecoveryConfiguration : IEntityTypeConfiguration<WhoopRecovery>
{
    public void Configure(EntityTypeBuilder<WhoopRecovery> builder)
    {
        builder.ToTable("whoop_recoveries", t =>
        {
            t.HasCheckConstraint(
                "chk_whoop_recoveries_score_state",
                "score_state IN ('SCORED', 'PENDING_SCORE', 'UNSCORABLE')");
            t.HasCheckConstraint(
                "chk_whoop_recoveries_ingested_via",
                "ingested_via IN ('webhook', 'poller', 'on_open_pull')");
            t.HasCheckConstraint(
                "chk_whoop_recoveries_score_range",
                "recovery_score IS NULL OR (recovery_score BETWEEN 0 AND 100)");
            t.HasCheckConstraint(
                "chk_whoop_recoveries_spo2_range",
                "spo2_percentage IS NULL OR (spo2_percentage BETWEEN 0 AND 100)");
            t.HasCheckConstraint(
                "chk_whoop_recoveries_scored_has_score",
                "(score_state = 'SCORED' AND recovery_score IS NOT NULL) OR (score_state IN ('PENDING_SCORE', 'UNSCORABLE'))");
        });

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(r => r.UserId)
            .IsRequired();

        builder.Property(r => r.ExternalConnectionId)
            .IsRequired();

        builder.Property(r => r.WhoopCycleId)
            .IsRequired();

        builder.Property(r => r.WhoopSleepId);

        builder.Property(r => r.ScoreState)
            .IsRequired();

        builder.Property(r => r.RecoveryScore);

        builder.Property(r => r.HrvRmssdMilli)
            .HasPrecision(10, 4);

        builder.Property(r => r.RestingHeartRate);

        builder.Property(r => r.Spo2Percentage)
            .HasColumnName("spo2_percentage")
            .HasPrecision(5, 2);

        builder.Property(r => r.SkinTempCelsius)
            .HasPrecision(5, 2);

        builder.Property(r => r.CycleStartAt)
            .IsRequired();

        builder.Property(r => r.CycleEndAt);

        builder.Property(r => r.IngestedVia)
            .IsRequired();

        builder.Property(r => r.IngestedAt)
            .HasDefaultValueSql("now()");

        builder.Property(r => r.RawPayload)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(r => r.CreatedAt)
            .HasDefaultValueSql("now()");

        builder.Property(r => r.UpdatedAt)
            .HasDefaultValueSql("now()");

        builder.HasOne(r => r.User)
            .WithMany(u => u.WhoopRecoveries)
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.ExternalConnection)
            .WithMany(c => c.WhoopRecoveries)
            .HasForeignKey(r => r.ExternalConnectionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Idempotency / dedupe: one row per (connection, cycle).
        builder.HasIndex(r => new { r.ExternalConnectionId, r.WhoopCycleId })
            .IsUnique()
            .HasDatabaseName("idx_whoop_recoveries_connection_cycle");

        // Hot-path query for /me: this user's recent recoveries.
        builder.HasIndex(r => new { r.UserId, r.CycleStartAt })
            .IsDescending(false, true)
            .HasDatabaseName("idx_whoop_recoveries_user_cycle_start");

        // Lookup by sleep ID (webhook payload identifies recoveries by sleep UUID).
        builder.HasIndex(r => r.WhoopSleepId)
            .HasFilter("whoop_sleep_id IS NOT NULL")
            .HasDatabaseName("idx_whoop_recoveries_sleep_id");
    }
}

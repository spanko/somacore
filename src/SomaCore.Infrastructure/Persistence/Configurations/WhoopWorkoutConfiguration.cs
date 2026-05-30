using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.WhoopWorkouts;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class WhoopWorkoutConfiguration : IEntityTypeConfiguration<WhoopWorkout>
{
    public void Configure(EntityTypeBuilder<WhoopWorkout> builder)
    {
        builder.ToTable("whoop_workouts", t =>
        {
            t.HasCheckConstraint(
                "chk_whoop_workouts_score_state",
                "score_state IN ('SCORED', 'PENDING_SCORE', 'UNSCORABLE')");
            t.HasCheckConstraint(
                "chk_whoop_workouts_ingested_via",
                "ingested_via IN ('webhook', 'poller', 'on_open_pull', 'backfill')");
            t.HasCheckConstraint(
                "chk_whoop_workouts_strain_range",
                "strain IS NULL OR (strain BETWEEN 0 AND 21)");
            t.HasCheckConstraint(
                "chk_whoop_workouts_avg_hr_range",
                "average_heart_rate IS NULL OR (average_heart_rate BETWEEN 0 AND 300)");
            t.HasCheckConstraint(
                "chk_whoop_workouts_max_hr_range",
                "max_heart_rate IS NULL OR (max_heart_rate BETWEEN 0 AND 300)");
        });

        builder.HasKey(w => w.Id);

        builder.Property(w => w.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(w => w.UserId)
            .IsRequired();

        builder.Property(w => w.ExternalConnectionId);

        builder.Property(w => w.WhoopWorkoutId)
            .IsRequired();

        builder.Property(w => w.StartAt)
            .IsRequired();

        builder.Property(w => w.EndAt)
            .IsRequired();

        builder.Property(w => w.TimezoneOffset)
            .IsRequired();

        builder.Property(w => w.SportName)
            .IsRequired();

        builder.Property(w => w.ScoreState)
            .IsRequired();

        builder.Property(w => w.Strain)
            .HasPrecision(6, 4);

        builder.Property(w => w.AverageHeartRate);

        builder.Property(w => w.MaxHeartRate);

        builder.Property(w => w.Kilojoule)
            .HasPrecision(10, 4);

        builder.Property(w => w.Score)
            .HasColumnType("jsonb");

        builder.Property(w => w.IngestedVia)
            .IsRequired();

        builder.Property(w => w.IngestedAt)
            .HasDefaultValueSql("now()");

        builder.Property(w => w.RawPayload)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(w => w.CreatedAt)
            .HasDefaultValueSql("now()");

        builder.Property(w => w.UpdatedAt)
            .HasDefaultValueSql("now()");

        builder.HasOne(w => w.User)
            .WithMany()
            .HasForeignKey(w => w.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(w => w.ExternalConnection)
            .WithMany()
            .HasForeignKey(w => w.ExternalConnectionId)
            // SET NULL: disconnect severs the integration, not the data.
            .OnDelete(DeleteBehavior.SetNull);

        // Idempotency / dedupe: one row per (connection, whoop workout id).
        builder.HasIndex(w => new { w.ExternalConnectionId, w.WhoopWorkoutId })
            .IsUnique()
            .HasDatabaseName("idx_whoop_workouts_connection_workout");

        // Hot-path query: this user's recent workouts.
        builder.HasIndex(w => new { w.UserId, w.StartAt })
            .IsDescending(false, true)
            .HasDatabaseName("idx_whoop_workouts_user_start");
    }
}

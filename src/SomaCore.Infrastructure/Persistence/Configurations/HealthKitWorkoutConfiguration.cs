using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.HealthKitWorkouts;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class HealthKitWorkoutConfiguration : IEntityTypeConfiguration<HealthKitWorkout>
{
    public void Configure(EntityTypeBuilder<HealthKitWorkout> builder)
    {
        builder.ToTable("healthkit_workouts", t =>
        {
            t.HasCheckConstraint(
                "chk_healthkit_workouts_elapsed_positive",
                "elapsed_seconds > 0");
            t.HasCheckConstraint(
                "chk_healthkit_workouts_avg_hr_range",
                "average_hr IS NULL OR (average_hr BETWEEN 0 AND 300)");
        });

        builder.HasKey(w => w.Id);

        builder.Property(w => w.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(w => w.UserId)
            .IsRequired();

        builder.Property(w => w.SourceBundleId)
            .IsRequired();

        builder.Property(w => w.HkSampleUuid)
            .IsRequired();

        builder.Property(w => w.WorkoutType)
            .IsRequired();

        builder.Property(w => w.StartedAt)
            .IsRequired();

        builder.Property(w => w.ElapsedSeconds)
            .IsRequired();

        builder.Property(w => w.TotalEnergyKcal).HasPrecision(8, 2);
        builder.Property(w => w.TotalDistanceM).HasPrecision(10, 2);

        builder.Property(w => w.HkMetadata)
            .HasColumnType("jsonb");

        builder.Property(w => w.IngestedAt)
            .HasDefaultValueSql("now()");

        builder.Property(w => w.CreatedAt)
            .HasDefaultValueSql("now()");

        builder.Property(w => w.UpdatedAt)
            .HasDefaultValueSql("now()");

        builder.HasOne(w => w.User)
            .WithMany()
            .HasForeignKey(w => w.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Idempotency: HKObject.uuid for iOS rows, fresh Guid for manual.
        builder.HasIndex(w => w.HkSampleUuid)
            .IsUnique()
            .HasDatabaseName("idx_healthkit_workouts_sample_uuid");

        builder.HasIndex(w => new { w.UserId, w.StartedAt })
            .IsDescending(false, true)
            .HasDatabaseName("idx_healthkit_workouts_user_started");
    }
}

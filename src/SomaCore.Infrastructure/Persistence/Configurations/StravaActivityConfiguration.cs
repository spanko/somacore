using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.StravaActivities;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class StravaActivityConfiguration : IEntityTypeConfiguration<StravaActivity>
{
    public void Configure(EntityTypeBuilder<StravaActivity> builder)
    {
        builder.ToTable("strava_activities", t =>
        {
            t.HasCheckConstraint(
                "chk_strava_activities_elapsed_positive",
                "elapsed_seconds > 0");
            t.HasCheckConstraint(
                "chk_strava_activities_avg_hr_range",
                "average_hr IS NULL OR (average_hr BETWEEN 0 AND 300)");
            t.HasCheckConstraint(
                "chk_strava_activities_max_hr_range",
                "max_hr IS NULL OR (max_hr BETWEEN 0 AND 300)");
        });

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(a => a.UserId)
            .IsRequired();

        builder.Property(a => a.ExternalConnectionId);

        builder.Property(a => a.StravaActivityId)
            .IsRequired();

        builder.Property(a => a.StravaAthleteId)
            .IsRequired();

        builder.Property(a => a.ActivityType)
            .IsRequired();

        builder.Property(a => a.StartedAt)
            .IsRequired();

        builder.Property(a => a.ElapsedSeconds)
            .IsRequired();

        builder.Property(a => a.HrZones)
            .HasColumnType("jsonb");

        builder.Property(a => a.Splits)
            .HasColumnType("jsonb");

        builder.Property(a => a.Laps)
            .HasColumnType("jsonb");

        builder.Property(a => a.RawSummaryPayload)
            .HasColumnType("jsonb");

        builder.Property(a => a.RawDetailPayload)
            .HasColumnType("jsonb");

        builder.Property(a => a.IngestedVia)
            .IsRequired();

        builder.Property(a => a.IngestedAt)
            .HasDefaultValueSql("now()");

        builder.Property(a => a.CreatedAt)
            .HasDefaultValueSql("now()");

        builder.Property(a => a.UpdatedAt)
            .HasDefaultValueSql("now()");

        builder.HasOne(a => a.User)
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.ExternalConnection)
            .WithMany()
            .HasForeignKey(a => a.ExternalConnectionId)
            // SET NULL: disconnect severs the integration, not the data.
            .OnDelete(DeleteBehavior.SetNull);

        // Idempotency / dedupe: Strava activity ids are globally unique bigints,
        // so the natural key needs no connection scoping (unlike WHOOP's).
        builder.HasIndex(a => a.StravaActivityId)
            .IsUnique()
            .HasDatabaseName("idx_strava_activities_activity_id");

        // Hot-path query: this user's recent activities.
        builder.HasIndex(a => new { a.UserId, a.StartedAt })
            .IsDescending(false, true)
            .HasDatabaseName("idx_strava_activities_user_started");
    }
}

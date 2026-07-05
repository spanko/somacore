using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.CoachThreads;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class CoachThreadConfiguration : IEntityTypeConfiguration<CoachThread>
{
    public void Configure(EntityTypeBuilder<CoachThread> builder)
    {
        builder.ToTable("coach_threads", t =>
        {
            t.HasCheckConstraint(
                "chk_coach_threads_subject_type",
                "subject_type IN ('document', 'meal', 'workout', 'note', 'general')");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.SubjectType).IsRequired();
        builder.Property(x => x.Title).HasMaxLength(200).IsRequired();
        builder.Property(x => x.LastMessageAt).HasDefaultValueSql("now()");
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.UserId, x.LastMessageAt })
            .IsDescending(false, true)
            .HasDatabaseName("idx_coach_threads_user_last_message");
    }
}

public sealed class CoachMessageConfiguration : IEntityTypeConfiguration<CoachMessage>
{
    public void Configure(EntityTypeBuilder<CoachMessage> builder)
    {
        builder.ToTable("coach_messages", t =>
        {
            t.HasCheckConstraint(
                "chk_coach_messages_role",
                "role IN ('user', 'coach')");
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(x => x.ThreadId).IsRequired();
        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.Role).IsRequired();
        builder.Property(x => x.Content).HasMaxLength(8000).IsRequired();
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        builder.HasOne(x => x.Thread)
            .WithMany(t => t.Messages)
            .HasForeignKey(x => x.ThreadId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.ThreadId, x.CreatedAt })
            .HasDatabaseName("idx_coach_messages_thread_created");
    }
}

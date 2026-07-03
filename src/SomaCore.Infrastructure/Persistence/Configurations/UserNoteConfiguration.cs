using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.UserNotes;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class UserNoteConfiguration : IEntityTypeConfiguration<UserNote>
{
    public void Configure(EntityTypeBuilder<UserNote> builder)
    {
        builder.ToTable("user_notes", t =>
        {
            t.HasCheckConstraint(
                "chk_user_notes_category",
                "category IS NULL OR category IN ('symptom', 'schedule', 'context')");
            t.HasCheckConstraint(
                "chk_user_notes_source",
                "source IN ('quick_log', 'conversation')");
        });

        builder.HasKey(n => n.Id);

        builder.Property(n => n.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(n => n.UserId)
            .IsRequired();

        builder.Property(n => n.Source)
            .IsRequired();

        builder.Property(n => n.Note)
            .HasMaxLength(2000)
            .IsRequired();

        builder.Property(n => n.CreatedAt)
            .HasDefaultValueSql("now()");

        builder.Property(n => n.UpdatedAt)
            .HasDefaultValueSql("now()");

        builder.HasOne(n => n.User)
            .WithMany()
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Hot path: "this user's active notes" at snapshot-build time.
        builder.HasIndex(n => new { n.UserId, n.ActiveUntil })
            .HasDatabaseName("idx_user_notes_user_active_until");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.LabUploads;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class LabUploadConfiguration : IEntityTypeConfiguration<LabUpload>
{
    public void Configure(EntityTypeBuilder<LabUpload> builder)
    {
        builder.ToTable("lab_uploads", t =>
        {
            t.HasCheckConstraint(
                "chk_lab_uploads_parse_status",
                "parse_status IN ('parsed', 'failed', 'confirmed')");
            t.HasCheckConstraint(
                "chk_lab_uploads_file_size",
                "file_size > 0 AND file_size <= 10485760");
        });

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(u => u.UserId).IsRequired();
        builder.Property(u => u.Source).IsRequired();
        builder.Property(u => u.UploadedAt).HasDefaultValueSql("now()");
        builder.Property(u => u.FileName).HasMaxLength(255).IsRequired();
        builder.Property(u => u.FileBytes).IsRequired();
        builder.Property(u => u.FileSize).IsRequired();
        builder.Property(u => u.ParseStatus).IsRequired();
        builder.Property(u => u.ParseError).HasMaxLength(2000);
        builder.Property(u => u.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(u => u.UpdatedAt).HasDefaultValueSql("now()");

        builder.HasOne(u => u.User)
            .WithMany()
            .HasForeignKey(u => u.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Re-uploading the same panel replaces it, not duplicates it —
        // one row per (user, source, collection date).
        builder.HasIndex(u => new { u.UserId, u.Source, u.CollectedAt })
            .IsUnique()
            .HasDatabaseName("idx_lab_uploads_user_collected");
    }
}

public sealed class LabBiomarkerConfiguration : IEntityTypeConfiguration<LabBiomarker>
{
    public void Configure(EntityTypeBuilder<LabBiomarker> builder)
    {
        builder.ToTable("lab_biomarkers", t =>
        {
            t.HasCheckConstraint(
                "chk_lab_biomarkers_flagged",
                "flagged IN ('in_range', 'low', 'high', 'unknown')");
        });

        builder.HasKey(b => b.Id);

        builder.Property(b => b.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(b => b.LabUploadId).IsRequired();
        builder.Property(b => b.UserId).IsRequired();
        builder.Property(b => b.BiomarkerName).HasMaxLength(100).IsRequired();
        builder.Property(b => b.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(b => b.Category).HasMaxLength(50).IsRequired();
        builder.Property(b => b.NumericValue).HasPrecision(12, 4);
        builder.Property(b => b.StringValue).HasMaxLength(200);
        builder.Property(b => b.Unit).HasMaxLength(50);
        builder.Property(b => b.ReferenceLow).HasPrecision(12, 4);
        builder.Property(b => b.ReferenceHigh).HasPrecision(12, 4);
        builder.Property(b => b.ReferenceString).HasMaxLength(100);
        builder.Property(b => b.CollectedAt).IsRequired();
        builder.Property(b => b.Flagged).IsRequired();
        builder.Property(b => b.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(b => b.UpdatedAt).HasDefaultValueSql("now()");

        builder.HasOne(b => b.LabUpload)
            .WithMany(u => u.Biomarkers)
            .HasForeignKey(b => b.LabUploadId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(b => b.User)
            .WithMany()
            .HasForeignKey(b => b.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Hot path: most recent value per biomarker for the snapshot.
        builder.HasIndex(b => new { b.UserId, b.BiomarkerName, b.CollectedAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("idx_lab_biomarkers_user_marker");
    }
}

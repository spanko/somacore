using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.UserDocuments;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class UserDocumentConfiguration : IEntityTypeConfiguration<UserDocument>
{
    public void Configure(EntityTypeBuilder<UserDocument> builder)
    {
        builder.ToTable("user_documents", t =>
        {
            t.HasCheckConstraint(
                "chk_user_documents_parse_status",
                "parse_status IN ('parsed', 'failed')");
            t.HasCheckConstraint(
                "chk_user_documents_file_size",
                "file_size > 0 AND file_size <= 10485760");
        });

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(d => d.UserId).IsRequired();
        builder.Property(d => d.FileName).HasMaxLength(255).IsRequired();
        builder.Property(d => d.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(d => d.FileBytes).IsRequired();
        builder.Property(d => d.FileSize).IsRequired();
        builder.Property(d => d.ParseStatus).IsRequired();
        builder.Property(d => d.ParseError).HasMaxLength(2000);
        builder.Property(d => d.Summary).HasMaxLength(300);
        builder.Property(d => d.UploadedAt).HasDefaultValueSql("now()");
        builder.Property(d => d.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(d => d.UpdatedAt).HasDefaultValueSql("now()");

        builder.HasOne(d => d.User)
            .WithMany()
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(d => new { d.UserId, d.UploadedAt })
            .IsDescending(false, true)
            .HasDatabaseName("idx_user_documents_user_uploaded");
    }
}

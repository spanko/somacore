using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.ExternalConnections;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class ExternalConnectionConfiguration : IEntityTypeConfiguration<ExternalConnection>
{
    public void Configure(EntityTypeBuilder<ExternalConnection> builder)
    {
        builder.ToTable("external_connections", t =>
        {
            t.HasCheckConstraint(
                "chk_external_connections_source",
                "source IN ('whoop', 'oura', 'strava', 'apple_health', 'manual')");
            t.HasCheckConstraint(
                "chk_external_connections_status",
                "status IN ('active', 'revoked', 'refresh_failed', 'pending_authorization')");
            t.HasCheckConstraint(
                "chk_external_connections_kv_secret_name_not_empty",
                "length(key_vault_secret_name) > 0");
        });

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(c => c.UserId)
            .IsRequired();

        builder.Property(c => c.Source)
            .IsRequired();

        builder.Property(c => c.Status)
            .IsRequired();

        builder.Property(c => c.Scopes)
            .HasColumnType("text[]")
            .HasDefaultValueSql("'{}'::text[]")
            .IsRequired();

        builder.Property(c => c.KeyVaultSecretName)
            .IsRequired();

        builder.Property(c => c.LastRefreshAt);
        builder.Property(c => c.NextRefreshAt);

        builder.Property(c => c.RefreshFailureCount)
            .HasDefaultValue(0);

        builder.Property(c => c.LastRefreshError);

        builder.Property(c => c.ConnectionMetadata)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();

        builder.Property(c => c.CreatedAt)
            .HasDefaultValueSql("now()");

        builder.Property(c => c.UpdatedAt)
            .HasDefaultValueSql("now()");

        builder.HasOne(c => c.User)
            .WithMany(u => u.ExternalConnections)
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // One ACTIVE connection per (user, source). Other statuses can coexist.
        builder.HasIndex(c => new { c.UserId, c.Source })
            .IsUnique()
            .HasFilter("status = 'active'")
            .HasDatabaseName("idx_external_connections_user_source_active");

        builder.HasIndex(c => c.NextRefreshAt)
            .HasFilter("status = 'active'")
            .HasDatabaseName("idx_external_connections_next_refresh");

        builder.HasIndex(c => c.UserId)
            .HasDatabaseName("idx_external_connections_user_id");
    }
}

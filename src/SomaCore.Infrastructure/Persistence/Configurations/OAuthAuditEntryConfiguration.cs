using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.OAuthAudit;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class OAuthAuditEntryConfiguration : IEntityTypeConfiguration<OAuthAuditEntry>
{
    public void Configure(EntityTypeBuilder<OAuthAuditEntry> builder)
    {
        builder.ToTable("oauth_audit", t =>
        {
            t.HasCheckConstraint(
                "chk_oauth_audit_source",
                "source IN ('whoop', 'oura', 'strava', 'apple_health')");
            t.HasCheckConstraint(
                "chk_oauth_audit_action",
                "action IN ('authorize', 'callback_success', 'callback_failed', 'token_refresh_success', 'token_refresh_failed', 'revoke_detected', 'manual_disconnect')");
        });

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(a => a.UserId);
        builder.Property(a => a.ExternalConnectionId);

        builder.Property(a => a.Source)
            .IsRequired();

        builder.Property(a => a.Action)
            .IsRequired();

        builder.Property(a => a.Success)
            .IsRequired();

        builder.Property(a => a.HttpStatusCode);
        builder.Property(a => a.ErrorMessage);

        builder.Property(a => a.Context)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();

        builder.Property(a => a.OccurredAt)
            .HasDefaultValueSql("now()");

        builder.HasOne(a => a.User)
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(a => a.ExternalConnection)
            .WithMany()
            .HasForeignKey(a => a.ExternalConnectionId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(a => new { a.UserId, a.OccurredAt })
            .IsDescending(false, true)
            .HasFilter("user_id IS NOT NULL")
            .HasDatabaseName("idx_oauth_audit_user_occurred");

        builder.HasIndex(a => new { a.ExternalConnectionId, a.OccurredAt })
            .IsDescending(false, true)
            .HasFilter("external_connection_id IS NOT NULL")
            .HasDatabaseName("idx_oauth_audit_connection_occurred");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.WebhookEvents;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("webhook_events", t =>
        {
            t.HasCheckConstraint(
                "chk_webhook_events_source",
                "source IN ('whoop', 'oura', 'strava')");
            t.HasCheckConstraint(
                "chk_webhook_events_status",
                "status IN ('received', 'processing', 'processed', 'failed', 'discarded')");
        });

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(e => e.Source)
            .IsRequired();

        builder.Property(e => e.SourceEventId)
            .IsRequired();

        builder.Property(e => e.SourceTraceId)
            .IsRequired();

        builder.Property(e => e.EventType)
            .IsRequired();

        builder.Property(e => e.UserId);
        builder.Property(e => e.ExternalConnectionId);

        builder.Property(e => e.Status)
            .IsRequired()
            .HasDefaultValue("received");

        builder.Property(e => e.ReceivedAt)
            .HasDefaultValueSql("now()");

        builder.Property(e => e.ProcessingStartedAt);
        builder.Property(e => e.ProcessedAt);

        builder.Property(e => e.ProcessingAttempts)
            .HasDefaultValue(0);

        builder.Property(e => e.LastError);

        builder.Property(e => e.RawBody)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(e => e.SignatureHeader)
            .IsRequired();

        builder.Property(e => e.SignatureTimestampHeader)
            .IsRequired();

        builder.Property(e => e.CreatedAt)
            .HasDefaultValueSql("now()");

        builder.Property(e => e.UpdatedAt)
            .HasDefaultValueSql("now()");

        builder.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(e => e.ExternalConnection)
            .WithMany()
            .HasForeignKey(e => e.ExternalConnectionId)
            .OnDelete(DeleteBehavior.SetNull);

        // Idempotency: WHOOP can deliver the same event twice.
        builder.HasIndex(e => new { e.Source, e.SourceEventId, e.SourceTraceId })
            .IsUnique()
            .HasDatabaseName("idx_webhook_events_dedupe");

        // Work queue path: next batch of unprocessed events.
        builder.HasIndex(e => e.ReceivedAt)
            .HasFilter("status IN ('received', 'processing')")
            .HasDatabaseName("idx_webhook_events_pending");

        // Audit lookup: events for this user, recent first.
        builder.HasIndex(e => new { e.UserId, e.ReceivedAt })
            .IsDescending(false, true)
            .HasFilter("user_id IS NOT NULL")
            .HasDatabaseName("idx_webhook_events_user_received");
    }
}

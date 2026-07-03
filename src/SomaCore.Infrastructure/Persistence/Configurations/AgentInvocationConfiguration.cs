using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.Agent;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class AgentInvocationConfiguration : IEntityTypeConfiguration<AgentInvocation>
{
    public void Configure(EntityTypeBuilder<AgentInvocation> builder)
    {
        builder.ToTable("agent_invocations");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(a => a.UserId)
            .IsRequired();

        // Default covers every pre-existing row: they were all daily cards.
        builder.Property(a => a.Kind)
            .IsRequired()
            .HasDefaultValue(AgentInvocationKinds.DailyCard);

        builder.Property(a => a.InputSnapshot)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(a => a.TodaysRead)
            .IsRequired();

        builder.Property(a => a.ActionsJson)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(a => a.ModelId)
            .IsRequired();

        builder.Property(a => a.CostEstimateUsd)
            .HasPrecision(10, 6);

        builder.Property(a => a.DurationMs)
            .IsRequired();

        builder.Property(a => a.ErrorMessage)
            .HasMaxLength(2000);

        builder.Property(a => a.TraceId)
            .HasMaxLength(128);

        builder.Property(a => a.CreatedAt)
            .HasDefaultValueSql("now()");

        builder.Property(a => a.UpdatedAt)
            .HasDefaultValueSql("now()");

        builder.HasOne(a => a.User)
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Hot-path query: this user's most recent agent cards (the latest
        // card renders on /me; older ones may surface on an /admin review).
        builder.HasIndex(a => new { a.UserId, a.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("idx_agent_invocations_user_created");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.Users;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(u => u.EntraOid)
            .IsRequired();

        builder.Property(u => u.EntraTenantId)
            .IsRequired();

        builder.Property(u => u.Email)
            .IsRequired();

        builder.Property(u => u.DisplayName);

        builder.Property(u => u.CreatedAt)
            .HasDefaultValueSql("now()");

        builder.Property(u => u.UpdatedAt)
            .HasDefaultValueSql("now()");

        builder.Property(u => u.LastSeenAt);

        // ADR 0012 per-user opt-in for the network-backed daily-card agent.
        // Default false at row creation; flipped via SQL or future admin
        // toggle. /me reads this to decide stub vs live for each user.
        builder.Property(u => u.AgentOptIn)
            .IsRequired()
            .HasDefaultValue(false);

        builder.HasIndex(u => u.EntraOid)
            .IsUnique();

        // Functional index on lower(email). EF Core has no fluent API for
        // expression-based index columns, so the migration is hand-edited
        // post-generation to wrap the column in lower(). Keeping the index
        // declared here so the model is the source of truth for "this index
        // exists"; the migration carries the lower() wrapping.
        builder.HasIndex(u => u.Email)
            .HasDatabaseName("idx_users_email");
    }
}

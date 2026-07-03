using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using SomaCore.Domain.FoodEntries;

namespace SomaCore.Infrastructure.Persistence.Configurations;

public sealed class FoodEntryConfiguration : IEntityTypeConfiguration<FoodEntry>
{
    public void Configure(EntityTypeBuilder<FoodEntry> builder)
    {
        // Table name is mfp_food_entries per the MFP session brief — this
        // build (quick-log) pulls the table forward; the MFP session adds
        // its sources. Rename to food_entries is noted as a cleanup for
        // that session's migration.
        builder.ToTable("mfp_food_entries", t =>
        {
            t.HasCheckConstraint(
                "chk_mfp_food_entries_meal_slot",
                "meal_slot IN ('breakfast', 'lunch', 'dinner', 'snack', 'other')");
            t.HasCheckConstraint(
                "chk_mfp_food_entries_source",
                "source IN ('manual', 'healthkit_ios', 'csv_upload')");
            t.HasCheckConstraint(
                "chk_mfp_food_entries_calories_range",
                "calories IS NULL OR (calories BETWEEN 0 AND 20000)");
        });

        builder.HasKey(f => f.Id);

        builder.Property(f => f.Id)
            .ValueGeneratedOnAdd()
            .HasValueGenerator<Guid7Generator>();

        builder.Property(f => f.UserId)
            .IsRequired();

        builder.Property(f => f.Source)
            .IsRequired();

        builder.Property(f => f.MealDate)
            .IsRequired();

        builder.Property(f => f.MealSlot)
            .IsRequired();

        builder.Property(f => f.Calories).HasPrecision(8, 2);
        builder.Property(f => f.ProteinG).HasPrecision(7, 2);
        builder.Property(f => f.CarbsG).HasPrecision(7, 2);
        builder.Property(f => f.FatG).HasPrecision(7, 2);
        builder.Property(f => f.FiberG).HasPrecision(7, 2);
        builder.Property(f => f.SugarG).HasPrecision(7, 2);
        builder.Property(f => f.SodiumMg).HasPrecision(9, 2);

        builder.Property(f => f.FoodItems)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(f => f.RawPayload)
            .HasColumnType("jsonb");

        builder.Property(f => f.IngestedVia)
            .IsRequired();

        builder.Property(f => f.IngestedAt)
            .HasDefaultValueSql("now()");

        builder.Property(f => f.CreatedAt)
            .HasDefaultValueSql("now()");

        builder.Property(f => f.UpdatedAt)
            .HasDefaultValueSql("now()");

        builder.HasOne(f => f.User)
            .WithMany()
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(f => f.ExternalConnection)
            .WithMany()
            .HasForeignKey(f => f.ExternalConnectionId)
            .OnDelete(DeleteBehavior.SetNull);

        // One rollup row per (user, day, slot, source). HealthKit and manual
        // can coexist for the same slot; within a source, repeat logs merge
        // into the existing row (see QuickLogEntryService).
        builder.HasIndex(f => new { f.UserId, f.MealDate, f.MealSlot, f.Source })
            .IsUnique()
            .HasDatabaseName("idx_mfp_food_entries_user_date_slot_source");

        builder.HasIndex(f => new { f.UserId, f.MealDate })
            .IsDescending(false, true)
            .HasDatabaseName("idx_mfp_food_entries_user_date");
    }
}

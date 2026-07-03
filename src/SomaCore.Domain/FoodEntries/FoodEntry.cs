using System.Text.Json;

using SomaCore.Domain.Common;
using SomaCore.Domain.ExternalConnections;
using SomaCore.Domain.Users;

namespace SomaCore.Domain.FoodEntries;

/// <summary>
/// One meal-slot's nutrition for one user-day. Table name is
/// <c>mfp_food_entries</c> per the MFP session brief — the quick-log build
/// (session-quick-log.md) pulls the table forward with <c>source='manual'</c>
/// so the MyFitnessPal integration lands into an already-live schema with a
/// different source value. A row is a per-slot rollup, not a per-food-item
/// record: logging a second food into an existing slot merges into the row.
/// </summary>
public class FoodEntry : IHasTimestamps
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// Null for manual/CSV entries; populated when an aggregator-style
    /// connection produces the row. SET NULL on connection delete, same
    /// contract as <see cref="SomaCore.Domain.WhoopRecoveries.WhoopRecovery"/>.
    /// </summary>
    public Guid? ExternalConnectionId { get; set; }

    /// <summary>'manual' / 'healthkit_ios' / 'csv_upload'.</summary>
    public string Source { get; set; } = string.Empty;

    public DateOnly MealDate { get; set; }

    /// <summary>'breakfast' / 'lunch' / 'dinner' / 'snack' / 'other'.</summary>
    public string MealSlot { get; set; } = string.Empty;

    /// <summary>Best-effort timestamp; null when the source is a slot-level rollup.</summary>
    public DateTimeOffset? LoggedAt { get; set; }

    public decimal? Calories { get; set; }

    public decimal? ProteinG { get; set; }

    public decimal? CarbsG { get; set; }

    public decimal? FatG { get; set; }

    public decimal? FiberG { get; set; }

    public decimal? SugarG { get; set; }

    public decimal? SodiumMg { get; set; }

    /// <summary>
    /// Array of {name, amount, unit} objects. Never forwarded to Anthropic —
    /// food names stay server-side per privacy draft Part 4 / the MFP
    /// session's Section D.2 commitment.
    /// </summary>
    public JsonDocument FoodItems { get; set; } = null!;

    /// <summary>The original input: the user's typed line (manual), the HK sample batch, or the CSV row.</summary>
    public JsonDocument? RawPayload { get; set; }

    /// <summary>'quick_log' / 'ios_observer' / 'ios_reconciliation' / 'csv_upload'.</summary>
    public string IngestedVia { get; set; } = string.Empty;

    public DateTimeOffset IngestedAt { get; set; }

    public string? TraceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User? User { get; set; }

    public ExternalConnection? ExternalConnection { get; set; }
}

/// <summary>Allowed values for <see cref="FoodEntry.MealSlot"/>.</summary>
public static class MealSlot
{
    public const string Breakfast = "breakfast";
    public const string Lunch = "lunch";
    public const string Dinner = "dinner";
    public const string Snack = "snack";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All =
        new[] { Breakfast, Lunch, Dinner, Snack, Other };
}

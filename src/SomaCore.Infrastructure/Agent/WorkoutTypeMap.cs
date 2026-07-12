namespace SomaCore.Infrastructure.Agent;

/// <summary>
/// Maps the three sources' activity vocabularies (WHOOP sport names, Strava
/// sport types, HealthKit HKWorkoutActivityType names, quick-log free text)
/// onto shared type families for the snapshot dedup rule (Strava brief §1.8):
/// two captures of the same physical workout merge only when their families
/// match. Unmapped types fall through to "other" — an over-broad family
/// would merge distinct workouts, which is worse than failing to merge.
/// </summary>
public static class WorkoutTypeMap
{
    public const string Other = "other";

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // running
        ["run"] = "running",
        ["running"] = "running",
        ["trailrun"] = "running",
        ["virtualrun"] = "running",
        // cycling
        ["ride"] = "cycling",
        ["cycling"] = "cycling",
        ["virtualride"] = "cycling",
        ["mountainbikeride"] = "cycling",
        ["gravelride"] = "cycling",
        ["ebikeride"] = "cycling",
        ["spinning"] = "cycling",
        // swimming
        ["swim"] = "swimming",
        ["swimming"] = "swimming",
        // strength
        ["weighttraining"] = "strength",
        ["weightlifting"] = "strength",
        ["strength"] = "strength",
        ["strengthtraining"] = "strength",
        ["traditionalstrengthtraining"] = "strength",
        ["functionalstrengthtraining"] = "strength",
        // walking
        ["walk"] = "walking",
        ["walking"] = "walking",
        // hiking
        ["hike"] = "hiking",
        ["hiking"] = "hiking",
        // rowing
        ["rowing"] = "rowing",
        ["virtualrow"] = "rowing",
        // yoga
        ["yoga"] = "yoga",
    };

    /// <summary>
    /// Resolve the family for any source's activity type. Handles HealthKit's
    /// "HKWorkoutActivityType" prefix; everything else matches case-insensitively.
    /// </summary>
    public static string FamilyOf(string? activityType)
    {
        if (string.IsNullOrWhiteSpace(activityType))
        {
            return Other;
        }

        var key = activityType.Trim();
        const string hkPrefix = "HKWorkoutActivityType";
        if (key.StartsWith(hkPrefix, StringComparison.OrdinalIgnoreCase))
        {
            key = key[hkPrefix.Length..];
        }

        return Map.TryGetValue(key, out var family) ? family : Other;
    }
}

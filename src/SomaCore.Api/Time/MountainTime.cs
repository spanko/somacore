namespace SomaCore.Api.Time;

/// <summary>
/// Formats <see cref="DateTimeOffset"/> values in Mountain time (America/Denver),
/// with an MST/MDT suffix chosen from <see cref="TimeZoneInfo.IsDaylightSavingTime(DateTime)"/>.
/// Phase-1 trade-off: single timezone for all users. If a user is in a different
/// zone they'll see Mountain time, not local. Revisit with a per-user time_zone
/// column or client-side rendering when we have users outside MT.
/// </summary>
public static class MountainTime
{
    private static readonly TimeZoneInfo Tz =
        TimeZoneInfo.FindSystemTimeZoneById("America/Denver");

    public static string Format(DateTimeOffset? value)
        => value is null ? "—" : Format(value.Value);

    public static string Format(DateTimeOffset value)
    {
        var local = TimeZoneInfo.ConvertTime(value, Tz);
        var abbreviation = Tz.IsDaylightSavingTime(local) ? "MDT" : "MST";
        return local.ToString("yyyy-MM-dd HH:mm") + " " + abbreviation;
    }

    public static string FormatDateOnly(DateTimeOffset value)
    {
        var local = TimeZoneInfo.ConvertTime(value, Tz);
        return local.ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// Short clock-time form, e.g. "10:30 PM". No date, no zone suffix —
    /// used where surrounding context (a date column on the same row) makes
    /// those redundant and noisy. Hour is single-digit when applicable
    /// ("9:05 AM" not "09:05 AM").
    /// </summary>
    public static string FormatTimeShort(DateTimeOffset value)
    {
        var local = TimeZoneInfo.ConvertTime(value, Tz);
        return local.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string FormatTimeShort(DateTimeOffset? value)
        => value is null ? "—" : FormatTimeShort(value.Value);
}

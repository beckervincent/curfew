using System.Globalization;

namespace Curfew.Core;

/// <summary>Pure, side-effect-free time + duration helpers shared by App, Overlay, Service. No Windows API calls, no mutable state, so every method is thread-safe.</summary>
public static class TimeMath
{
    /// <summary>Seconds in one minute.</summary>
    private const int SecondsPerMinute = 60;

    /// <summary>Seconds in one hour.</summary>
    private const int SecondsPerHour = 60 * SecondsPerMinute;

    /// <summary>Rendered when <see cref="FormatDuration"/> given a negative value.</summary>
    private const string DurationPlaceholder = "--";

    /// <summary>Rendered when <see cref="FormatCompact"/> given a negative value.</summary>
    private const string CompactPlaceholder = "--:--";

    /// <summary>Map a date to a Monday-based weekday index, Monday = 0 … Saturday = 5, Sunday = 6.</summary>
    /// <param name="date">Date to evaluate.</param>
    /// <returns>Integer in inclusive range <c>0</c>–<c>6</c>.</returns>
    /// <remarks>
    /// .NET <see cref="DayOfWeek"/> is Sunday-based (Sunday = 0, Saturday = 6). Shifts it so the week starts Monday, the daily-limit schedule convention.
    /// </remarks>
    public static int MondayBasedWeekday(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Sunday => 6,
        var day => (int)day - 1,
    };

    /// <summary>Format a duration as a space-separated string like <c>"1h 30m 45s"</c>, <c>"30m 5s"</c> or <c>"5s"</c>. Leading zero units omitted, so most significant non-zero unit appears first.</summary>
    /// <param name="seconds">Duration in seconds.</param>
    /// <returns>Formatted duration, or <c>"--"</c> when <paramref name="seconds"/> negative (UI "unknown/unavailable" placeholder).</returns>
    public static string FormatDuration(int seconds)
    {
        if (seconds < 0) return DurationPlaceholder;

        var (hours, minutes, secs) = SplitHms(seconds);

        if (hours > 0) return $"{hours}h {minutes}m {secs}s";
        if (minutes > 0) return $"{minutes}m {secs}s";
        return $"{secs}s";
    }

    /// <summary>Format a duration as a compact clock string like <c>"1:30:45"</c> (with hours) or <c>"30:45"</c> (without). Minutes + seconds zero-padded to two digits; leading hours/minutes field not padded.</summary>
    /// <param name="seconds">Duration in seconds.</param>
    /// <returns>Formatted duration, or <c>"--:--"</c> when <paramref name="seconds"/> negative (UI "unknown/unavailable" placeholder).</returns>
    public static string FormatCompact(int seconds)
    {
        if (seconds < 0) return CompactPlaceholder;

        var (hours, minutes, secs) = SplitHms(seconds);

        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{secs:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{secs:00}");
    }

    /// <summary>Split a non-negative second count into whole hours, remaining minutes (0–59), remaining seconds (0–59). Hours may exceed 23 for multi-day durations.</summary>
    private static (int Hours, int Minutes, int Seconds) SplitHms(int seconds) =>
        (seconds / SecondsPerHour,
         seconds % SecondsPerHour / SecondsPerMinute,
         seconds % SecondsPerMinute);
}

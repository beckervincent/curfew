using System.Globalization;

namespace Curfew.Core;

/// <summary>
/// Pure, side-effect-free time and duration helpers shared by the App, Overlay
/// and Service. Contains no Windows API calls and no mutable state, so every
/// method is safe to call from any thread.
/// </summary>
public static class TimeMath
{
    /// <summary>Number of seconds in one minute.</summary>
    private const int SecondsPerMinute = 60;

    /// <summary>Number of seconds in one hour.</summary>
    private const int SecondsPerHour = 60 * SecondsPerMinute;

    /// <summary>Rendered when <see cref="FormatDuration"/> is given a negative value.</summary>
    private const string DurationPlaceholder = "--";

    /// <summary>Rendered when <see cref="FormatCompact"/> is given a negative value.</summary>
    private const string CompactPlaceholder = "--:--";

    /// <summary>
    /// Maps a calendar date to a Monday-based weekday index, where
    /// Monday = 0, Tuesday = 1, … Saturday = 5, Sunday = 6.
    /// </summary>
    /// <param name="date">The date to evaluate.</param>
    /// <returns>An integer in the inclusive range <c>0</c>–<c>6</c>.</returns>
    /// <remarks>
    /// The .NET <see cref="DayOfWeek"/> enum is Sunday-based (Sunday = 0,
    /// Saturday = 6). This helper shifts it so the working week starts on Monday,
    /// which is the convention used by the daily-limit schedule.
    /// </remarks>
    public static int MondayBasedWeekday(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Sunday => 6,
        var day => (int)day - 1,
    };

    /// <summary>
    /// Formats a duration as a human-readable, space-separated string such as
    /// <c>"1h 30m 45s"</c>, <c>"30m 5s"</c> or <c>"5s"</c>. Leading zero units are
    /// omitted, so the most significant non-zero unit appears first.
    /// </summary>
    /// <param name="seconds">The duration in seconds.</param>
    /// <returns>
    /// The formatted duration, or <c>"--"</c> when <paramref name="seconds"/> is
    /// negative (used as an "unknown / unavailable" placeholder by the UI).
    /// </returns>
    public static string FormatDuration(int seconds)
    {
        if (seconds < 0) return DurationPlaceholder;

        var (hours, minutes, secs) = SplitHms(seconds);

        if (hours > 0) return $"{hours}h {minutes}m {secs}s";
        if (minutes > 0) return $"{minutes}m {secs}s";
        return $"{secs}s";
    }

    /// <summary>
    /// Formats a duration as a compact clock-style string such as
    /// <c>"1:30:45"</c> (with hours) or <c>"30:45"</c> (without). Minutes and
    /// seconds are zero-padded to two digits; the leading hours/minutes field is
    /// not padded.
    /// </summary>
    /// <param name="seconds">The duration in seconds.</param>
    /// <returns>
    /// The formatted duration, or <c>"--:--"</c> when <paramref name="seconds"/>
    /// is negative (used as an "unknown / unavailable" placeholder by the UI).
    /// </returns>
    public static string FormatCompact(int seconds)
    {
        if (seconds < 0) return CompactPlaceholder;

        var (hours, minutes, secs) = SplitHms(seconds);

        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{secs:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{secs:00}");
    }

    /// <summary>
    /// Splits a non-negative second count into whole hours, the remaining
    /// minutes (0–59) and the remaining seconds (0–59). Hours may exceed 23 for
    /// multi-day durations.
    /// </summary>
    private static (int Hours, int Minutes, int Seconds) SplitHms(int seconds) =>
        (seconds / SecondsPerHour,
         seconds % SecondsPerHour / SecondsPerMinute,
         seconds % SecondsPerMinute);
}

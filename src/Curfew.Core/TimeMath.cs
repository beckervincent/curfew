namespace Curfew.Core;

/// <summary>Pure time/formatting helpers, independent of any Windows API.</summary>
public static class TimeMath
{
    /// <summary>Weekday with Monday = 0 … Sunday = 6.</summary>
    public static int MondayBasedWeekday(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Sunday => 6,
        var d => (int)d - 1,
    };

    /// <summary>"1h 30m 45s", "30m 5s", "5s", or "--" for negative input.</summary>
    public static string FormatDuration(int seconds)
    {
        if (seconds < 0) return "--";
        int h = seconds / 3600, m = seconds % 3600 / 60, s = seconds % 60;
        if (h > 0) return $"{h}h {m}m {s}s";
        if (m > 0) return $"{m}m {s}s";
        return $"{s}s";
    }

    /// <summary>"1:30:45" or "30:45"; "--:--" for negative input.</summary>
    public static string FormatCompact(int seconds)
    {
        if (seconds < 0) return "--:--";
        int h = seconds / 3600, m = seconds % 3600 / 60, s = seconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }
}

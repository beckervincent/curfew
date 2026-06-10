using Curfew.Core;

namespace Curfew.Overlay;

/// <summary>Shared mutable state between the mini overlay and the lock screen
/// (one process, one thread).</summary>
internal static class OverlayState
{
    public static SettingsStore Settings = null!;
    public static int Remaining;
    public static IntPtr MiniHwnd;
    public static bool Locked;

    // Parent's enforcement choices.
    public static bool LimitEnabled = true;       // daily hours budget
    public static bool ScheduleEnabled;           // weekly allowed-time grid
    public static Schedule Schedule = Schedule.AllAllowed();

    /// <summary>Set when the parent unlocks during a blocked schedule window;
    /// cleared once an allowed window is reached, so the next blocked window
    /// re-locks.</summary>
    public static bool ScheduleOverride;

    public static void LoadEnforcement()
    {
        LimitEnabled = Settings.GetBool("limit_enabled", true);
        ScheduleEnabled = Settings.GetBool("schedule_enabled", false);
        Schedule = Schedule.Parse(Settings.Get("schedule"));
    }

    /// <summary>True when usage is currently allowed by the schedule.</summary>
    public static bool ScheduleAllows()
    {
        if (!ScheduleEnabled) return true;
        var now = DateTime.Now;
        var weekday = TimeMath.MondayBasedWeekday(DateOnly.FromDateTime(now));
        return Schedule.IsAllowed(weekday, now.Hour * 60 + now.Minute);
    }

    /// <summary>Reasons the screen should be blocked right now.</summary>
    public static bool BudgetBlocked => LimitEnabled && Remaining <= 0;
    public static bool ScheduleBlocked => ScheduleEnabled && !ScheduleAllows() && !ScheduleOverride;
    public static bool ShouldBlock => BudgetBlocked || ScheduleBlocked;

    public static void Persist() =>
        Settings.Set($"remaining_time_{DateTime.Now:yyyy-MM-dd}", Remaining.ToString());
}

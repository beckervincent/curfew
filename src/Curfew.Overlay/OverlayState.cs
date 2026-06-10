using System.Globalization;
using Curfew.Core;

namespace Curfew.Overlay;

/// <summary>
/// Shared mutable state for the overlay process, read and written by both the
/// mini countdown window (<see cref="OverlayApp"/>) and the full-screen lock
/// screen (<see cref="LockScreen"/>).
/// </summary>
/// <remarks>
/// <para>
/// The whole overlay runs as a single Win32 process on a single UI thread: the
/// message loop in <see cref="OverlayApp.Run"/> drives every timer tick, paint
/// and command on that thread, and the low-level keyboard hook is delivered to
/// it as well. Because there is no second thread mutating this state, plain
/// static fields are sufficient and no locking is required. Keep it that way —
/// if work is ever moved off the UI thread this class must be revisited.
/// </para>
/// <para>
/// These members are deliberately public mutable fields rather than properties:
/// the two windows assign to <see cref="Remaining"/>, <see cref="Locked"/> and
/// <see cref="ScheduleOverride"/> directly, and changing their shape would break
/// those call sites.
/// </para>
/// </remarks>
internal static class OverlayState
{
    // ---- Settings keys -----------------------------------------------------
    // Centralised so the literals can never drift between read and write. These
    // strings are a cross-process contract with the App and Service, which write
    // the same keys into the shared SQLite store; do not rename them.

    /// <summary>Key for the "daily hours budget on/off" flag.</summary>
    private const string KeyLimitEnabled = "limit_enabled";

    /// <summary>Key for the "weekly allowed-time schedule on/off" flag.</summary>
    private const string KeyScheduleEnabled = "schedule_enabled";

    /// <summary>Key for the serialised weekly schedule grid.</summary>
    private const string KeySchedule = "schedule";

    /// <summary>Prefix for the per-day persisted remaining-time rows.</summary>
    private const string RemainingPrefix = "remaining_time_";

    // ---- Process-wide handles and live counters ----------------------------

    /// <summary>The shared settings store, opened once in <see cref="OverlayApp.Run"/>.</summary>
    public static SettingsStore Settings = null!;

    /// <summary>Seconds of daily budget remaining; counts down once per second.</summary>
    public static int Remaining;

    /// <summary>Window handle of the mini countdown overlay, or <see cref="IntPtr.Zero"/> before it is created.</summary>
    public static IntPtr MiniHwnd;

    /// <summary>True while the full-screen lock is up. The budget is frozen and timers paused in this state.</summary>
    public static bool Locked;

    // ---- Parent's enforcement choices --------------------------------------

    /// <summary>Whether the daily hours budget is enforced.</summary>
    public static bool LimitEnabled = true;

    /// <summary>Whether the weekly allowed-time grid is enforced.</summary>
    public static bool ScheduleEnabled;

    /// <summary>The weekly allowed-time grid; defaults to fully allowed.</summary>
    public static Schedule Schedule = Schedule.AllAllowed();

    /// <summary>
    /// Set when the parent unlocks during a blocked schedule window; cleared once
    /// an allowed window is reached, so the next blocked window re-locks.
    /// </summary>
    public static bool ScheduleOverride;

    /// <summary>
    /// (Re)loads the parent's enforcement choices from the settings store. Each
    /// accessor falls back to a safe default when its key is missing or malformed,
    /// so a partially written database never throws here.
    /// </summary>
    public static void LoadEnforcement()
    {
        LimitEnabled = Settings.GetBool(KeyLimitEnabled, true);
        ScheduleEnabled = Settings.GetBool(KeyScheduleEnabled, false);
        Schedule = Schedule.Parse(Settings.Get(KeySchedule));
    }

    /// <summary>
    /// Whether usage is currently allowed by the schedule. Always true when the
    /// schedule is disabled, so the budget is the only gate in that mode.
    /// </summary>
    public static bool ScheduleAllows()
    {
        if (!ScheduleEnabled) return true;

        var now = DateTime.Now;
        var weekday = TimeMath.MondayBasedWeekday(DateOnly.FromDateTime(now));
        return Schedule.IsAllowed(weekday, now.Hour * 60 + now.Minute);
    }

    // ---- Blocking policy ---------------------------------------------------
    // Two independent reasons to lock the screen. Either one is sufficient; the
    // lock screen reads BudgetBlocked to pick its title, and the tick loop reads
    // ShouldBlock to decide when to raise the lock.

    /// <summary>True when the daily budget is enabled and exhausted.</summary>
    public static bool BudgetBlocked => LimitEnabled && Remaining <= 0;

    /// <summary>True when the schedule is enabled, the current slot is blocked, and the parent has not overridden it.</summary>
    public static bool ScheduleBlocked => ScheduleEnabled && !ScheduleAllows() && !ScheduleOverride;

    /// <summary>True when any enforcement reason currently requires the lock screen.</summary>
    public static bool ShouldBlock => BudgetBlocked || ScheduleBlocked;

    /// <summary>
    /// Persists today's remaining budget so a restart (e.g. watchdog respawn)
    /// resumes from the correct value instead of granting a fresh allowance.
    /// </summary>
    public static void Persist() =>
        Settings.Set(RemainingKey(DateOnly.FromDateTime(DateTime.Now)), Remaining.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The settings key under which today's remaining budget is stored. Must match
    /// the key the App and <see cref="OverlayApp"/> use to read it back, and the
    /// per-day prefix the settings store purges on each open.
    /// </summary>
    private static string RemainingKey(DateOnly date) =>
        RemainingPrefix + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

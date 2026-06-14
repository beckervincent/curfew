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

    /// <summary>The current session user's SID; scopes per-user config + counters.</summary>
    public static string CurrentSid = string.Empty;

    /// <summary>
    /// Whether this user already had recorded usage at startup. Grandfathers users who
    /// were on the device before the new-user setup gate existed, so they are never
    /// shown the setup lock. Set once at init from <see cref="SettingsStore.HasUsageHistory"/>.
    /// </summary>
    public static bool UserHasHistory;

    /// <summary>Seconds of daily budget remaining; counts down once per second.</summary>
    public static int Remaining;

    /// <summary>Window handle of the mini countdown overlay, or <see cref="IntPtr.Zero"/> before it is created.</summary>
    public static IntPtr MiniHwnd;

    /// <summary>True while the full-screen lock is up. The budget is frozen and timers paused in this state.</summary>
    public static bool Locked;

    /// <summary>
    /// Unix-seconds timestamp until which the budget countdown is paused (a
    /// parent-granted break). In-memory only, so it resets on the next restart.
    /// </summary>
    public static long PausedUntilUnix;

    /// <summary>True while a parent-granted pause is in effect; the budget is frozen.</summary>
    public static bool IsPaused => DateTimeOffset.UtcNow.ToUnixTimeSeconds() < PausedUntilUnix;

    // ---- Parent's enforcement choices --------------------------------------

    /// <summary>Whether the daily hours budget is enforced.</summary>
    public static bool LimitEnabled = true;

    /// <summary>Whether the weekly allowed-time grid is enforced.</summary>
    public static bool ScheduleEnabled;

    /// <summary>The weekly allowed-time grid; defaults to fully allowed.</summary>
    public static Schedule Schedule = Schedule.AllAllowed();

    /// <summary>
    /// Process names whose foreground time does not consume the daily budget
    /// (cat-3 app allow-list). Loaded from config in <see cref="LoadEnforcement"/>.
    /// </summary>
    public static IReadOnlySet<string> AllowedApps = new HashSet<string>();

    /// <summary>
    /// Set when the parent unlocks during a blocked schedule window; cleared once
    /// an allowed window is reached, so the next blocked window re-locks.
    /// </summary>
    public static bool ScheduleOverride;

    /// <summary>
    /// Set when the parent chooses to ignore the weekly schedule for the rest of
    /// the session. Unlike <see cref="ScheduleOverride"/> it is never cleared when
    /// an allowed window arrives, so later blocked windows do not re-lock. Being a
    /// plain in-memory field it resets to <c>false</c> on the next overlay restart
    /// (reboot / logon) — exactly the "until next restart" lifetime intended.
    /// </summary>
    public static bool IgnoreScheduleUntilRestart;

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
        AllowedApps = AppAllowlist.Parse(Settings.Get("app_allowlist"));
        ApplyLimitChangeToRemaining();
    }

    private static int _lastLimitMinutes;
    private static DateOnly _lastLimitDate;
    private static bool _limitTracked;

    /// <summary>
    /// When the administrator changes today's daily limit while a session is live,
    /// shift the remaining budget by the same delta (e.g. limit 2:00 -> 2:30 with
    /// 1:30 left bumps remaining to 2:00). A day rollover instead re-seeds the budget
    /// for the new day so a long-lived overlay (the logon process is not restarted at
    /// midnight on a machine left on overnight) grants the fresh allowance rather than
    /// carrying yesterday's depleted — or leftover — Remaining across the boundary.
    /// Runs on each enforcement reload, so a change lands within a reload cycle.
    /// </summary>
    private static void ApplyLimitChangeToRemaining()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var weekday = TimeMath.MondayBasedWeekday(today);
        var limitMinutes = Settings.GetDailyLimit(weekday);

        if (_limitTracked && today != _lastLimitDate)
        {
            // Day rollover. Re-seed exactly as startup does (Program.cs): honour any
            // value already persisted for the new day, otherwise grant a fresh
            // allowance from the new day's limit. Mirrors RecordActiveSecond, which
            // resets its usage counter at the same boundary.
            var saved = int.TryParse(Settings.Get(RemainingKey(today)), out var s) ? (int?)s : null;
            Remaining = TimeKeeper.InitialRemaining(saved, limitMinutes);
            Persist();
        }
        else if (_limitTracked && limitMinutes != _lastLimitMinutes)
        {
            Remaining = Math.Max(0, Remaining + (limitMinutes - _lastLimitMinutes) * 60);
            Persist();
        }

        _lastLimitMinutes = limitMinutes;
        _lastLimitDate = today;
        _limitTracked = true;
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

    /// <summary>True when the schedule is enabled, the current slot is blocked, and the parent has neither overridden it nor ignored it for the session.</summary>
    public static bool ScheduleBlocked =>
        ScheduleEnabled && !ScheduleAllows() && !ScheduleOverride && !IgnoreScheduleUntilRestart;

    /// <summary>
    /// True when this Windows user has not been set up yet. In force once the device
    /// has a parent passcode (so a genuine first run still reaches the setup wizard);
    /// a user whose SID is not in <c>provisioned_users</c> is blocked at the new-user
    /// setup lock until the parent enters the PIN and sets their daily limit.
    /// </summary>
    public static bool NewUserBlocked =>
        !string.IsNullOrEmpty(Settings.Get("passcode"))
        && !UserHasHistory
        && !UserProvisioning.IsProvisioned(Settings.Get("provisioned_users"), CurrentSid);

    /// <summary>True when any enforcement reason currently requires the lock screen.</summary>
    public static bool ShouldBlock => BudgetBlocked || ScheduleBlocked || NewUserBlocked;

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
        $"{RemainingPrefix}{CurrentSid}_{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    // ---- Usage history -----------------------------------------------------
    // Records active (unlocked) screen time per day so Settings can chart it.

    private static int _usedSeconds;
    private static DateOnly _usageDate;

    /// <summary>Loads today's accumulated usage so a respawn continues the count.</summary>
    public static void LoadUsage()
    {
        _usageDate = DateOnly.FromDateTime(DateTime.Now);
        _usedSeconds = int.TryParse(Settings.Get(UsageKey(_usageDate)), out var seconds) ? seconds : 0;
    }

    /// <summary>
    /// Counts one second of active screen use. Rolls over cleanly at midnight by
    /// flushing the finished day and resetting the counter, and persists about
    /// twice a minute to bound writes.
    /// </summary>
    public static void RecordActiveSecond()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today != _usageDate)
        {
            PersistUsage();
            _usageDate = today;
            _usedSeconds = 0;
        }

        _usedSeconds++;
        if (_usedSeconds % 30 == 0) PersistUsage();
    }

    /// <summary>Writes the running usage total for the current day.</summary>
    public static void PersistUsage() =>
        Settings.Set(UsageKey(_usageDate), _usedSeconds.ToString(CultureInfo.InvariantCulture));

    private static string UsageKey(DateOnly date) =>
        $"{SettingsStore.UsagePrefix}{CurrentSid}_{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}

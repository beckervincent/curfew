using System.Globalization;
using Curfew.Core;

namespace Curfew.Overlay;

/// <summary>shared mutable overlay state, read/written by mini countdown (<see cref="OverlayApp"/>) + lock screen (<see cref="LockScreen"/>)</summary>
/// <remarks>
/// <para>single Win32 process, single UI thread: <see cref="OverlayApp.Run"/> loop drives every tick/paint/command + keyboard hook. no second thread mutates -> plain static fields, no locking. keep it that way; revisit if work moves off UI thread</para>
/// <para>public mutable fields not properties: two windows assign <see cref="Remaining"/>, <see cref="Locked"/>, <see cref="ScheduleOverride"/> directly; reshaping breaks call sites</para>
/// </remarks>
internal static class OverlayState
{
    // ---- Settings keys -----------------------------------------------------
    // centralised so literals can't drift read vs write. cross-process contract w/ App + Service (same keys in shared SQLite store); do not rename

    /// <summary>key: daily hours budget on/off flag</summary>
    private const string KeyLimitEnabled = "limit_enabled";

    /// <summary>key: weekly allowed-time schedule on/off flag</summary>
    private const string KeyScheduleEnabled = "schedule_enabled";

    /// <summary>key: serialised weekly schedule grid</summary>
    private const string KeySchedule = "schedule";

    /// <summary>prefix: per-day persisted remaining-time rows</summary>
    private const string RemainingPrefix = "remaining_time_";

    // ---- Process-wide handles and live counters ----------------------------

    /// <summary>shared settings store, opened once in <see cref="OverlayApp.Run"/></summary>
    public static SettingsStore Settings = null!;

    /// <summary>current session user SID; scopes per-user config + counters</summary>
    public static string CurrentSid = string.Empty;

    /// <summary>had recorded usage at startup. grandfathers users predating new-user setup gate so never shown setup lock. set once at init from <see cref="SettingsStore.HasUsageHistory"/></summary>
    public static bool UserHasHistory;

    /// <summary>seconds of daily budget remaining; counts down once per second</summary>
    public static int Remaining;

    /// <summary>mini countdown overlay window handle, or <see cref="IntPtr.Zero"/> before created</summary>
    public static IntPtr MiniHwnd;

    /// <summary>full-screen lock up; budget frozen + timers paused</summary>
    public static bool Locked;

    /// <summary>unix-seconds until budget countdown paused (parent-granted break). in-memory only, resets on restart</summary>
    public static long PausedUntilUnix;

    /// <summary>parent-granted pause in effect; budget frozen</summary>
    public static bool IsPaused => DateTimeOffset.UtcNow.ToUnixTimeSeconds() < PausedUntilUnix;

    // ---- Parent's enforcement choices --------------------------------------

    /// <summary>daily hours budget enforced</summary>
    public static bool LimitEnabled = true;

    /// <summary>weekly allowed-time grid enforced</summary>
    public static bool ScheduleEnabled;

    /// <summary>weekly allowed-time grid; defaults fully allowed</summary>
    public static Schedule Schedule = Schedule.AllAllowed();

    /// <summary>process names whose foreground time doesn't consume budget (cat-3 app allow-list). loaded in <see cref="LoadEnforcement"/></summary>
    public static IReadOnlySet<string> AllowedApps = new HashSet<string>();

    /// <summary>parent unlocked during blocked schedule window; cleared once allowed window reached so next blocked window re-locks</summary>
    public static bool ScheduleOverride;

    /// <summary>parent ignores weekly schedule rest of session. unlike <see cref="ScheduleOverride"/> never cleared on allowed window, so later blocked windows don't re-lock. in-memory -> resets <c>false</c> on next restart (reboot/logon) = "until next restart" lifetime</summary>
    public static bool IgnoreScheduleUntilRestart;

    /// <summary>(re)load parent enforcement choices from store; each accessor falls back to safe default on missing/malformed key so partial DB never throws</summary>
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

    /// <summary>admin changes today's limit mid-session -> shift Remaining by same delta (2:00->2:30 w/ 1:30 left -> 2:00). day rollover instead re-seeds new day so long-lived overlay (not restarted at midnight) grants fresh allowance, not yesterday's leftover. runs each enforcement reload</summary>
    private static void ApplyLimitChangeToRemaining()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var weekday = TimeMath.MondayBasedWeekday(today);
        var limitMinutes = Settings.GetDailyLimit(weekday);

        if (_limitTracked && today != _lastLimitDate)
        {
            // day rollover. re-seed as startup (Program.cs): honour persisted value for new day, else fresh allowance from new day's limit. mirrors RecordActiveSecond reset at same boundary
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

    /// <summary>usage allowed by schedule now. always true when schedule disabled -> budget is only gate</summary>
    public static bool ScheduleAllows()
    {
        if (!ScheduleEnabled) return true;

        var now = DateTime.Now;
        var weekday = TimeMath.MondayBasedWeekday(DateOnly.FromDateTime(now));
        return Schedule.IsAllowed(weekday, now.Hour * 60 + now.Minute);
    }

    // ---- Blocking policy ---------------------------------------------------
    // two independent lock reasons; either suffices. lock screen reads BudgetBlocked for title, tick loop reads ShouldBlock to raise lock

    /// <summary>daily budget enabled + exhausted</summary>
    public static bool BudgetBlocked => LimitEnabled && Remaining <= 0;

    /// <summary>schedule enabled, current slot blocked, parent neither overrode nor ignored for session</summary>
    public static bool ScheduleBlocked =>
        ScheduleEnabled && !ScheduleAllows() && !ScheduleOverride && !IgnoreScheduleUntilRestart;

    /// <summary>Windows user not set up yet. in force once device has parent passcode (genuine first run still reaches setup wizard); SID not in <c>provisioned_users</c> blocked at new-user setup lock until parent enters PIN + sets daily limit</summary>
    public static bool NewUserBlocked =>
        !string.IsNullOrEmpty(CurrentSid)
        && !string.IsNullOrEmpty(Settings.Get("passcode"))
        && !UserHasHistory
        && !UserProvisioning.IsProvisioned(Settings.Get("provisioned_users"), CurrentSid);

    /// <summary>any enforcement reason requires lock screen now</summary>
    public static bool ShouldBlock => BudgetBlocked || ScheduleBlocked || NewUserBlocked;

    /// <summary>genuinely new user the gate must still hold: no usage history at startup, not yet in
    /// provisioned_users. unlike <see cref="NewUserBlocked"/> does NOT depend on passcode, so stays true
    /// even on a boot where the gate failed to engage (config.db unreadable -> passcode empty -> no lock).
    /// while true the overlay must NOT persist a used_time row: <see cref="SettingsStore.HasUsageHistory"/>
    /// reads any such row as pre-gate history and permanently grandfathers the user out of setup.
    /// suppressing it keeps each boot able to re-raise setup until the parent provisions the user</summary>
    public static bool PendingNewUser =>
        !string.IsNullOrEmpty(CurrentSid)
        && !UserHasHistory
        && !UserProvisioning.IsProvisioned(Settings.Get("provisioned_users"), CurrentSid);

    /// <summary>persist today's remaining budget so restart (watchdog respawn) resumes correct value, not fresh allowance</summary>
    public static void Persist() =>
        Settings.Set(RemainingKey(DateOnly.FromDateTime(DateTime.Now)), Remaining.ToString(CultureInfo.InvariantCulture));

    /// <summary>settings key for today's remaining budget. must match key App + <see cref="OverlayApp"/> read back, and per-day prefix store purges on each open</summary>
    private static string RemainingKey(DateOnly date) =>
        $"{RemainingPrefix}{CurrentSid}_{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    // ---- Usage history -----------------------------------------------------
    // record active (unlocked) screen time per day so Settings can chart it

    private static int _usedSeconds;
    private static DateOnly _usageDate;

    /// <summary>load today's accumulated usage so respawn continues count</summary>
    public static void LoadUsage()
    {
        _usageDate = DateOnly.FromDateTime(DateTime.Now);
        _usedSeconds = int.TryParse(Settings.Get(UsageKey(_usageDate)), out var seconds) ? seconds : 0;
    }

    /// <summary>count one second of active screen use. midnight rollover flushes finished day + resets counter; persists ~twice a minute to bound writes</summary>
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

    /// <summary>write running usage total for current day</summary>
    public static void PersistUsage() =>
        Settings.Set(UsageKey(_usageDate), _usedSeconds.ToString(CultureInfo.InvariantCulture));

    private static string UsageKey(DateOnly date) =>
        $"{SettingsStore.UsagePrefix}{CurrentSid}_{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}

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

    /// <summary>process names the parent blocks outright; the overlay terminates them when foreground. loaded in <see cref="LoadEnforcement"/></summary>
    public static IReadOnlySet<string> BlockedApps = new HashSet<string>();

    /// <summary>per-app daily time limits (normalized name -> minutes). once an app's tracked foreground
    /// time today reaches its limit the overlay blocks it like <see cref="BlockedApps"/>. loaded in <see cref="LoadEnforcement"/></summary>
    public static IReadOnlyDictionary<string, int> AppLimits = new Dictionary<string, int>();

    /// <summary>parent unlocked during blocked schedule window; cleared once allowed window reached so next blocked window re-locks</summary>
    public static bool ScheduleOverride;

    /// <summary>parent ignores weekly schedule rest of session. unlike <see cref="ScheduleOverride"/> never cleared on allowed window, so later blocked windows don't re-lock. in-memory -> resets <c>false</c> on next restart (reboot/logon) = "until next restart" lifetime</summary>
    public static bool IgnoreScheduleUntilRestart;

    /// <summary>weekly total screen-time cap enforced (on top of the daily budget)</summary>
    public static bool WeeklyLimitEnabled;

    /// <summary>weekly cap in minutes (0 = no cap)</summary>
    public static int WeeklyLimitMinutes;

    /// <summary>screen time used so far this week (Mon..today), minutes; refreshed each enforcement reload</summary>
    public static int WeeklyUsedMinutes;

    /// <summary>parent lifted the weekly cap for the rest of this session (set from the lock's unlock/extend). in-memory -> re-enforced next restart</summary>
    public static bool WeeklyOverride;

    /// <summary>(re)load parent enforcement choices from store; each accessor falls back to safe default on missing/malformed key so partial DB never throws</summary>
    public static void LoadEnforcement()
    {
        LimitEnabled = Settings.GetBool(KeyLimitEnabled, true);
        ScheduleEnabled = Settings.GetBool(KeyScheduleEnabled, false);
        Schedule = Schedule.Parse(Settings.Get(KeySchedule));
        AllowedApps = AppAllowlist.Parse(Settings.Get("app_allowlist"));
        BlockedApps = AppAllowlist.Parse(Settings.Get("blocked_apps"));
        AppLimits = AppTimeLimits.Parse(Settings.Get("app_time_limits"));
        WeeklyLimitEnabled = Settings.GetBool("weekly_limit_enabled", false);
        WeeklyLimitMinutes = Settings.GetInt("weekly_limit_minutes", 0);
        WeeklyUsedMinutes = Settings.UsedThisWeekMinutes(DateOnly.FromDateTime(DateTime.Now));
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

    /// <summary>minutes until the next schedule (bedtime) block today, or -1 when the schedule is off or the rest of the day is allowed. caller should check <see cref="ScheduleAllows"/> first (only meaningful while currently allowed)</summary>
    public static int MinutesUntilScheduleBlock()
    {
        if (!ScheduleEnabled) return -1;
        var now = DateTime.Now;
        var weekday = TimeMath.MondayBasedWeekday(DateOnly.FromDateTime(now));
        return Schedule.MinutesUntilBlock(weekday, now.Hour * 60 + now.Minute);
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

    /// <summary>weekly cap enabled, this week's usage has reached it, and the parent has not lifted it this session</summary>
    public static bool WeeklyBlocked =>
        WeeklyLimitEnabled && WeeklyLimitMinutes > 0 && WeeklyUsedMinutes >= WeeklyLimitMinutes && !WeeklyOverride;

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
    public static bool ShouldBlock => BudgetBlocked || WeeklyBlocked || ScheduleBlocked || NewUserBlocked;

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

    // ---- Per-app daily time limits -----------------------------------------
    // Track each app's foreground seconds for the day so an app with its own limit
    // (AppLimits) can be blocked once it reaches that limit, independent of the global
    // budget. Persisted as one serialized state.db row per day so the count survives an
    // overlay restart; midnight rollover flushes the finished day and resets.

    private static Dictionary<string, int> _appUsed = new(StringComparer.OrdinalIgnoreCase);
    private static DateOnly _appUsageDate;

    /// <summary>load today's accumulated per-app usage so a respawn continues the counts</summary>
    public static void LoadAppUsage()
    {
        _appUsageDate = DateOnly.FromDateTime(DateTime.Now);
        _appUsed = AppUsageMap.Parse(Settings.Get(AppUsageKey(_appUsageDate)));
    }

    /// <summary>count one second of foreground use against <paramref name="appName"/>'s daily total. midnight
    /// rollover flushes the finished day + resets; persists ~twice a minute to bound writes. no-op for a blank
    /// name or when the app has no configured limit (nothing reads the count otherwise)</summary>
    public static void RecordAppSecond(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return;
        var name = AppAllowlist.Normalize(appName);
        if (name.Length == 0 || AppTimeLimits.LimitMinutesFor(AppLimits, name) < 0) return;

        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today != _appUsageDate)
        {
            PersistAppUsage();
            _appUsageDate = today;
            _appUsed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        _appUsed.TryGetValue(name, out var seconds);
        _appUsed[name] = seconds + 1;
        if ((seconds + 1) % 30 == 0) PersistAppUsage();
    }

    /// <summary>true when <paramref name="appName"/> has a per-app limit and today's tracked foreground time
    /// has reached it; the overlay then terminates the app's foreground (like the blocklist)</summary>
    public static bool IsAppOverLimit(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return false;
        var name = AppAllowlist.Normalize(appName);
        var limitMinutes = AppTimeLimits.LimitMinutesFor(AppLimits, name);
        if (limitMinutes < 0) return false;

        _appUsed.TryGetValue(name, out var seconds);
        return seconds >= limitMinutes * 60;
    }

    /// <summary>write the running per-app usage totals for the current day</summary>
    public static void PersistAppUsage() =>
        Settings.Set(AppUsageKey(_appUsageDate), AppUsageMap.Serialize(_appUsed));

    private static string AppUsageKey(DateOnly date) =>
        $"app_usage_{CurrentSid}_{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    // ---- Child-initiated breaks (pause) ------------------------------------
    // A child can take a short break that freezes the budget, rate-limited by the
    // parent's pause policy (PauseRules in Core): a daily pause budget, a per-break
    // cap, a cooldown between breaks, and a minimum active time before the first one.
    // No passcode — the policy is what prevents abuse. Budget used today and the last
    // break's end are persisted to state.db so the limits survive an overlay restart
    // (PausedUntilUnix itself stays in-memory, so a restart simply ends the break).

    private static int _pauseUsedSeconds;
    private static DateOnly _pauseDate;
    private static long _lastPauseEndUnix;
    private static bool _wasPaused;

    /// <summary>Load today's consumed pause budget and the last break's end so the limits persist across a restart.</summary>
    public static void LoadPause()
    {
        _pauseDate = DateOnly.FromDateTime(DateTime.Now);
        _pauseUsedSeconds = int.TryParse(Settings.Get(PauseUsedKey(_pauseDate)), out var used) ? used : 0;
        _lastPauseEndUnix = long.TryParse(Settings.Get(PauseLastEndKey()), out var end) ? end : 0;
    }

    /// <summary>
    /// Attempt to start a child-initiated break. Consults the parent's pause policy
    /// (enabled, time left, daily budget, cooldown, minimum active time). On success
    /// freezes the budget for the granted duration and returns <see cref="PauseBlock.None"/>
    /// with <paramref name="grantedSeconds"/> set; otherwise returns the blocking reason
    /// and changes nothing.
    /// </summary>
    public static PauseBlock TryStartBreak(out int grantedSeconds)
    {
        grantedSeconds = 0;
        RollPauseDay();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var dailyBudget = Settings.GetInt("pause_daily_budget", 45) * 60;
        var state = new PauseState(
            Enabled: Settings.GetBool("pause_enabled", true),
            RemainingSeconds: Remaining,
            PauseUsedSeconds: _pauseUsedSeconds,
            DailyBudgetSeconds: dailyBudget,
            LastPauseEndUnix: _lastPauseEndUnix,
            NowUnix: now,
            CooldownSeconds: Settings.GetInt("pause_cooldown", 15) * 60,
            SessionActiveSeconds: _usedSeconds,
            MinActiveSeconds: Settings.GetInt("pause_min_active_time", 10) * 60);

        var verdict = PauseRules.CanPause(state);
        if (verdict != PauseBlock.None) return verdict;

        grantedSeconds = PauseRules.MaxPauseDuration(
            Settings.GetInt("pause_max_duration", 20) * 60, dailyBudget, _pauseUsedSeconds);
        if (grantedSeconds <= 0) return PauseBlock.BudgetExhausted;

        PausedUntilUnix = now + grantedSeconds;
        _wasPaused = true;
        return PauseBlock.None;
    }

    /// <summary>Seconds remaining before another break is allowed, for the cooldown message; 0 if none.</summary>
    public static int CooldownRemainingSeconds()
    {
        if (_lastPauseEndUnix <= 0) return 0;
        var cooldown = Settings.GetInt("pause_cooldown", 15) * 60;
        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _lastPauseEndUnix;
        return (int)Math.Max(0, cooldown - elapsed);
    }

    /// <summary>Per-tick pause accounting: charge a second against the daily budget while a break is in
    /// effect, and stamp the break's end the instant it lapses (starts the cooldown). Call once per tick.</summary>
    public static void TickPause()
    {
        RollPauseDay();

        if (IsPaused)
        {
            _wasPaused = true;
            _pauseUsedSeconds++;
            if (_pauseUsedSeconds % 15 == 0) PersistPauseUsed();
            return;
        }

        if (_wasPaused)
        {
            // break just lapsed: record its end so the cooldown begins, and flush the budget
            _wasPaused = false;
            _lastPauseEndUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Settings.Set(PauseLastEndKey(), _lastPauseEndUnix.ToString(CultureInfo.InvariantCulture));
            PersistPauseUsed();
        }
    }

    private static void RollPauseDay()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _pauseDate) return;
        PersistPauseUsed();
        _pauseDate = today;
        _pauseUsedSeconds = 0;
    }

    private static void PersistPauseUsed() =>
        Settings.Set(PauseUsedKey(_pauseDate), _pauseUsedSeconds.ToString(CultureInfo.InvariantCulture));

    private static string PauseUsedKey(DateOnly date) =>
        $"pause_used_{CurrentSid}_{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    private static string PauseLastEndKey() => $"pause_last_end_{CurrentSid}";

    /// <summary>
    /// Break time (seconds) the child could redeem right now for bonus minutes while budget-blocked, or 0
    /// if none is available (breaks off, daily budget spent, or still in cooldown). Pure — no mutation; the
    /// overlay publishes this to <c>lock_break_minutes</c> so the lock screen can offer the button. Unlike
    /// <see cref="TryStartBreak"/> there is no "time too low" gate: the whole point is that time has run out.
    /// </summary>
    public static int BreakOfferSeconds()
    {
        RollPauseDay();
        if (!Settings.GetBool("pause_enabled", true)) return 0;

        var dailyBudget = Settings.GetInt("pause_daily_budget", 45) * 60;
        if (PauseRules.RemainingBudget(dailyBudget, _pauseUsedSeconds) <= 0) return 0;

        if (_lastPauseEndUnix > 0)
        {
            var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _lastPauseEndUnix;
            if (since < Settings.GetInt("pause_cooldown", 15) * 60) return 0;
        }

        return PauseRules.MaxPauseDuration(
            Settings.GetInt("pause_max_duration", 20) * 60, dailyBudget, _pauseUsedSeconds);
    }

    /// <summary>
    /// Redeem the available break as bonus screen time while budget-blocked: adds the granted minutes to
    /// <see cref="Remaining"/>, charges them against the daily break budget, and starts the cooldown.
    /// Returns the granted seconds, or 0 when nothing is available (see <see cref="BreakOfferSeconds"/>).
    /// </summary>
    public static int TryRedeemBreak()
    {
        var granted = BreakOfferSeconds();
        if (granted <= 0) return 0;

        Remaining = TimeKeeper.Extend(Math.Max(0, Remaining), granted / 60);
        _pauseUsedSeconds += granted;
        PersistPauseUsed();
        _lastPauseEndUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Settings.Set(PauseLastEndKey(), _lastPauseEndUnix.ToString(CultureInfo.InvariantCulture));
        Persist();
        return granted;
    }
}

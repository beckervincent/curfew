using System.Runtime.InteropServices;
using Curfew.Core;
using Curfew.Core.Localization;
using Curfew.Overlay;
using static Curfew.Overlay.Native;

// one overlay per session. "Local\" scopes mutex to session: each user gets one, watchdog respawn never stacks duplicates
OverlayLog.Write("process start");
using var instance = new Mutex(true, @"Local\CurfewOverlayInstance", out var createdNew);
if (!createdNew)
{
    OverlayLog.Write("another instance already running; exiting");
    return;
}

try
{
    OverlayApp.Run();
}
catch (Exception ex)
{
    OverlayLog.Write($"unhandled: {ex}");
}

namespace Curfew.Overlay
{
    /// <summary>Win32 mini countdown overlay -- small always-on-top reminder pill showing remaining budget (or wall clock in schedule-only mode). plain Win32 for reliable service-spawn start; full-screen passcode lock in <see cref="LockScreen"/></summary>
    internal static class OverlayApp
    {
        private const string ClassName = "CurfewOverlayClass";

        // pill geometry; compact = gentle reminder not banner. left accent bar carries colour state
        private const int Width = 168;
        private const int Height = 46;
        private const int Margin = 12;
        private const int AccentBarWidth = 5;
        private const int TextInset = 16;

        // layered-window opacity (0-255). legible over busy wallpaper, still unobtrusive
        private const byte Opacity = 225;

        // DrawTextW left-align flag. DT_LEFT is 0x0 but not exposed by Native; define locally for readable calls
        private const int DT_LEFT = 0x0;

        // colours 0x00BBGGRR (GDI COLORREF order)
        private const uint ColorBg = 0x00222222;       // near-black panel
        private const uint ColorLabel = 0x00A8A29A;    // muted grey caption
        private const uint ColorWhite = 0x00F4F4F4;    // primary text (off-white)
        private const uint ColorAmber = 0x00309CF0;    // warning  (~5 min) BGR
        private const uint ColorRed = 0x004444FF;       // critical (<1 min) BGR

        // keep delegate alive for window life; a local could be collected while Win32 holds the function pointer
        private static readonly WndProc Proc = WindowProc;

        // The shell broadcasts this registered message when the notification area is
        // (re)created. Resolved once at startup; 0 only if registration failed.
        private static uint _taskbarCreatedMsg;

        public static void Run()
        {
            // harden enforcement process (child's session) vs DLL injection/hijack before any other DLL loads
            Curfew.Core.Security.ProcessHardening.Apply();

            var today = DateOnly.FromDateTime(DateTime.Now);
            OverlayState.Settings = CurfewPaths.OpenSettings(today);

            // scope per-user config + counters to this session's user
            OverlayState.CurrentSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
            OverlayState.Settings.UserSid = OverlayState.CurrentSid;
            // grandfather existing users so only genuinely new (no history, not set up) hit setup lock
            OverlayState.UserHasHistory = OverlayState.Settings.HasUsageHistory(OverlayState.CurrentSid);

            int? saved = int.TryParse(OverlayState.Settings.Get(RemainingKey(today)), out var s) ? s : null;
            var weekday = TimeMath.MondayBasedWeekday(today);
            OverlayState.Remaining =
                TimeKeeper.InitialRemaining(saved, OverlayState.Settings.GetDailyLimit(weekday));
            OverlayState.LoadEnforcement();
            OverlayState.LoadUsage();
            OverlayState.LoadAppUsage();
            OverlayState.LoadPause();

            var hInstance = GetModuleHandleW(null);
            OverlayLog.Write($"settings opened, remaining={OverlayState.Remaining}, hInstance={hInstance}");

            // we own WM_PAINT + WM_ERASEBKGND, so no class bg brush -> no leak, no pre-first-paint flash
            var wc = new WNDCLASSW
            {
                lpfnWndProc = Proc,
                hInstance = hInstance,
                lpszClassName = ClassName,
                hbrBackground = IntPtr.Zero,
            };
            var atom = RegisterClassW(ref wc);
            OverlayLog.Write($"RegisterClassW atom={atom} err={Marshal.GetLastWin32Error()}");

            var screenWidth = GetSystemMetrics(SM_CXSCREEN);
            var hwnd = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE,
                ClassName, "Curfew", WS_POPUP,
                screenWidth - Width - Margin, Margin, Width, Height,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            OverlayLog.Write($"CreateWindowExW hwnd={hwnd} err={Marshal.GetLastWin32Error()} screenW={screenWidth}");

            if (hwnd == IntPtr.Zero)
            {
                OverlayLog.Write("window creation failed; exiting");
                return;
            }
            OverlayState.MiniHwnd = hwnd;

            // semi-transparent: gentle reminder not a wall
            SetLayeredWindowAttributes(hwnd, 0, Opacity, LWA_ALPHA);
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            SetTimer(hwnd, new IntPtr(1), 1000, IntPtr.Zero);

            // Register the shell's "TaskbarCreated" broadcast so the tray icon can be
            // re-added once Explorer's notification area exists (it often does not yet
            // when the logon task starts the overlay) and after any Explorer restart.
            _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
            OverlayLog.Write($"TaskbarCreated msg={_taskbarCreatedMsg}");

            TrayIcon.Add(hwnd, hInstance);

            // pre-create lock window; show now if already blocked. else clear lock_active so respawn-while-unblocked can't leave Task Manager lockdown stuck on
            LockScreen.Register(hInstance);
            if (OverlayState.ShouldBlock) LockScreen.Show();
            else OverlayState.Settings.Set("lock_active", "0");

            OverlayLog.Write("entering message loop");

            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            OverlayLog.Write("message loop exited");
        }

        private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // Re-add the tray icon when the shell (re)creates the notification area.
            // Not a switch case because the message id is resolved at runtime.
            if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
            {
                TrayIcon.Readd();
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case WM_ERASEBKGND:
                    // suppress default erase: WM_PAINT repaints whole client area, erasing first only flickers. non-zero = handled
                    return new IntPtr(1);

                case WM_PAINT:
                    Paint(hwnd);
                    return IntPtr.Zero;

                case WM_TIMER:
                    Tick(hwnd);
                    return IntPtr.Zero;

                case TrayIcon.WM_TRAYICON:
                    TrayIcon.OnMessage(hwnd, lParam);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    TrayIcon.Remove();
                    PostQuitMessage(0);
                    return IntPtr.Zero;

                default:
                    return DefWindowProcW(hwnd, msg, wParam, lParam);
            }
        }

        /// <summary>parent-granted pause (break) duration, seconds</summary>
        private const long PauseDurationSeconds = 600; // 10 minutes

        /// <summary>overlay enforcement-reload interval, seconds (ticks)</summary>
        private const int ReloadEverySeconds = 30;
        private static int _reloadCounter;

        /// <summary>dirs an allow-listed app must run from to be exempt; all admin-writable only so child can't drop a renamed exe there to stop budget clock (see <see cref="AppAllowlist.AllowsTrusted"/>)</summary>
        private static readonly string[] TrustedAppRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        }.Where(p => !string.IsNullOrEmpty(p)).ToArray();

        /// <summary>last blocked-app name we ballooned about, so the notification fires once per app rather than every tick</summary>
        private static string? _lastBlockedAppName;

        /// <summary>last app we ballooned a time-up notice for, so it fires once per app rather than every tick</summary>
        private static string? _lastTimeUpAppName;

        /// <summary>Read the foreground app once, then run every per-app rule against it: hard blocklist
        /// (terminate), usage tracking (for stats + per-app limits), and the per-app daily limit (terminate
        /// when over). One <see cref="ForegroundApp.Foreground"/> call per tick. <paramref name="active"/> is
        /// false while idle or paused so time isn't charged when the child is away. Never touches Curfew's own
        /// windows.</summary>
        private static void HandleForegroundApp(bool active)
        {
            var (pid, name) = ForegroundApp.Foreground();
            if (pid == 0 || string.IsNullOrEmpty(name)) { _lastBlockedAppName = null; _lastTimeUpAppName = null; return; }
            if (name.StartsWith("Curfew", StringComparison.OrdinalIgnoreCase)) return; // never touch our own UI

            // 1. hard blocklist: close outright regardless of time
            if (EnforceBlockedApp(pid, name)) return;

            // 2. record active foreground time (every app -> per-app usage stats; per-app limits read it too)
            if (active) OverlayState.RecordAppSecond(name);

            // 3. per-app daily limit: close once today's tracked time reaches the app's own budget
            EnforceAppTimeLimit(pid, name);
        }

        /// <summary>Terminate the foreground app when it is on the parent's blocklist; notify the child once.
        /// Returns true when it was blocked (so no further per-app rule should run for it this tick).</summary>
        private static bool EnforceBlockedApp(int pid, string name)
        {
            if (OverlayState.BlockedApps.Count == 0 || !AppAllowlist.Allows(OverlayState.BlockedApps, name))
            {
                _lastBlockedAppName = null;
                return false;
            }

            ForegroundApp.Terminate(pid);
            if (!string.Equals(_lastBlockedAppName, name, StringComparison.OrdinalIgnoreCase))
            {
                _lastBlockedAppName = name;
                OverlayLog.Write($"blocked app terminated: {name}");
                EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.AppBlocked, name);
                TrayIcon.ShowBalloon(Loc.T("tray.idle"), Loc.T("tray.appblocked", name));
            }
            return true;
        }

        /// <summary>Terminate the foreground app once today's tracked time reaches its per-app daily limit;
        /// notify the child once. No-op when the app has no limit.</summary>
        private static void EnforceAppTimeLimit(int pid, string name)
        {
            if (!OverlayState.IsAppOverLimit(name)) { _lastTimeUpAppName = null; return; }

            ForegroundApp.Terminate(pid);
            if (!string.Equals(_lastTimeUpAppName, name, StringComparison.OrdinalIgnoreCase))
            {
                _lastTimeUpAppName = name;
                OverlayLog.Write($"app time limit reached: {name}");
                EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.AppTimeLimitReached, name);
                TrayIcon.ShowBalloon(Loc.T("tray.idle"), Loc.T("tray.apptimeup", name));
            }
        }

        /// <summary>seconds of continuous active screen use since the last eye-strain reminder (or last rest)</summary>
        private static int _eyeStrainActiveSeconds;

        /// <summary>20-20-20 rule nudge: after <see cref="OverlayState.EyeStrainIntervalMinutes"/> of continuous
        /// active use, balloon the child to look ~20 ft away for 20 s, then reset the streak. A rest (idle or a
        /// pause) also resets it, since the eyes have already had a break. No-op when disabled or the interval
        /// is non-positive.</summary>
        private static void EyeStrainReminder(bool active)
        {
            if (!OverlayState.EyeStrainEnabled || OverlayState.EyeStrainIntervalMinutes <= 0 || !active)
            {
                _eyeStrainActiveSeconds = 0;
                return;
            }

            if (++_eyeStrainActiveSeconds < OverlayState.EyeStrainIntervalMinutes * 60) return;

            _eyeStrainActiveSeconds = 0;
            OverlayLog.Write("eye-strain reminder fired");
            TrayIcon.ShowBalloon(Loc.T("tray.eyestrain.title"), Loc.T("tray.eyestrain.body"));
        }

        /// <summary>foreground app allow-listed -> this second exempt from budget</summary>
        private static bool ForegroundExempt() =>
            OverlayState.AllowedApps.Count > 0
            && AppAllowlist.AllowsTrusted(OverlayState.AllowedApps, ForegroundApp.ProcessImagePath(), TrustedAppRoots);

        /// <summary>session idle past the configured timeout (no keyboard/mouse), so this second is not
        /// active use. off when idle_enabled is false or idle_timeout_minutes is non-positive. dwTime is a
        /// GetTickCount value; unsigned subtraction from Environment.TickCount cancels the 32-bit wrap.</summary>
        private static bool Idle()
        {
            if (!OverlayState.Settings.GetBool("idle_enabled", true)) return false;
            var timeoutSeconds = OverlayState.Settings.GetInt("idle_timeout_minutes", 5) * 60;
            if (timeoutSeconds <= 0) return false;

            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return false;

            var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
            return idleMs >= (uint)timeoutSeconds * 1000u;
        }

        private static void Tick(IntPtr hwnd)
        {
            // apply parent-approved tray action. Curfew.App writes command only after passcode verified, so overlay needs no passcode UI
            ExecutePendingTrayCommand(hwnd);

            // time frozen while lock up, but lock still needs per-second work: apply WinUI actions + keep surface alive
            if (OverlayState.Locked)
            {
                LockScreen.WhileLockedTick();
                return;
            }

            // pick up parent changes (limits/schedule/allow-list) without restart, ~twice a minute so SQLite reads stay cheap
            if (++_reloadCounter >= ReloadEverySeconds)
            {
                _reloadCounter = 0;
                OverlayState.LoadEnforcement();
            }

            // charge an in-progress child break against the daily pause budget and stamp its end when it
            // lapses (cooldown). a break freezes the budget regardless of idle, so this runs first
            OverlayState.TickPause();

            // idle (no keyboard/mouse past the configured timeout) is not active screen use: it must
            // neither consume the budget nor count toward usage history. the child stepping away should
            // not drain their time. honours the idle_enabled / idle_timeout_minutes settings
            var idle = Idle();

            // count this second of active (unlocked, non-idle) screen time for usage history -- but NEVER
            // for a user who still owes setup. a recorded row is read on next boot as pre-gate history and
            // permanently grandfathers the user out of setup (see OverlayState.PendingNewUser). healthy boot
            // locks them here anyway; this guard closes the window where the gate failed to engage
            // (config.db unreadable) so the lock re-raises next boot instead of being silently disabled
            if (!OverlayState.PendingNewUser && !idle) OverlayState.RecordActiveSecond();

            // foreground app rules in one pass (single Foreground() read): blocklist, usage tracking, per-app
            // limit. per-app limits are independent of the global budget — a game can be capped at 1h/day even
            // when general screen time remains
            HandleForegroundApp(active: !idle && !OverlayState.IsPaused);

            // 20-20-20 eye-strain nudge: after enough continuous active use, remind the child to look away.
            // idle or a pause counts as rest and resets the streak
            EyeStrainReminder(active: !idle && !OverlayState.IsPaused);

            // budget ticks down only when active control, not idle, no pause, foreground not allow-listed (homework/IDE exempt)
            if (OverlayState.LimitEnabled && !idle && !OverlayState.IsPaused && !ForegroundExempt())
            {
                OverlayState.Remaining = TimeKeeper.Tick(OverlayState.Remaining);
                if (TimeKeeper.ShouldPersist(OverlayState.Remaining)) OverlayState.Persist();
            }

            // reopened schedule window clears parent override
            if (OverlayState.ScheduleAllows()) OverlayState.ScheduleOverride = false;

            UpdateTray();

            // repaint without erase (false): WM_PAINT redraws all, WM_ERASEBKGND suppressed -> flicker-free
            InvalidateRect(hwnd, IntPtr.Zero, false);

            if (OverlayState.ShouldBlock) LockScreen.Show();
        }

        /// <summary>consume + apply one-shot command from passcode-gated tray menu in Curfew.App. cleared immediately (runs once), ignored if older than a minute</summary>
        private static void ExecutePendingTrayCommand(IntPtr hwnd)
        {
            var cmd = OverlayState.Settings.Get("tray_command");
            if (string.IsNullOrEmpty(cmd)) return;

            OverlayState.Settings.Set("tray_command", string.Empty); // consume once
            long.TryParse(OverlayState.Settings.Get("tray_command_at"), out var at);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now - at > 60) return; // stale

            switch (cmd)
            {
                case "extend15": ApplyExtend(15); break;
                case "extend45": ApplyExtend(45); break;
                case "break": StartBreak(); break;
                case "pause":
                    OverlayState.PausedUntilUnix = now + PauseDurationSeconds;
                    OverlayLog.Write("tray: paused");
                    break;
                case "resume":
                    OverlayState.PausedUntilUnix = 0;
                    OverlayLog.Write("tray: resumed");
                    break;
                case "quit":
                    // refuse quit while locked: DestroyWindow exits loop + kills process WITHOUT LockScreen.Hide(), leaving lock_active=1 + WinUI lock orphaned, session locked-down with no enforcer until watchdog respawn. command already consumed -> dropped not replayed; parent re-issues quit after unlock when teardown runs clean
                    if (OverlayState.Locked)
                    {
                        OverlayLog.Write("tray: quit ignored while locked");
                        break;
                    }
                    OverlayLog.Write("tray: quit requested");
                    DestroyWindow(hwnd); // WM_DESTROY -> tray removal + PostQuitMessage
                    break;
            }
        }

        /// <summary>add bonus minutes + lift schedule block, like lock-screen extend</summary>
        private static void ApplyExtend(int minutes)
        {
            OverlayState.Remaining = TimeKeeper.Extend(Math.Max(0, OverlayState.Remaining), minutes);
            OverlayState.ScheduleOverride = true;
            OverlayState.Persist();
            OverlayLog.Write($"tray: extended +{minutes} min");
        }

        /// <summary>Apply a child-initiated break governed by the parent's pause policy, then tell the child
        /// (balloon) whether it started and for how long, or why it was refused.</summary>
        private static void StartBreak()
        {
            var verdict = OverlayState.TryStartBreak(out var grantedSeconds);
            if (verdict == PauseBlock.None)
            {
                OverlayLog.Write($"tray: break started ({grantedSeconds}s)");
                EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.BreakTaken, $"{grantedSeconds / 60} min");
                TrayIcon.ShowBalloon(Loc.T("tray.idle"), Loc.T("tray.break.granted", grantedSeconds / 60));
                return;
            }

            OverlayLog.Write($"tray: break denied ({verdict})");
            var message = verdict switch
            {
                PauseBlock.Disabled => Loc.T("tray.break.denied.disabled"),
                PauseBlock.BudgetExhausted => Loc.T("tray.break.denied.budget"),
                PauseBlock.Cooldown => Loc.T("tray.break.denied.cooldown",
                    Math.Max(1, (OverlayState.CooldownRemainingSeconds() + 59) / 60)),
                PauseBlock.MinActiveTimeNotMet => Loc.T("tray.break.denied.active"),
                PauseBlock.TimeTooLow => Loc.T("tray.break.denied.timelow"),
                _ => Loc.T("tray.break.denied.budget"),
            };
            TrayIcon.ShowBalloon(Loc.T("tray.idle"), message);
        }

        /// <summary>refresh tray tooltip, raise balloon at each warning threshold</summary>
        private static void UpdateTray()
        {
            TrayIcon.UpdateTooltip(
                OverlayState.IsPaused ? Loc.T("tray.paused")
                : OverlayState.LimitEnabled ? Loc.T("tray.left", TimeMath.FormatCompact(OverlayState.Remaining))
                : Loc.T("tray.idle"));

            // bedtime wind-down: warn once before the next schedule block, even in schedule-only mode
            // (so this runs before the daily-budget early-return below)
            WarnBeforeBedtime();

            if (!OverlayState.LimitEnabled || OverlayState.IsPaused) return;

            var warn1 = OverlayState.Settings.GetInt("warning1_minutes", 10);
            var warn2 = OverlayState.Settings.GetInt("warning2_minutes", 5);

            if (TimeKeeper.WarningFires(OverlayState.Remaining, warn1))
                TrayIcon.ShowBalloon(Loc.T("tray.idle"), WarningMessage("warning1_message"));
            else if (TimeKeeper.WarningFires(OverlayState.Remaining, warn2))
                TrayIcon.ShowBalloon(Loc.T("tray.idle"), WarningMessage("warning2_message"));
        }

        private static string WarningMessage(string key)
        {
            var message = OverlayState.Settings.Get(key);
            return string.IsNullOrWhiteSpace(message) ? Loc.T("warn.default") : message;
        }

        /// <summary>Minute-of-day of the bedtime block we last warned about, so the wind-down balloon fires
        /// once per upcoming block rather than every tick. -1 = no pending warning.</summary>
        private static int _bedtimeWarnedStartMinute = -1;

        /// <summary>Raise a one-shot balloon when the next schedule (bedtime) block is within the configured
        /// wind-down window, so the child gets warning before the screen locks. No-op when the schedule is
        /// off, currently blocked, the wind-down is disabled, or no block is imminent today.</summary>
        private static void WarnBeforeBedtime()
        {
            var windDown = OverlayState.Settings.GetInt("wind_down_minutes", 10);
            if (windDown <= 0 || !OverlayState.ScheduleEnabled || !OverlayState.ScheduleAllows())
            {
                _bedtimeWarnedStartMinute = -1;
                return;
            }

            var mins = OverlayState.MinutesUntilScheduleBlock();
            if (mins <= 0 || mins > windDown)
            {
                _bedtimeWarnedStartMinute = -1;
                return;
            }

            var now = DateTime.Now;
            var blockStartMinute = now.Hour * 60 + now.Minute + mins; // stable id for this upcoming block
            if (_bedtimeWarnedStartMinute == blockStartMinute) return; // already warned for it

            _bedtimeWarnedStartMinute = blockStartMinute;
            TrayIcon.ShowBalloon(Loc.T("tray.idle"), Loc.T("tray.bedtime", mins));
        }

        /// <summary>paint whole pill in one pass: panel fill, colour-coded accent bar, small caption, large remaining-time (or clock) value. every GDI object released, DC originals restored</summary>
        private static void Paint(IntPtr hwnd)
        {
            var hdc = BeginPaint(hwnd, out var ps);
            GetClientRect(hwnd, out var rect);

            // 1. solid panel bg (single fill = no flicker)
            var bgBrush = CreateSolidBrush(ColorBg);
            FillRect(hdc, ref rect, bgBrush);
            DeleteObject(bgBrush);

            // pick what to show + accent colour for its state
            string value;
            string caption;
            uint accent;
            if (OverlayState.LimitEnabled)
            {
                value = TimeMath.FormatCompact(OverlayState.Remaining);
                caption = "TIME LEFT";
                accent = ColorForRemaining(OverlayState.Remaining);
            }
            else
            {
                value = DateTime.Now.ToString("HH:mm");
                caption = "SCHEDULE";
                accent = ColorWhite;
            }

            // 2. accent bar left edge -- at-a-glance status colour
            var barRect = new RECT
            {
                left = rect.left,
                top = rect.top,
                right = rect.left + AccentBarWidth,
                bottom = rect.bottom,
            };
            var accentBrush = CreateSolidBrush(accent);
            FillRect(hdc, ref barRect, accentBrush);
            DeleteObject(accentBrush);

            SetBkMode(hdc, TRANSPARENT);

            // text column: inset from accent bar, padded right
            var textLeft = rect.left + AccentBarWidth + TextInset;
            var textRight = rect.right - 10;

            // 3. caption: small muted label above value
            DrawText(hdc, caption, textLeft, rect.top + 6, textRight, rect.top + 22,
                fontSize: 12, weight: 600, color: ColorLabel,
                format: DT_LEFT | DT_SINGLELINE | DT_VCENTER);

            // 4. value: large semibold, status-coloured (red critical, amber warning, else off-white)
            var valueColor = OverlayState.LimitEnabled ? accent : ColorWhite;
            DrawText(hdc, value, textLeft, rect.top + 18, textRight, rect.bottom - 4,
                fontSize: 26, weight: 700, color: valueColor,
                format: DT_LEFT | DT_SINGLELINE | DT_VCENTER);

            EndPaint(hwnd, ref ps);
        }

        /// <summary>draw one line of Segoe UI text into rect; create + release font, restore DC's previous font</summary>
        private static void DrawText(
            IntPtr hdc, string text, int left, int top, int right, int bottom,
            int fontSize, int weight, uint color, int format)
        {
            var font = CreateFontW(fontSize, 0, 0, 0, weight, 0, 0, 0, 0, 0, 0, 0, 0, "Segoe UI");
            var oldFont = SelectObject(hdc, font);

            SetTextColor(hdc, color);
            var rect = new RECT { left = left, top = top, right = right, bottom = bottom };
            DrawTextW(hdc, text, text.Length, ref rect, format);

            SelectObject(hdc, oldFont);
            DeleteObject(font);
        }

        /// <summary>accent colour for remaining budget: white normal, amber last 5 min, red final minute</summary>
        private static uint ColorForRemaining(int seconds)
        {
            if (seconds <= 60) return ColorRed;
            if (seconds <= 300) return ColorAmber;
            return ColorWhite;
        }

        // invariant culture: must match OverlayState writer + store purge exactly, even under non-Gregorian region format
        private static string RemainingKey(DateOnly date) =>
            $"remaining_time_{OverlayState.CurrentSid}_{date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}";
    }
}

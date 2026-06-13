using System.Runtime.InteropServices;
using Curfew.Core;
using Curfew.Core.Localization;
using Curfew.Overlay;
using static Curfew.Overlay.Native;

// One overlay per session. "Local\" scopes the mutex to the session, so each
// logged-in user gets one and the watchdog respawn never stacks duplicates.
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
    /// <summary>
    /// Win32 mini countdown overlay — a small, always-on-top reminder pill that
    /// shows the remaining daily budget (or the wall clock in schedule-only mode).
    /// Plain Win32 so it starts reliably when the service spawns it. The
    /// full-screen passcode lock lives in <see cref="LockScreen"/>.
    /// </summary>
    internal static class OverlayApp
    {
        private const string ClassName = "CurfewOverlayClass";

        // Pill geometry. Kept compact so it reads as a gentle reminder rather
        // than a banner; the accent bar on the left carries the colour state.
        private const int Width = 168;
        private const int Height = 46;
        private const int Margin = 12;
        private const int AccentBarWidth = 5;
        private const int TextInset = 16;

        // Layered-window opacity (0–255). High enough to stay legible over busy
        // wallpaper, low enough to feel unobtrusive.
        private const byte Opacity = 225;

        // DrawTextW left-alignment flag. DT_LEFT is 0x0 in Win32 but isn't
        // exposed by Native, so define it locally for self-documenting calls.
        private const int DT_LEFT = 0x0;

        // Colours are 0x00BBGGRR (GDI COLORREF order).
        private const uint ColorBg = 0x00222222;       // near-black panel
        private const uint ColorLabel = 0x00A8A29A;    // muted grey caption
        private const uint ColorWhite = 0x00F4F4F4;    // primary text (off-white)
        private const uint ColorAmber = 0x00309CF0;    // warning  (~5 min) BGR
        private const uint ColorRed = 0x004444FF;       // critical (<1 min) BGR

        // Keep the delegate alive for the window's lifetime; if it were a local
        // it could be collected while Win32 still holds the function pointer.
        private static readonly WndProc Proc = WindowProc;

        public static void Run()
        {
            // Harden the enforcement process (runs in the child's session) against
            // DLL injection / hijacking before any other DLL loads.
            Curfew.Core.Security.ProcessHardening.Apply();

            var today = DateOnly.FromDateTime(DateTime.Now);
            OverlayState.Settings = CurfewPaths.OpenSettings(today);

            // Scope all per-user config + counters to this session's user.
            OverlayState.CurrentSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
            OverlayState.Settings.UserSid = OverlayState.CurrentSid;

            int? saved = int.TryParse(OverlayState.Settings.Get(RemainingKey(today)), out var s) ? s : null;
            var weekday = TimeMath.MondayBasedWeekday(today);
            OverlayState.Remaining =
                TimeKeeper.InitialRemaining(saved, OverlayState.Settings.GetDailyLimit(weekday));
            OverlayState.LoadEnforcement();
            OverlayState.LoadUsage();

            var hInstance = GetModuleHandleW(null);
            OverlayLog.Write($"settings opened, remaining={OverlayState.Remaining}, hInstance={hInstance}");

            // We own all painting (WM_PAINT) and erasing (WM_ERASEBKGND), so the
            // class needs no background brush — that also avoids leaking one and
            // prevents a single-colour flash before the first paint.
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

            // Semi-transparent so it reads as a gentle reminder, not a wall.
            SetLayeredWindowAttributes(hwnd, 0, Opacity, LWA_ALPHA);
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            SetTimer(hwnd, new IntPtr(1), 1000, IntPtr.Zero);

            TrayIcon.Add(hwnd, hInstance);

            // Pre-create the lock window; show it immediately if already blocked.
            // Otherwise clear lock_active so a respawn while unblocked can never leave
            // the service's Task Manager lockdown stuck on.
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
            switch (msg)
            {
                case WM_ERASEBKGND:
                    // Suppress the default erase: WM_PAINT repaints the whole
                    // client area every time, so erasing first only causes
                    // flicker. Returning non-zero tells Windows it's handled.
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

        /// <summary>Duration of a parent-granted pause (break), in seconds.</summary>
        private const long PauseDurationSeconds = 600; // 10 minutes

        /// <summary>How often the overlay reloads enforcement settings, in seconds (ticks).</summary>
        private const int ReloadEverySeconds = 30;
        private static int _reloadCounter;

        /// <summary>
        /// Directories an allow-listed app must run from to be exempt. All are
        /// admin-writable only, so the child cannot place a renamed executable
        /// there to stop the budget clock (see <see cref="AppAllowlist.AllowsTrusted"/>).
        /// </summary>
        private static readonly string[] TrustedAppRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        }.Where(p => !string.IsNullOrEmpty(p)).ToArray();

        /// <summary>Whether the foreground app is allow-listed, so this second is exempt from the budget.</summary>
        private static bool ForegroundExempt() =>
            OverlayState.AllowedApps.Count > 0
            && AppAllowlist.AllowsTrusted(OverlayState.AllowedApps, ForegroundApp.ProcessImagePath(), TrustedAppRoots);

        private static void Tick(IntPtr hwnd)
        {
            // Apply any parent-approved tray action. The command is written by
            // Curfew.App only after the parent passcode is verified, so the overlay
            // itself needs no passcode UI of its own.
            ExecutePendingTrayCommand(hwnd);

            // Time is frozen while the lock screen is up — but the lock still needs
            // its per-second work: apply WinUI lock actions and keep that surface alive.
            if (OverlayState.Locked)
            {
                LockScreen.WhileLockedTick();
                return;
            }

            // Pick up parent changes (limits/schedule/allow-list) without a restart,
            // roughly twice a minute so the SQLite reads stay cheap.
            if (++_reloadCounter >= ReloadEverySeconds)
            {
                _reloadCounter = 0;
                OverlayState.LoadEnforcement();
            }

            // Count this second of active (unlocked) screen time for usage history.
            OverlayState.RecordActiveSecond();

            // The budget only ticks down when it is the active control, no
            // parent-granted pause is in effect, and the foreground app is not on the
            // allow-list (homework/IDE time is exempt).
            if (OverlayState.LimitEnabled && !OverlayState.IsPaused && !ForegroundExempt())
            {
                OverlayState.Remaining = TimeKeeper.Tick(OverlayState.Remaining);
                if (TimeKeeper.ShouldPersist(OverlayState.Remaining)) OverlayState.Persist();
            }

            // A reopened schedule window clears any parent override.
            if (OverlayState.ScheduleAllows()) OverlayState.ScheduleOverride = false;

            UpdateTray();

            // Repaint without erasing (false): WM_PAINT redraws everything and
            // WM_ERASEBKGND is suppressed, so this stays flicker-free.
            InvalidateRect(hwnd, IntPtr.Zero, false);

            if (OverlayState.ShouldBlock) LockScreen.Show();
        }

        /// <summary>
        /// Consumes a one-shot command left by the passcode-gated tray menu in
        /// Curfew.App and applies it. The command is cleared immediately so it runs
        /// once, and ignored if it is older than a minute (e.g. written while the
        /// overlay was not running).
        /// </summary>
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
                case "pause":
                    OverlayState.PausedUntilUnix = now + PauseDurationSeconds;
                    OverlayLog.Write("tray: paused");
                    break;
                case "resume":
                    OverlayState.PausedUntilUnix = 0;
                    OverlayLog.Write("tray: resumed");
                    break;
                case "quit":
                    // Refuse to quit while the lock is up. DestroyWindow here would
                    // exit the message loop and terminate the process WITHOUT running
                    // LockScreen.Hide(), leaving lock_active=1 and the WinUI lock app
                    // orphaned — the session stays locked-down with no live enforcer
                    // until the watchdog respawns. The command is already consumed
                    // above, so it is dropped (not replayed): the parent can re-issue
                    // quit after unlocking, when teardown runs cleanly.
                    if (OverlayState.Locked)
                    {
                        OverlayLog.Write("tray: quit ignored while locked");
                        break;
                    }
                    OverlayLog.Write("tray: quit requested");
                    DestroyWindow(hwnd); // triggers WM_DESTROY -> tray removal + PostQuitMessage
                    break;
            }
        }

        /// <summary>Adds bonus minutes and lifts any schedule block, as the lock-screen extend does.</summary>
        private static void ApplyExtend(int minutes)
        {
            OverlayState.Remaining = TimeKeeper.Extend(Math.Max(0, OverlayState.Remaining), minutes);
            OverlayState.ScheduleOverride = true;
            OverlayState.Persist();
            OverlayLog.Write($"tray: extended +{minutes} min");
        }

        /// <summary>Refreshes the tray tooltip and raises a balloon at each warning threshold.</summary>
        private static void UpdateTray()
        {
            TrayIcon.UpdateTooltip(
                OverlayState.IsPaused ? Loc.T("tray.paused")
                : OverlayState.LimitEnabled ? Loc.T("tray.left", TimeMath.FormatCompact(OverlayState.Remaining))
                : Loc.T("tray.idle"));

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

        /// <summary>
        /// Paints the whole pill in one pass: panel fill, a colour-coded accent
        /// bar, a small caption and the large remaining-time (or clock) value.
        /// Every GDI object created here is released before returning, and the
        /// DC's original objects are restored.
        /// </summary>
        private static void Paint(IntPtr hwnd)
        {
            var hdc = BeginPaint(hwnd, out var ps);
            GetClientRect(hwnd, out var rect);

            // 1. Solid panel background (single fill = no flicker).
            var bgBrush = CreateSolidBrush(ColorBg);
            FillRect(hdc, ref rect, bgBrush);
            DeleteObject(bgBrush);

            // Decide what to show and which accent colour represents its state.
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

            // 2. Accent bar down the left edge — the at-a-glance status colour.
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

            // Text column: inset from the accent bar, padded on the right.
            var textLeft = rect.left + AccentBarWidth + TextInset;
            var textRight = rect.right - 10;

            // 3. Caption: small, muted label sitting just above the value.
            DrawText(hdc, caption, textLeft, rect.top + 6, textRight, rect.top + 22,
                fontSize: 12, weight: 600, color: ColorLabel,
                format: DT_LEFT | DT_SINGLELINE | DT_VCENTER);

            // 4. The value itself: large, semibold, status-coloured for urgency
            //    (critical = red, warning = amber, otherwise off-white).
            var valueColor = OverlayState.LimitEnabled ? accent : ColorWhite;
            DrawText(hdc, value, textLeft, rect.top + 18, textRight, rect.bottom - 4,
                fontSize: 26, weight: 700, color: valueColor,
                format: DT_LEFT | DT_SINGLELINE | DT_VCENTER);

            EndPaint(hwnd, ref ps);
        }

        /// <summary>
        /// Draws a single line of Segoe UI text into the given rectangle,
        /// creating and releasing the font and restoring the DC's previous font.
        /// </summary>
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

        /// <summary>Accent colour for the remaining budget: white normally,
        /// amber in the last five minutes, red in the final minute.</summary>
        private static uint ColorForRemaining(int seconds)
        {
            if (seconds <= 60) return ColorRed;
            if (seconds <= 300) return ColorAmber;
            return ColorWhite;
        }

        // Invariant culture: must match OverlayState's writer and the store's purge
        // exactly even under a region format with a non-Gregorian calendar.
        private static string RemainingKey(DateOnly date) =>
            $"remaining_time_{OverlayState.CurrentSid}_{date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}";
    }
}

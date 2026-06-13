using System.Runtime.InteropServices;
using Curfew.Core;
using Curfew.Core.Security;
using static Curfew.Overlay.Native;
using static Curfew.Overlay.LockNative;

namespace Curfew.Overlay;

/// <summary>
/// The hard enforcement floor behind the lock: a full-screen black topmost cover
/// and a low-level keyboard hook that swallow the escape shortcuts, plus the
/// logoff countdown. The visible, interactive lock is the WinUI surface
/// (<c>Curfew.App --lock</c>), which this launches on top and relaunches if it
/// dies; this process only enforces and applies the actions the surface records
/// (extend / unlock / ignore-schedule / redeem / provision / logoff). Plain Win32
/// so it starts reliably from the logon scheduled task.
/// </summary>
internal static class LockScreen
{
    private const string ClassName = "CurfewLockClass";

    /// <summary>Solid black cover painted behind the WinUI lock surface.</summary>
    private const uint ColorOverlayBg = 0x00000000;

    private const int TimerReassert = 2;
    private const int TimerCountdown = 3;

    // Keep delegates alive for the lifetime of the process so the GC never
    // collects the thunks that Win32 holds raw pointers to.
    private static readonly WndProc Proc = LockProc;
    private static readonly HookProc Hook = KeyboardHookProc;

    private static IntPtr _hwnd;
    private static IntPtr _hook;

    /// <summary>Seconds until the session is logged off; counts down once a second while locked.</summary>
    private static int _shutdownCountdown = -1;

    // New-user provisioning runs a blocking ConfigClient pipe call that must NOT run
    // on the message-pump thread (it would freeze the keyboard hook and let escape
    // shortcuts leak through). It runs on a background task and the outcome is applied
    // on the next tick. _provisionTask is the in-flight call (null when idle); the
    // overlay is single-threaded so these fields need no locking.
    private static Task<bool>? _provisionTask;
    private static IntPtr _provisionHwnd;

    public static void Register(IntPtr hInstance)
    {
        var wc = new WNDCLASSW
        {
            lpfnWndProc = Proc,
            hInstance = hInstance,
            lpszClassName = ClassName,
            // No class background brush: WM_PAINT fills the cover and WM_ERASEBKGND is
            // swallowed, which eliminates the flash GDI would draw before each repaint.
            hbrBackground = IntPtr.Zero,
        };
        RegisterClassW(ref wc);

        _hwnd = CreateWindowExW(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
            ClassName, "Curfew", WS_POPUP,
            0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN),
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    public static void Show()
    {
        if (_hwnd == IntPtr.Zero || OverlayState.Locked) return;
        OverlayState.Locked = true;
        OverlayLog.Write("lock screen shown");

        if (OverlayState.MiniHwnd != IntPtr.Zero) ShowWindow(OverlayState.MiniHwnd, SW_HIDE);

        _provisionTask = null; // drop any stale outcome from a previous lock
        _shutdownCountdown = OverlayState.Settings.GetInt("lock_screen_timeout", 600);

        // Publish the lock state the WinUI surface reads, then launch it on top. This
        // process stays a plain black cover underneath as the hard enforcement floor:
        // if the surface is slow to appear or has to be relaunched, the screen is still
        // black-locked with the keyboard hook active, so nothing leaks through.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        OverlayState.Settings.Set("lock_reason",
            OverlayState.NewUserBlocked ? "newuser"
            : OverlayState.BudgetBlocked ? "budget" : "schedule");
        OverlayState.Settings.Set("lock_deadline_unix", (now + Math.Max(0, _shutdownCountdown)).ToString());
        OverlayState.Settings.Set("lock_action", string.Empty); // clear any stale action
        OverlayState.Settings.Set("lock_sid", CurrentUserSid());
        OverlayState.Settings.Set("lock_active", "1");
        LockAppHost.Launch();

        EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.Locked,
            OverlayState.BudgetBlocked ? "budget" : "schedule");

        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_SHOWWINDOW | SWP_NOMOVE | SWP_NOSIZE);
        ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);
        SetTaskbarHidden(true);

        SetTimer(_hwnd, new IntPtr(TimerReassert), 500, IntPtr.Zero);
        SetTimer(_hwnd, new IntPtr(TimerCountdown), 1000, IntPtr.Zero);

        if (_hook == IntPtr.Zero)
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, Hook, IntPtr.Zero, 0);
    }

    private static void Hide()
    {
        OverlayState.Locked = false;

        // Tell the WinUI surface to exit and stop relaunching it.
        OverlayState.Settings.Set("lock_active", "0");
        LockAppHost.Kill();

        KillTimer(_hwnd, new IntPtr(TimerReassert));
        KillTimer(_hwnd, new IntPtr(TimerCountdown));
        ShowWindow(_hwnd, SW_HIDE);
        SetTaskbarHidden(false);

        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }

        OverlayState.Persist();
        if (OverlayState.MiniHwnd != IntPtr.Zero) ShowWindow(OverlayState.MiniHwnd, SW_SHOWNOACTIVATE);
    }

    /// <summary>The current session user's SID, for the service's Task Manager lockdown.</summary>
    private static string CurrentUserSid()
    {
        try { return System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Per-second work while the lock is up: apply any action the WinUI surface
    /// recorded, then keep that surface alive — relaunch it if it died, so the
    /// interactive WinUI card is always the lock the user sees. The black cover and
    /// keyboard hook hold enforcement underneath regardless. Called from the overlay tick.
    /// </summary>
    public static void WhileLockedTick()
    {
        // Apply any background provisioning that finished since the last tick before it
        // can tear the lock down.
        ApplyProvisionResult();

        ConsumeLockAction();

        if (!OverlayState.Locked) return;

        // The WinUI surface IS the lock UI. If it ever dies, relaunch it; the cover
        // keeps the session black-locked in the gap so nothing leaks through.
        if (!LockAppHost.IsRunning) LockAppHost.Launch();
    }

    /// <summary>
    /// Applies a one-shot action the WinUI lock recorded after verifying the
    /// passcode/code. Cleared immediately (runs once) and ignored if stale. A child
    /// forging this key would need write access to the settings DB — the same exposure
    /// as the tray command, closed by the DB-ACL work.
    /// </summary>
    private static void ConsumeLockAction()
    {
        var action = OverlayState.Settings.Get("lock_action");
        if (string.IsNullOrEmpty(action)) return;

        OverlayState.Settings.Set("lock_action", string.Empty); // consume once
        long.TryParse(OverlayState.Settings.Get("lock_action_at"), out var at);
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - at > 60) return; // stale

        switch (action)
        {
            case "extend15": ExtendApply(15); break;
            case "extend30": ExtendApply(30); break;
            case "extend60": ExtendApply(60); break;
            case "unlock":
                OverlayState.ScheduleOverride = true;
                EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.Unlocked, "passcode");
                if (!OverlayState.ShouldBlock) Hide();
                break;
            case "ignore_schedule":
                OverlayState.IgnoreScheduleUntilRestart = true;
                OverlayState.ScheduleOverride = true;
                EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.ScheduleIgnored, "until restart");
                if (!OverlayState.ShouldBlock) Hide();
                break;
            case "redeem":
                var code = OverlayState.Settings.Get("lock_code") ?? string.Empty;
                OverlayState.Settings.Set("lock_code", string.Empty);
                if (TryRedeemCode(code))
                {
                    EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.Extended, "unlock code");
                    if (!OverlayState.ShouldBlock) Hide();
                }
                break;
            case "provision":
                // Activate this Windows user via the SYSTEM service (device code or
                // parent passcode). The pipe call blocks, so it runs on a background
                // thread and the outcome is applied on the next tick.
                var provCode = OverlayState.Settings.Get("lock_code") ?? string.Empty;
                OverlayState.Settings.Set("lock_code", string.Empty);
                StartProvision(_hwnd, provCode);
                break;
            case "logoff":
                LockNative.Logoff();
                break;
        }
    }

    private static void ExtendApply(int minutes)
    {
        OverlayState.Remaining = TimeKeeper.Extend(Math.Max(0, OverlayState.Remaining), minutes);
        OverlayState.ScheduleOverride = true;
        OverlayState.Persist();
        EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.Extended, $"+{minutes} min");
        if (!OverlayState.ShouldBlock) Hide();
    }

    /// <summary>
    /// Redeems a valid offline unlock code (TOTP): grants the configured bonus minutes,
    /// lifts a schedule block and records the code's time step so it cannot be replayed.
    /// Returns false when no secret is configured or the code is wrong/reused.
    /// </summary>
    private static bool TryRedeemCode(string entered)
    {
        var secret = OverlayState.Settings.Get("unlock_secret");
        if (string.IsNullOrWhiteSpace(secret)) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var minCounter = long.TryParse(OverlayState.Settings.Get("unlock_last_counter"), out var last)
            ? last
            : long.MinValue;

        // window=10 (±5 min) keeps a code the parent reads aloud valid long enough for
        // the child to enter it, even as the authenticator app rotates it. Replay is
        // still blocked because minCounter advances past each redeemed step.
        if (!UnlockCode.Verify(secret, entered, now, 10, minCounter, out var matched))
            return false;

        OverlayState.Settings.Set("unlock_last_counter", matched.ToString());
        var bonus = OverlayState.Settings.GetInt("unlock_bonus_minutes", 30);
        OverlayState.Remaining = TimeKeeper.Extend(Math.Max(0, OverlayState.Remaining), bonus);
        OverlayState.ScheduleOverride = true;
        OverlayState.Persist();
        OverlayLog.Write($"unlock code redeemed (+{bonus} min)");
        return true;
    }

    /// <summary>
    /// Kicks off the new-user provisioning pipe call (device code or parent passcode)
    /// on a background thread so the pump/keyboard hook keep running, then resets or
    /// records the lockout counter on the same background thread. The outcome is applied
    /// by <see cref="ApplyProvisionResult"/> on the next tick. One at a time: a second
    /// request while one is in flight is ignored.
    /// </summary>
    private static void StartProvision(IntPtr hwnd, string code)
    {
        if (_provisionTask is { IsCompleted: false }) return;
        _provisionHwnd = hwnd;
        _provisionTask = Task.Run(() =>
        {
            try
            {
                if (ConfigClient.Provision(OverlayState.CurrentSid, code))
                {
                    ConfigClient.ResetFailures(code);
                    return true;
                }
                ConfigClient.RecordFailure();
                return false;
            }
            catch { return false; }
        });
    }

    /// <summary>
    /// Applies a completed background provision on the pump thread: on success logs the
    /// activation and tears the lock down. On failure the lock simply stays up and the
    /// WinUI surface is relaunched (by <see cref="WhileLockedTick"/>) so the user can
    /// retry — the surface verified the code locally before sending it, so a failure
    /// here means the service rejected it (typically momentarily unavailable).
    /// </summary>
    private static void ApplyProvisionResult()
    {
        if (_provisionTask is not { IsCompleted: true } task) return;
        var ok = task.Result;
        _provisionTask = null;

        if (ok)
        {
            EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.Unlocked, "user activated");
            if (!OverlayState.ShouldBlock) Hide();
        }
    }

    private static IntPtr LockProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                // Suppress default background erase; WM_PAINT fills the whole cover.
                return new IntPtr(1);

            case WM_PAINT:
                PaintCover(hwnd);
                return IntPtr.Zero;

            case WM_TIMER:
                HandleTimer(hwnd, (int)(long)wParam);
                return IntPtr.Zero;

            case WM_CLOSE: // never close — the lock owns the session until unlocked
                return IntPtr.Zero;

            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private static void HandleTimer(IntPtr hwnd, int id)
    {
        if (id == TimerReassert)
        {
            // Stay clamped to the top of the Z-order — but NOT while the WinUI surface
            // is up: re-topmosting would slam the cover over it. When the surface is
            // absent the cover IS the visible lock, so keep it on top.
            if (!LockAppHost.IsRunning)
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            // Keep the shell taskbar down: it re-asserts itself topmost on shell events
            // and would otherwise float above the lock and be clickable.
            SetTaskbarHidden(true);
        }
        else if (id == TimerCountdown)
        {
            // The WinUI surface shows the countdown (it reads lock_deadline_unix); this
            // process owns enforcement, so it drives the actual logoff when it reaches zero.
            if (_shutdownCountdown > 0) _shutdownCountdown--;
            else if (_shutdownCountdown == 0) LockNative.Logoff();
        }
    }

    private static void PaintCover(IntPtr hwnd)
    {
        var hdc = BeginPaint(hwnd, out var ps);
        GetClientRect(hwnd, out var client);
        FillSolid(hdc, client.left, client.top, client.right - client.left, client.bottom - client.top, ColorOverlayBg);
        EndPaint(hwnd, ref ps);
    }

    private static void FillSolid(IntPtr hdc, int x, int y, int w, int h, uint color)
    {
        var brush = CreateSolidBrush(color);
        var rect = new RECT { left = x, top = y, right = x + w, bottom = y + h };
        FillRect(hdc, ref rect, brush);
        DeleteObject(brush);
    }

    /// <summary>
    /// Hides or restores the shell taskbar (<c>Shell_TrayWnd</c>) while the lock is up.
    /// The full-screen cover and the WinUI surface are both topmost, but the taskbar is
    /// topmost too and re-asserts itself above them on shell events — leaving it
    /// visible and clickable (a mouse route to the Start menu / other apps that the
    /// keyboard hook can't block). Taking it out of the picture entirely is the
    /// deterministic fix; it is restored when the lock is dismissed. Re-applied on each
    /// reassert tick in case the shell re-shows it.
    /// </summary>
    private static void SetTaskbarHidden(bool hidden)
    {
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero) ShowWindow(tray, hidden ? SW_HIDE : SW_SHOW);
    }

    private static IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HC_ACTION && OverlayState.Locked)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var vk = (int)info.vkCode;
            var alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            var win = ((GetAsyncKeyState(VK_LWIN) | GetAsyncKeyState(VK_RWIN)) & 0x8000) != 0;
            var block = vk is VK_ESCAPE or VK_LWIN or VK_RWIN
                        || (alt && vk is VK_F4 or VK_TAB)
                        || (win && vk is VK_D or VK_M);
            if (block) return new IntPtr(1);
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }
}

using System.Runtime.InteropServices;
using Curfew.Core;
using Curfew.Core.Security;
using static Curfew.Overlay.Native;
using static Curfew.Overlay.LockNative;

namespace Curfew.Overlay;

/// <summary>hard floor behind lock: black topmost cover + keyboard hook + logoff countdown. visible lock is WinUI surface (<c>Curfew.App --lock</c>) launched/relaunched on top; this only enforces + applies its actions (extend/unlock/ignore-schedule/redeem/logoff). plain Win32 for reliable logon-task start</summary>
internal static class LockScreen
{
    private const string ClassName = "CurfewLockClass";

    /// <summary>solid black cover behind WinUI lock surface</summary>
    private const uint ColorOverlayBg = 0x00000000;

    private const int TimerReassert = 2;
    private const int TimerCountdown = 3;

    // keep delegates alive for process life so GC won't collect thunks Win32 holds raw pointers to
    private static readonly WndProc Proc = LockProc;
    private static readonly HookProc Hook = KeyboardHookProc;

    private static IntPtr _hwnd;
    private static IntPtr _hook;

    /// <summary>seconds until logoff; counts down once a second while locked</summary>
    private static int _shutdownCountdown = -1;

    // new-user setup = blocking ConfigClient.Provision pipe call; must NOT run on pump thread (would freeze hook, leak escape shortcuts). runs on background task, outcome applied next tick. _provisionTask = in-flight call (null=idle); single-threaded so no locking
    private static Task<bool>? _provisionTask;

    public static void Register(IntPtr hInstance)
    {
        var wc = new WNDCLASSW
        {
            lpfnWndProc = Proc,
            hInstance = hInstance,
            lpszClassName = ClassName,
            // no class bg brush: WM_PAINT fills cover, WM_ERASEBKGND swallowed -> no pre-repaint flash
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

        _shutdownCountdown = OverlayState.Settings.GetInt("lock_screen_timeout", 600);

        // publish lock state for WinUI surface, launch on top. this stays black cover underneath as hard floor: slow/relaunched surface still black-locked + hook active, nothing leaks
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        OverlayState.Settings.Set("lock_reason",
            OverlayState.NewUserBlocked ? "newuser"
            : OverlayState.BudgetBlocked || OverlayState.WeeklyBlocked ? "budget" : "schedule");
        OverlayState.Settings.Set("lock_deadline_unix", (now + Math.Max(0, _shutdownCountdown)).ToString());
        OverlayState.Settings.Set("lock_action", string.Empty); // clear stale action
        OverlayState.Settings.Set("lock_sid", CurrentUserSid());
        PublishBreakOffer();
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

        // tell WinUI surface to exit, stop relaunching
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

    /// <summary>current session user SID, for service Task Manager lockdown</summary>
    private static string CurrentUserSid()
    {
        try { return System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>per-second work while locked: apply WinUI surface action, keep surface alive (relaunch if died). black cover + hook enforce underneath. called from overlay tick</summary>
    public static void WhileLockedTick()
    {
        // apply background new-user setup finished since last tick, before it tears lock down
        ApplyProvisionResult();

        ConsumeLockAction();

        if (!OverlayState.Locked) return;

        // keep the lock's break offer current as the budget/cooldown change while it sits up
        PublishBreakOffer();

        // WinUI surface IS the lock UI; if dies relaunch. cover black-locks the gap, nothing leaks
        if (!LockAppHost.IsRunning) LockAppHost.Launch();
    }

    /// <summary>apply one-shot action WinUI lock recorded after verifying passcode/code. cleared immediately (runs once), ignored if stale. forging it needs settings DB write -- same exposure as tray command, closed by DB-ACL work</summary>
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
                OverlayState.WeeklyOverride = true; // parent authorized more time past the weekly cap this session
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
                // new-user setup: surface verified parent PIN + chose daily limit (min). hand both to service on bg thread (re-verifies, writes per-user limit, marks set up); outcome lands in ApplyProvisionResult
                var provCode = OverlayState.Settings.Get("lock_code") ?? string.Empty;
                OverlayState.Settings.Set("lock_code", string.Empty);
                int.TryParse(OverlayState.Settings.Get("lock_setup_limit"), out var provLimit);
                OverlayState.Settings.Set("lock_setup_limit", string.Empty);
                StartProvision(provCode, provLimit);
                break;
            case "break":
                // ungated child self-service: spend remaining daily break budget for bonus minutes.
                // no passcode — abuse is bounded by the pause policy (budget + cooldown) in TryRedeemBreak.
                var grantedSeconds = OverlayState.TryRedeemBreak();
                if (grantedSeconds > 0)
                {
                    EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.BreakTaken, $"+{grantedSeconds / 60} min");
                    if (!OverlayState.ShouldBlock) Hide();
                }
                break;
            case "logoff":
                LockNative.Logoff();
                break;
        }
    }

    /// <summary>Publish the break time (whole minutes) the child may redeem now, so the lock surface can show
    /// or hide its "Take a break" button. Only offered for a pure budget block; a schedule or new-user lock
    /// is not something a break can lift, so it publishes 0 there.</summary>
    private static void PublishBreakOffer()
    {
        var minutes = OverlayState.BudgetBlocked && !OverlayState.WeeklyBlocked
                      && !OverlayState.ScheduleBlocked && !OverlayState.NewUserBlocked
            ? OverlayState.BreakOfferSeconds() / 60
            : 0;
        OverlayState.Settings.Set("lock_break_minutes", minutes.ToString());
    }

    /// <summary>run new-user setup pipe call off pump thread, reset/record lockout counter on same bg thread. one at a time; second request while in-flight ignored</summary>
    private static void StartProvision(string code, int limitMinutes)
    {
        if (_provisionTask is { IsCompleted: false }) return;
        _provisionTask = Task.Run(() =>
        {
            try
            {
                if (ConfigClient.Provision(OverlayState.CurrentSid, code, limitMinutes))
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

    /// <summary>apply completed new-user setup on pump thread: success -> re-read enforcement (budget seeds new per-user limit) + tear lock down. failure -> lock stays, surface relaunched by <see cref="WhileLockedTick"/> for retry</summary>
    private static void ApplyProvisionResult()
    {
        if (_provisionTask is not { IsCompleted: true } task) return;
        var ok = task.Result;
        _provisionTask = null;

        if (ok)
        {
            OverlayState.LoadEnforcement();
            EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.Unlocked, "user set up");
            if (!OverlayState.ShouldBlock) Hide();
        }
    }

    private static void ExtendApply(int minutes)
    {
        OverlayState.Remaining = TimeKeeper.Extend(Math.Max(0, OverlayState.Remaining), minutes);
        OverlayState.ScheduleOverride = true;
        OverlayState.WeeklyOverride = true; // granted minutes must be usable past the weekly cap too
        OverlayState.Persist();
        EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.Extended, $"+{minutes} min");
        if (!OverlayState.ShouldBlock) Hide();
    }

    /// <summary>redeem valid offline unlock code (TOTP): grant bonus minutes, lift schedule block, record time step so no replay. false if no secret or code wrong/reused</summary>
    private static bool TryRedeemCode(string entered)
    {
        var secret = OverlayState.Settings.Get("unlock_secret");
        if (string.IsNullOrWhiteSpace(secret)) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var minCounter = long.TryParse(OverlayState.Settings.Get("unlock_last_counter"), out var last)
            ? last
            : long.MinValue;

        // window=10 (+/-5 min) keeps parent-read code valid long enough to enter despite rotation. replay blocked: minCounter advances past each redeemed step
        if (!UnlockCode.Verify(secret, entered, now, 10, minCounter, out var matched))
            return false;

        OverlayState.Settings.Set("unlock_last_counter", matched.ToString());
        var bonus = OverlayState.Settings.GetInt("unlock_bonus_minutes", 30);
        OverlayState.Remaining = TimeKeeper.Extend(Math.Max(0, OverlayState.Remaining), bonus);
        // lift every session-scoped block the granted time should bypass, exactly like ExtendApply and the
        // passcode "unlock": schedule (bedtime) AND the weekly cap. Without WeeklyOverride a redeemed code
        // while weekly-capped added minutes but left WeeklyBlocked true, so the lock never came down and the
        // ticket looked dead whenever a weekly limit was set.
        OverlayState.ScheduleOverride = true;
        OverlayState.WeeklyOverride = true;
        OverlayState.Persist();
        OverlayLog.Write($"unlock code redeemed (+{bonus} min)");
        return true;
    }

    private static IntPtr LockProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                // suppress default erase; WM_PAINT fills whole cover
                return new IntPtr(1);

            case WM_PAINT:
                PaintCover(hwnd);
                return IntPtr.Zero;

            case WM_TIMER:
                HandleTimer(hwnd, (int)(long)wParam);
                return IntPtr.Zero;

            case WM_CLOSE: // never close -- lock owns session until unlocked
                return IntPtr.Zero;

            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private static void HandleTimer(IntPtr hwnd, int id)
    {
        if (id == TimerReassert)
        {
            // clamp to top of Z-order, but NOT while WinUI surface up (would slam cover over it). surface absent -> cover IS visible lock, keep on top
            if (!LockAppHost.IsRunning)
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

            // keep shell taskbar down: re-asserts topmost on shell events, would float above lock + be clickable
            SetTaskbarHidden(true);
        }
        else if (id == TimerCountdown)
        {
            // surface shows countdown (reads lock_deadline_unix); this owns enforcement, drives actual logoff at zero
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

    /// <summary>hide/restore shell taskbar (<c>Shell_TrayWnd</c>) while locked. taskbar is topmost too + re-asserts above cover/surface on shell events -> clickable mouse route to Start that hook can't block. taking it out is the deterministic fix; restored on dismiss, re-applied each reassert tick</summary>
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

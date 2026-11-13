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
            GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN),
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
        ReportLockState(true);
        PublishBreakOffer();
        OverlayState.Settings.Set("lock_active", "1");
        LockAppHost.Launch();

        EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.Locked,
            OverlayState.BudgetBlocked ? "budget" : "schedule");

        CoverAllDisplays(_hwnd, SWP_SHOWWINDOW);
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
        ReportLockState(false);
        LockAppHost.Kill();

        KillTimer(_hwnd, new IntPtr(TimerReassert));
        KillTimer(_hwnd, new IntPtr(TimerCountdown));
        ShowWindow(_hwnd, SW_HIDE);
        SetTaskbarHidden(false);

        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }

        OverlayState.Persist();
        if (OverlayState.MiniHwnd != IntPtr.Zero) ShowWindow(OverlayState.MiniHwnd, SW_SHOWNOACTIVATE);
    }

    /// <summary>tell the service (authenticated pipe; it takes our SID from the token) whether we're locked, for the Task Manager lockdown. off the pump thread</summary>
    public static void ReportLockState(bool active) =>
        Task.Run(() => { try { ConfigClient.LockState(active); } catch { /* best effort */ } });

    /// <summary>tear the lock down once a grant (or reload) leaves nothing blocking</summary>
    public static void HideIfUnblocked()
    {
        if (OverlayState.Locked && !OverlayState.ShouldBlock) Hide();
    }

    /// <summary>per-second work while locked: apply WinUI surface action, keep surface alive (relaunch if died). black cover + hook enforce underneath. called from overlay tick</summary>
    public static void WhileLockedTick()
    {
        if (!OverlayState.Locked) return;

        // keep the lock's break offer current as the budget/cooldown change while it sits up
        PublishBreakOffer();

        // WinUI surface IS the lock UI; if dies relaunch. cover black-locks the gap, nothing leaks
        if (!LockAppHost.IsRunning) LockAppHost.Launch();
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

    /// <summary>size cover to the whole virtual desktop (every display), topmost. persistent fallback must block
    /// secondary displays too, even while the WinUI lock surface (and its per-monitor covers) is being relaunched</summary>
    private static void CoverAllDisplays(IntPtr hwnd, uint flags) =>
        SetWindowPos(hwnd, HWND_TOPMOST,
            GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN), flags);

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
                // re-fit every tick: monitors plugged in / rearranged while locked must be covered too
                CoverAllDisplays(hwnd, SWP_NOACTIVATE);

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

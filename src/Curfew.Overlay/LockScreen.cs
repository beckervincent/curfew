using System.Runtime.InteropServices;
using Curfew.Core;
using Curfew.Core.Localization;
using Curfew.Core.Security;
using static Curfew.Overlay.Native;
using static Curfew.Overlay.LockNative;

namespace Curfew.Overlay;

/// <summary>
/// Full-screen passcode lock shown when time runs out. Plain Win32 so it starts
/// reliably from the scheduled task. Blocks escape shortcuts, can extend time
/// (passcode required) and shuts down after a countdown.
/// </summary>
internal static class LockScreen
{
    private const string ClassName = "CurfewLockClass";

    // GDI colours are 0x00BBGGRR (COLORREF byte order is R, G, B low-to-high).
    // Palette mirrors the WinUI dark theme so the lock reads as part of the app.
    private const uint ColorOverlayBg = 0x00000000;  // completely black backdrop
    private const uint ColorPanelBg = 0x002B2B2B;    // card surface, ~CardBackgroundFillColorDefault (#2B2B2B)
    private const uint ColorPanelEdge = 0x00383838;  // subtle 1px card stroke (#383838)
    private const uint ColorPanelShadow = 0x00121212; // soft drop-shadow band under the card
    private const uint ColorWhite = 0x00FFFFFF;
    private const uint ColorLight = 0x00C8C8C8;       // body text (#C8C8C8)
    private const uint ColorMuted = 0x008A8A8A;       // captions / hints (#8A8A8A)
    // Brand accent #54ADF2 (matches the WinUI ScheduleGrid selection), in BGR.
    private const uint ColorAccent = 0x00F2AD54;
    private const uint ColorAccentPressed = 0x00C68A3F; // accent button while pressed (dimmer #3F8AC6)
    private const uint ColorWarn = 0x000A7FFF;        // amber warning (BGR)
    private const uint ColorRed = 0x000000FF;         // urgent red
    private const uint ColorFieldBg = 0x00202020;     // passcode field well (#202020)
    // Neutral (secondary) button surface, ~ControlFillColorDefault on dark.
    private const uint ColorBtn = 0x00333333;         // #333333
    private const uint ColorBtnPressed = 0x00292929;  // #292929 while pressed
    private const uint ColorBtnBorder = 0x00454545;   // #454545 1px stroke

    // Corner radii mirror the WinUI tokens: ControlCornerRadius 6, OverlayCornerRadius 8.
    // RoundRect takes the full ellipse diameter, hence 2× the logical radius.
    private const int ControlCornerDiameter = 12;
    private const int CardCornerDiameter = 16;

    // Card-relative vertical layout (offsets from the card's top edge). Shared by
    // CreateControls (child windows) and PaintLock (text) so they stay aligned.
    private const int ExtendRowTop = 226;
    private const int FieldTop = 314;
    private const int FieldWidth = 300;
    private const int FieldHeight = 46;
    private const int ActionWidth = 380;
    private const int ActionHeight = 46;
    private const int UnlockTop = 372;
    private const int ShutdownTop = 430;

    private const int IdEdit = 101;
    private const int IdUnlock = 102;
    private const int IdExtend15 = 103;
    private const int IdExtend30 = 104;
    private const int IdExtend60 = 105;
    private const int IdShutdown = 106;

    private const int TimerReassert = 2;
    private const int TimerCountdown = 3;

    // Card geometry (logical pixels). Centred on the primary monitor.
    private const int PanelWidth = 560;
    private const int PanelHeight = 524;

    // Keep delegates alive for the lifetime of the process so the GC never
    // collects the thunks that Win32 holds raw pointers to.
    private static readonly WndProc Proc = LockProc;
    private static readonly HookProc Hook = KeyboardHookProc;

    private static IntPtr _hwnd;
    private static IntPtr _edit;
    private static IntPtr _hook;

    // Toggleable child controls, captured at creation so Show() can switch the
    // action area between budget mode (extend time) and schedule mode (allow now).
    private static IntPtr _btnExtend15;
    private static IntPtr _btnExtend30;
    private static IntPtr _btnExtend60;
    private static IntPtr _btnUnlock;
    private static IntPtr _btnShutdown;

    /// <summary>True while the current lock is a schedule block (outside allowed hours), not a budget block.</summary>
    private static bool _scheduleMode;

    /// <summary>
    /// True while the WinUI lock surface (Curfew.App --lock) is driving the UI on
    /// top of this black cover. False means the built-in GDI lock is the fallback
    /// (the WinUI app could not be launched), in which case the GDI controls show.
    /// </summary>
    private static bool _winui;

    /// <summary>Consecutive failed WinUI-lock (re)launches before falling back to GDI.</summary>
    private const int MaxWinuiRelaunches = 2;
    private static int _winuiRelaunchCount;

    // Cached fonts/brushes created once and reused on every paint, then freed in
    // Dispose. Reusing GDI handles avoids per-frame churn and keeps the countdown
    // repaint cheap (it fires once a second).
    private static IntPtr _fontTitle;
    private static IntPtr _fontCountdown;
    private static IntPtr _fontBody;
    private static IntPtr _fontCaption;
    private static IntPtr _fontError;
    private static IntPtr _fontControl;

    // Brush returned from WM_CTLCOLOREDIT to paint the passcode field's background
    // dark instead of the default system white. Created once, freed in Dispose.
    private static IntPtr _brushField;

    private static int _shutdownCountdown = -1;
    private static bool _error;

    public static void Register(IntPtr hInstance)
    {
        var wc = new WNDCLASSW
        {
            lpfnWndProc = Proc,
            hInstance = hInstance,
            lpszClassName = ClassName,
            // No class background brush: we paint the whole client area in
            // WM_PAINT and swallow WM_ERASEBKGND, which eliminates the flash of
            // background that GDI would otherwise draw before each repaint.
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

        _error = false;
        _shutdownCountdown = OverlayState.Settings.GetInt("lock_screen_timeout", 600);

        // Schedule block (outside allowed hours) when the budget is not the cause.
        // The add-time buttons are shown in both modes: granting time also overrides
        // the schedule, so the added minutes are usable even during a curfew block.
        // Only the primary action's label changes between the two modes.
        _scheduleMode = !OverlayState.BudgetBlocked;
        SetWindowTextW(_btnUnlock, _scheduleMode ? Loc.T("lock.schedule.ignore") : Loc.T("lock.unlock"));

        // Publish the lock state for the WinUI surface and launch it on top of this
        // black cover. If it cannot start, fall back to the built-in GDI controls so
        // there is always a way to unlock.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        OverlayState.Settings.Set("lock_reason", OverlayState.BudgetBlocked ? "budget" : "schedule");
        OverlayState.Settings.Set("lock_deadline_unix", (now + Math.Max(0, _shutdownCountdown)).ToString());
        OverlayState.Settings.Set("lock_action", string.Empty); // clear any stale action
        OverlayState.Settings.Set("lock_sid", CurrentUserSid());
        OverlayState.Settings.Set("lock_active", "1");
        _winuiRelaunchCount = 0;
        _winui = LockAppHost.Launch();
        SetControlsVisible(!_winui);

        EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.Locked,
            OverlayState.BudgetBlocked ? "budget" : "schedule");

        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_SHOWWINDOW | SWP_NOMOVE | SWP_NOSIZE);
        ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);
        if (_edit != IntPtr.Zero && !_winui)
        {
            SetWindowTextW(_edit, "");
            SetFocus(_edit);
        }

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

    private static string EnteredText()
    {
        if (_edit == IntPtr.Zero) return string.Empty;
        var buffer = new char[16];
        var len = GetWindowTextW(_edit, buffer, buffer.Length);
        return new string(buffer, 0, Math.Clamp(len, 0, buffer.Length));
    }

    private static bool PasscodeMatches() =>
        PasscodeHash.Verify(EnteredText(), OverlayState.Settings.Get("passcode"));

    /// <summary>
    /// Redeems a valid offline unlock code (TOTP): grants the configured bonus
    /// minutes, lifts a schedule block and records the code's time step so it
    /// cannot be replayed. Returns false when no secret is configured or the code
    /// is wrong/reused.
    /// </summary>
    private static bool TryRedeemUnlockCode() => TryRedeemCode(EnteredText());

    private static bool TryRedeemCode(string entered)
    {
        var secret = OverlayState.Settings.Get("unlock_secret");
        if (string.IsNullOrWhiteSpace(secret)) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var minCounter = long.TryParse(OverlayState.Settings.Get("unlock_last_counter"), out var last)
            ? last
            : long.MinValue;

        // window=10 (±5 min) keeps a code the parent reads aloud valid long enough
        // for the child to enter it, even as the authenticator app rotates it.
        // Replay is still blocked because minCounter advances past each redeemed step.
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
    /// Per-second work while the lock is up: apply any action the WinUI surface
    /// recorded, and relaunch that surface if it died (falling back to the GDI
    /// controls if it cannot be relaunched). Called from the overlay tick.
    /// </summary>
    public static void WhileLockedTick()
    {
        ConsumeLockAction();

        if (!_winui || !OverlayState.Locked) return;

        if (LockAppHost.IsRunning)
        {
            // Healthy — reset the relaunch budget so only *consecutive* failures count.
            _winuiRelaunchCount = 0;
            return;
        }

        // The surface died. Relaunch a bounded number of times; if it will not stay
        // up (or cannot launch at all), fall back to the GDI controls so the lock can
        // never be a dead black screen with no unlock path.
        if (_winuiRelaunchCount++ >= MaxWinuiRelaunches || !LockAppHost.Launch())
        {
            OverlayLog.Write("WinUI lock unavailable; falling back to GDI lock");
            _winui = false;
            SetControlsVisible(true);
            InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    /// <summary>
    /// Applies a one-shot action the WinUI lock recorded after verifying the
    /// passcode/code. Cleared immediately (runs once) and ignored if stale. A child
    /// forging this key would need write access to the settings DB — the same
    /// pre-existing exposure as the tray command; closed only by the DB-ACL work.
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

    /// <summary>Shows or hides the GDI fallback controls (the WinUI surface hides them).</summary>
    private static void SetControlsVisible(bool visible)
    {
        var cmd = visible ? SW_SHOW : SW_HIDE;
        ShowWindow(_btnExtend15, cmd);
        ShowWindow(_btnExtend30, cmd);
        ShowWindow(_btnExtend60, cmd);
        ShowWindow(_edit, cmd);
        ShowWindow(_btnUnlock, cmd);
        ShowWindow(_btnShutdown, cmd);
    }

    private static void ClearEdit()
    {
        if (_edit != IntPtr.Zero) SetWindowTextW(_edit, "");
    }

    private static IntPtr LockProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CREATE:
                CreateFonts();
                CreateControls(hwnd);
                return IntPtr.Zero;

            case WM_ERASEBKGND:
                // Suppress default background erase; WM_PAINT fills everything.
                // Returning non-zero tells Win32 the background is handled and
                // prevents the white/flush flicker on each invalidate.
                return new IntPtr(1);

            case WM_PAINT:
                PaintLock(hwnd);
                return IntPtr.Zero;

            case WM_COMMAND:
                HandleCommand(hwnd, (int)((long)wParam & 0xFFFF), (int)(((long)wParam >> 16) & 0xFFFF));
                return IntPtr.Zero;

            case WM_CTLCOLOREDIT:
                // Paint the passcode field dark: light text on the field-well colour.
                // wParam is the edit's HDC; returning the matching brush makes Win32
                // use it for the control background.
                SetTextColor(wParam, ColorWhite);
                SetBkColor(wParam, ColorFieldBg);
                return _brushField;

            case WM_DRAWITEM:
                DrawOwnerButton(lParam);
                return new IntPtr(1);

            case WM_TIMER:
                HandleTimer(hwnd, (int)(long)wParam);
                return IntPtr.Zero;

            case WM_DESTROY:
                Dispose();
                return IntPtr.Zero;

            case WM_CLOSE: // never close — the lock owns the session until unlocked
                return IntPtr.Zero;

            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private static void CreateFonts()
    {
        _fontTitle = MakeFont(40, 700);
        _fontCountdown = MakeFont(26, 700);
        _fontBody = MakeFont(18, 400);
        _fontCaption = MakeFont(13, 600);
        _fontError = MakeFont(15, 700);
        _fontControl = MakeFont(16, 600);
        _brushField = CreateSolidBrush(ColorFieldBg);
    }

    private static IntPtr MakeFont(int height, int weight) =>
        // CLEARTYPE_QUALITY (5) for crisp anti-aliased Segoe UI text.
        CreateFontW(height, 0, 0, 0, weight, 0, 0, 0, 0, 0, 0, 5, 0, "Segoe UI");

    private static void Dispose()
    {
        DeleteFont(ref _fontTitle);
        DeleteFont(ref _fontCountdown);
        DeleteFont(ref _fontBody);
        DeleteFont(ref _fontCaption);
        DeleteFont(ref _fontError);
        DeleteFont(ref _fontControl);
        if (_brushField != IntPtr.Zero) { DeleteObject(_brushField); _brushField = IntPtr.Zero; }
    }

    private static void DeleteFont(ref IntPtr font)
    {
        if (font != IntPtr.Zero) { DeleteObject(font); font = IntPtr.Zero; }
    }

    // Panel-relative layout. Returns the card origin and exposes a running
    // vertical cursor helper so the controls (WM_CREATE) and the painted text
    // (WM_PAINT) stay perfectly aligned from a single source of truth.
    private static (int px, int py) PanelOrigin()
    {
        var sw = GetSystemMetrics(SM_CXSCREEN);
        var sh = GetSystemMetrics(SM_CYSCREEN);
        return ((sw - PanelWidth) / 2, (sh - PanelHeight) / 2);
    }

    private static void CreateControls(IntPtr hwnd)
    {
        var hInstance = GetModuleHandleW(null);
        var (px, py) = PanelOrigin();
        var cx = px + PanelWidth / 2;

        // Add-time row: +15 / +30 / +1 h. Shown in both budget and schedule mode —
        // granting time also overrides the schedule (see Extend), so the added time
        // is actually usable even when the lock is a curfew block.
        const int extW = 150, extH = 40, gap = 14;
        var rowW = extW * 3 + gap * 2;
        var rowX = cx - rowW / 2;
        var extY = py + ExtendRowTop;
        _btnExtend15 = AddButton(hwnd, hInstance, Loc.T("lock.extend.minutes", 15), IdExtend15, rowX, extY, extW, extH);
        _btnExtend30 = AddButton(hwnd, hInstance, Loc.T("lock.extend.minutes", 30), IdExtend30, rowX + extW + gap, extY, extW, extH);
        _btnExtend60 = AddButton(hwnd, hInstance, Loc.T("lock.extend.hour"), IdExtend60, rowX + (extW + gap) * 2, extY, extW, extH);

        // Passcode field: borderless EDIT over a painted rounded well (PaintLock),
        // with the interior filled dark via WM_CTLCOLOREDIT. No ES_NUMBER — the
        // passcode may be a PIN or a full password; an offline unlock code also fits.
        _edit = CreateWindowExW(
            0, "EDIT", "",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_CENTER | ES_PASSWORD,
            cx - FieldWidth / 2, py + FieldTop, FieldWidth, FieldHeight,
            hwnd, new IntPtr(IdEdit), hInstance, IntPtr.Zero);
        SendMessageW(_edit, EM_SETLIMITTEXT, new IntPtr(64), IntPtr.Zero);
        if (_fontControl != IntPtr.Zero)
            SendMessageW(_edit, WM_SETFONT, _fontControl, new IntPtr(1));

        // Primary action (accent) then the destructive shutdown action below it.
        var actX = cx - ActionWidth / 2;
        _btnUnlock = AddButton(hwnd, hInstance, Loc.T("lock.unlock"), IdUnlock, actX, py + UnlockTop, ActionWidth, ActionHeight);
        _btnShutdown = AddButton(hwnd, hInstance, Loc.T("lock.shutdown"), IdShutdown, actX, py + ShutdownTop, ActionWidth, ActionHeight);
    }

    private static IntPtr AddButton(IntPtr parent, IntPtr hInstance, string text, int id, int x, int y, int w, int h)
    {
        var btn = CreateWindowExW(0, "BUTTON", text,
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
            x, y, w, h, parent, new IntPtr(id), hInstance, IntPtr.Zero);
        if (btn != IntPtr.Zero && _fontControl != IntPtr.Zero)
            SendMessageW(btn, WM_SETFONT, _fontControl, new IntPtr(1));
        return btn;
    }

    private static void HandleCommand(IntPtr hwnd, int id, int notify)
    {
        switch (id)
        {
            case IdUnlock:
                // In schedule mode the parent's passcode ignores the weekly schedule
                // for the rest of the session; in budget mode it lifts a schedule
                // override only. Either way a valid unlock code still works.
                if (PasscodeMatches())
                {
                    if (_scheduleMode) OverlayState.IgnoreScheduleUntilRestart = true;
                    OverlayState.ScheduleOverride = true;
                    Hide();
                }
                else if (TryRedeemUnlockCode()) { if (OverlayState.ShouldBlock) ClearEdit(); else Hide(); }
                else Reject(hwnd);
                break;
            case IdExtend15: Extend(hwnd, 15); break;
            case IdExtend30: Extend(hwnd, 30); break;
            case IdExtend60: Extend(hwnd, 60); break;
            case IdShutdown:
                if (MessageBoxW(hwnd, Loc.T("lock.shutdown.confirm.text"),
                        Loc.T("lock.shutdown.confirm.title"), MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2) == IDYES)
                {
                    LockNative.Logoff();
                }
                break;
        }
    }

    private static void Extend(IntPtr hwnd, int minutes)
    {
        if (PasscodeMatches())
        {
            OverlayState.Remaining = TimeKeeper.Extend(Math.Max(0, OverlayState.Remaining), minutes);
            OverlayState.ScheduleOverride = true;
            OverlayState.Persist();
            Hide();
        }
        else Reject(hwnd);
    }

    private static void Reject(IntPtr hwnd)
    {
        EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.FailedUnlock, "lock");
        _error = true;
        ClearEdit();
        SetFocus(_edit);
        InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    private static void HandleTimer(IntPtr hwnd, int id)
    {
        if (id == TimerReassert)
        {
            // Stay clamped to the top of the Z-order — BUT NOT when the WinUI lock is
            // up and running: re-topmosting this black cover would slam it OVER the
            // WinUI window, hiding the unlock UI behind a black screen with no way
            // out. Only reassert in GDI fallback mode, or to hold the floor while the
            // WinUI surface is being (re)launched.
            if (!_winui || !LockAppHost.IsRunning)
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        else if (id == TimerCountdown)
        {
            if (_shutdownCountdown > 0) _shutdownCountdown--;
            else if (_shutdownCountdown == 0) LockNative.Logoff();
            // Only the countdown line changes each second; invalidate just the
            // card so the buttons and field aren't needlessly repainted.
            var (px, py) = PanelOrigin();
            var dirty = new RECT { left = px, top = py, right = px + PanelWidth, bottom = py + 210 };
            InvalidateRectEx(hwnd, ref dirty);
        }
    }

    private static void InvalidateRectEx(IntPtr hwnd, ref RECT r)
    {
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<RECT>());
        try
        {
            Marshal.StructureToPtr(r, p, false);
            InvalidateRect(hwnd, p, false);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    private static void PaintLock(IntPtr hwnd)
    {
        var hdc = BeginPaint(hwnd, out var ps);
        GetClientRect(hwnd, out var client);

        // Fill the full backdrop ourselves (no class brush) so there is no flash.
        FillSolid(hdc, client.left, client.top, client.right - client.left, client.bottom - client.top, ColorOverlayBg);

        // When the WinUI surface is driving, this window is only the black floor —
        // the card, field and buttons are drawn by Curfew.App on top.
        if (_winui)
        {
            EndPaint(hwnd, ref ps);
            return;
        }

        var px = (client.right - PanelWidth) / 2;
        var py = (client.bottom - PanelHeight) / 2;
        var cx = px + PanelWidth / 2;

        // Soft drop shadow: an offset rounded slab behind the card gives depth
        // without needing alpha blending or a shadow bitmap.
        FillRoundRect(hdc, px - 2, py + 4, PanelWidth + 4, PanelHeight + 6, CardCornerDiameter, ColorPanelShadow, ColorPanelShadow);

        // Card: rounded surface with a 1px stroke, matching the WinUI overlay
        // corner radius. No top accent bar — WinUI cards are flat; the accent is
        // carried by the primary action button instead.
        FillRoundRect(hdc, px, py, PanelWidth, PanelHeight, CardCornerDiameter, ColorPanelBg, ColorPanelEdge);

        SetBkMode(hdc, TRANSPARENT);

        var innerX = px + 32;
        var innerW = PanelWidth - 64;

        // Rounded well behind the borderless passcode field (the EDIT child paints
        // on top of this via WM_CTLCOLOREDIT), with a WinUI-style accent underline.
        var fieldX = cx - FieldWidth / 2;
        FillRoundRect(hdc, fieldX, py + FieldTop, FieldWidth, FieldHeight, ControlCornerDiameter, ColorFieldBg, ColorBtnBorder);
        FillSolid(hdc, fieldX + 10, py + FieldTop + FieldHeight - 2, FieldWidth - 20, 2, ColorAccent);

        // Title reflects why we're locked.
        var title = OverlayState.BudgetBlocked ? Loc.T("lock.title.budget") : Loc.T("lock.title.schedule");
        DrawText(hdc, _fontTitle, title, innerX, py + 36, innerW, 52, ColorWhite, DT_CENTER | DT_VCENTER | DT_SINGLELINE);

        // Shutdown countdown — escalates colour as the clock runs out.
        var (countdownText, countdownColor) = _shutdownCountdown switch
        {
            >= 0 and <= 60 => (Loc.T("lock.shutdown.in.short", _shutdownCountdown), ColorRed),
            <= 120 and > 60 => (Loc.T("lock.shutdown.in.long", TimeMath.FormatDuration(_shutdownCountdown)), ColorWarn),
            > 120 => (Loc.T("lock.shutdown.in.long", TimeMath.FormatDuration(_shutdownCountdown)), ColorAccent),
            _ => (Loc.T("lock.exceeded"), ColorAccent),
        };
        DrawText(hdc, _fontCountdown, countdownText, innerX, py + 100, innerW, 34, countdownColor, DT_CENTER | DT_VCENTER | DT_SINGLELINE);

        // Context-appropriate message: the configured blocking message only applies
        // to a budget block; a schedule block uses its own wording (showing the
        // "time limit reached" text outside allowed hours would be misleading).
        string message;
        if (OverlayState.BudgetBlocked)
        {
            var configured = OverlayState.Settings.Get("blocking_message");
            message = string.IsNullOrWhiteSpace(configured) ? Loc.T("lock.default.message") : configured;
        }
        else
        {
            message = Loc.T("lock.schedule.message");
        }
        DrawText(hdc, _fontBody, message, innerX, py + 146, innerW, 44, ColorLight, DT_CENTER | DT_WORDBREAK);

        // Caption above the add-time row.
        DrawText(hdc, _fontCaption, Loc.T("lock.addtime.caption"), innerX, py + 202, innerW, 16, ColorMuted, DT_CENTER | DT_SINGLELINE);

        // Caption above the passcode field, replaced inline by the error on a wrong
        // entry (same slot, directly above the field).
        if (_error)
            DrawText(hdc, _fontError, Loc.T("lock.incorrect"), innerX, py + 290, innerW, 18, ColorRed, DT_CENTER | DT_SINGLELINE);
        else
            DrawText(hdc, _fontCaption, Loc.T("lock.enter.caption"), innerX, py + 292, innerW, 16, ColorMuted, DT_CENTER | DT_SINGLELINE);

        EndPaint(hwnd, ref ps);
    }

    private static void FillSolid(IntPtr hdc, int x, int y, int w, int h, uint color)
    {
        var brush = CreateSolidBrush(color);
        var rect = new RECT { left = x, top = y, right = x + w, bottom = y + h };
        FillRect(hdc, ref rect, brush);
        DeleteObject(brush);
    }

    /// <summary>Filled, 1px-outlined rounded rectangle (WinUI card/control shape).</summary>
    private static void FillRoundRect(IntPtr hdc, int x, int y, int w, int h, int diameter, uint fill, uint border)
    {
        var brush = CreateSolidBrush(fill);
        var pen = CreatePen(PS_SOLID, 1, border);
        var oldBrush = SelectObject(hdc, brush);
        var oldPen = SelectObject(hdc, pen);
        // RoundRect's right/bottom are exclusive; +1 so the 1px stroke isn't clipped.
        RoundRect(hdc, x, y, x + w + 1, y + h + 1, diameter, diameter);
        SelectObject(hdc, oldBrush);
        SelectObject(hdc, oldPen);
        DeleteObject(brush);
        DeleteObject(pen);
    }

    /// <summary>Outline-only rounded rectangle (used for the accent focus ring).</summary>
    private static void DrawRoundOutline(IntPtr hdc, int x, int y, int w, int h, int diameter, uint border)
    {
        var pen = CreatePen(PS_SOLID, 1, border);
        var oldPen = SelectObject(hdc, pen);
        var oldBrush = SelectObject(hdc, GetStockObject(NULL_BRUSH));
        RoundRect(hdc, x, y, x + w + 1, y + h + 1, diameter, diameter);
        SelectObject(hdc, oldPen);
        SelectObject(hdc, oldBrush);
        DeleteObject(pen);
    }

    /// <summary>
    /// Owner-draws a lock-screen button as a WinUI-style rounded control. The
    /// primary unlock button uses the accent fill; the others use the neutral
    /// control fill with an accent stroke when focused.
    /// </summary>
    private static void DrawOwnerButton(IntPtr lParam)
    {
        var dis = Marshal.PtrToStructure<DRAWITEMSTRUCT>(lParam);
        var r = dis.rcItem;
        var pressed = (dis.itemState & ODS_SELECTED) != 0;
        var focused = (dis.itemState & ODS_FOCUS) != 0;
        var accent = (int)dis.CtlID == IdUnlock;

        uint fill, border;
        if (accent)
        {
            fill = pressed ? ColorAccentPressed : ColorAccent;
            border = fill;
        }
        else
        {
            fill = pressed ? ColorBtnPressed : ColorBtn;
            border = focused ? ColorAccent : ColorBtnBorder;
        }

        FillRoundRect(dis.hDC, r.left, r.top, r.Width, r.Height, ControlCornerDiameter, fill, border);

        // Keyboard focus on the accent button (which already fills with the accent
        // colour) is shown with a subtle inner white ring instead of a border swap.
        if (accent && focused)
            DrawRoundOutline(dis.hDC, r.left + 2, r.top + 2, r.Width - 4, r.Height - 4, ControlCornerDiameter - 2, ColorWhite);

        var text = WindowText(dis.hwndItem);
        var old = SelectObject(dis.hDC, _fontControl);
        SetBkMode(dis.hDC, TRANSPARENT);
        SetTextColor(dis.hDC, ColorWhite);
        DrawTextW(dis.hDC, text, text.Length, ref r, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        SelectObject(dis.hDC, old);
    }

    private static string WindowText(IntPtr hwnd)
    {
        var buffer = new char[64];
        var len = GetWindowTextW(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Clamp(len, 0, buffer.Length));
    }

    private static void DrawText(IntPtr hdc, IntPtr font, string text, int x, int y, int w, int h, uint color, int format)
    {
        var old = SelectObject(hdc, font);
        SetTextColor(hdc, color);
        var rect = new RECT { left = x, top = y, right = x + w, bottom = y + h };
        DrawTextW(hdc, text, text.Length, ref rect, format);
        SelectObject(hdc, old);
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

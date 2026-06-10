using Curfew.Core;
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

    private const uint ColorOverlayBg = 0x001E1E2E;
    private const uint ColorPanelBg = 0x00402828;
    private const uint ColorWhite = 0x00FFFFFF;
    private const uint ColorLight = 0x00BBBBBB;
    private const uint ColorAccent = 0x00756CE0;
    private const uint ColorRed = 0x000000FF;

    private const int IdEdit = 101;
    private const int IdUnlock = 102;
    private const int IdExtend15 = 103;
    private const int IdExtend30 = 104;
    private const int IdExtend60 = 105;
    private const int IdShutdown = 106;

    private const int TimerReassert = 2;
    private const int TimerCountdown = 3;

    // Keep delegates alive.
    private static readonly WndProc Proc = LockProc;
    private static readonly HookProc Hook = KeyboardHookProc;

    private static IntPtr _hwnd;
    private static IntPtr _edit;
    private static IntPtr _hook;
    private static int _shutdownCountdown = -1;
    private static bool _error;

    public static void Register(IntPtr hInstance)
    {
        var wc = new WNDCLASSW
        {
            lpfnWndProc = Proc,
            hInstance = hInstance,
            lpszClassName = ClassName,
            hbrBackground = CreateSolidBrush(ColorOverlayBg),
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

        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_SHOWWINDOW | SWP_NOMOVE | SWP_NOSIZE);
        ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd);
        if (_edit != IntPtr.Zero) SetFocus(_edit);

        SetTimer(_hwnd, new IntPtr(TimerReassert), 500, IntPtr.Zero);
        SetTimer(_hwnd, new IntPtr(TimerCountdown), 1000, IntPtr.Zero);

        if (_hook == IntPtr.Zero)
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, Hook, IntPtr.Zero, 0);
    }

    private static void Hide()
    {
        OverlayState.Locked = false;
        KillTimer(_hwnd, new IntPtr(TimerReassert));
        KillTimer(_hwnd, new IntPtr(TimerCountdown));
        ShowWindow(_hwnd, SW_HIDE);

        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }

        OverlayState.Persist();
        if (OverlayState.MiniHwnd != IntPtr.Zero) ShowWindow(OverlayState.MiniHwnd, SW_SHOWNOACTIVATE);
    }

    private static bool PasscodeMatches()
    {
        if (_edit == IntPtr.Zero) return false;
        var buffer = new char[16];
        var len = GetWindowTextW(_edit, buffer, buffer.Length);
        var entered = new string(buffer, 0, Math.Max(0, len));
        var stored = OverlayState.Settings.Get("passcode");
        return !string.IsNullOrEmpty(stored) && entered == stored;
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
                CreateControls(hwnd);
                return IntPtr.Zero;

            case WM_PAINT:
                PaintLock(hwnd);
                return IntPtr.Zero;

            case WM_COMMAND:
                HandleCommand(hwnd, (int)((long)wParam & 0xFFFF), (int)(((long)wParam >> 16) & 0xFFFF));
                return IntPtr.Zero;

            case WM_TIMER:
                HandleTimer(hwnd, (int)(long)wParam);
                return IntPtr.Zero;

            case 0x0010: // WM_CLOSE — never close
                return IntPtr.Zero;

            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private static void CreateControls(IntPtr hwnd)
    {
        var hInstance = GetModuleHandleW(null);
        var sw = GetSystemMetrics(SM_CXSCREEN);
        var sh = GetSystemMetrics(SM_CYSCREEN);
        var panelY = (sh - 460) / 2;
        var cx = sw / 2;

        // Extend buttons.
        AddButton(hwnd, hInstance, "+15 min", IdExtend15, cx - 170, panelY + 200, 100, 38);
        AddButton(hwnd, hInstance, "+30 min", IdExtend30, cx - 50, panelY + 200, 100, 38);
        AddButton(hwnd, hInstance, "+60 min", IdExtend60, cx + 70, panelY + 200, 100, 38);

        // Passcode edit.
        _edit = CreateWindowExW(
            0, "EDIT", "",
            WS_CHILD | WS_VISIBLE | WS_BORDER | ES_CENTER | ES_PASSWORD | ES_NUMBER,
            cx - 100, panelY + 270, 200, 40,
            hwnd, new IntPtr(IdEdit), hInstance, IntPtr.Zero);
        SendMessageW(_edit, EM_SETLIMITTEXT, new IntPtr(4), IntPtr.Zero);

        AddButton(hwnd, hInstance, "Unlock", IdUnlock, cx - 100, panelY + 320, 200, 40);
        AddButton(hwnd, hInstance, "Shut Down Computer", IdShutdown, cx - 100, panelY + 370, 200, 40);
    }

    private static void AddButton(IntPtr parent, IntPtr hInstance, string text, int id, int x, int y, int w, int h)
    {
        CreateWindowExW(0, "BUTTON", text,
            WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            x, y, w, h, parent, new IntPtr(id), hInstance, IntPtr.Zero);
    }

    private static void HandleCommand(IntPtr hwnd, int id, int notify)
    {
        switch (id)
        {
            case IdUnlock:
                if (PasscodeMatches()) { Hide(); }
                else Reject(hwnd);
                break;
            case IdExtend15: Extend(hwnd, 15); break;
            case IdExtend30: Extend(hwnd, 30); break;
            case IdExtend60: Extend(hwnd, 60); break;
            case IdShutdown:
                if (MessageBoxW(hwnd, "Are you sure you want to shut down the computer?",
                        "Confirm Shutdown", MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2) == IDYES)
                {
                    LockNative.Shutdown();
                }
                break;
        }
    }

    private static void Extend(IntPtr hwnd, int minutes)
    {
        if (PasscodeMatches())
        {
            OverlayState.Remaining = TimeKeeper.Extend(Math.Max(0, OverlayState.Remaining), minutes);
            OverlayState.Persist();
            Hide();
        }
        else Reject(hwnd);
    }

    private static void Reject(IntPtr hwnd)
    {
        _error = true;
        ClearEdit();
        SetFocus(_edit);
        InvalidateRect(hwnd, IntPtr.Zero, true);
    }

    private static void HandleTimer(IntPtr hwnd, int id)
    {
        if (id == TimerReassert)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        else if (id == TimerCountdown)
        {
            if (_shutdownCountdown > 0) _shutdownCountdown--;
            else if (_shutdownCountdown == 0) LockNative.Shutdown();
            InvalidateRect(hwnd, IntPtr.Zero, false);
        }
    }

    private static void PaintLock(IntPtr hwnd)
    {
        var hdc = BeginPaint(hwnd, out var ps);
        GetClientRect(hwnd, out var rect);

        var bg = CreateSolidBrush(ColorOverlayBg);
        FillRect(hdc, ref rect, bg);
        DeleteObject(bg);

        var sw = rect.right;
        var sh = rect.bottom;
        var pw = 500;
        var ph = 460;
        var px = (sw - pw) / 2;
        var py = (sh - ph) / 2;

        var panel = CreateSolidBrush(ColorPanelBg);
        var pr = new RECT { left = px, top = py, right = px + pw, bottom = py + ph };
        FillRect(hdc, ref pr, panel);
        DeleteObject(panel);

        SetBkMode(hdc, TRANSPARENT);

        DrawCentered(hdc, "Time's Up!", px, py + 30, pw, 50, 40, true, ColorWhite);

        var countdownText = _shutdownCountdown switch
        {
            <= 60 and >= 0 => $"SHUTDOWN IN: {_shutdownCountdown}s",
            > 60 => $"Shutdown in: {TimeMath.FormatDuration(_shutdownCountdown)}",
            _ => "Time limit exceeded",
        };
        var countdownColor = _shutdownCountdown is <= 60 and >= 0 ? ColorRed : ColorAccent;
        DrawCentered(hdc, countdownText, px, py + 90, pw, 36, 28, true, countdownColor);

        var message = OverlayState.Settings.Get("blocking_message") ?? "Screen time limit reached.";
        DrawCentered(hdc, message, px + 20, py + 140, pw - 40, 30, 18, false, ColorLight);

        DrawCentered(hdc, "Extend time (requires passcode):", px, py + 175, pw, 20, 15, false, ColorLight);
        DrawCentered(hdc, "Enter passcode to unlock:", px, py + 250, pw, 20, 15, false, ColorLight);

        if (_error)
            DrawCentered(hdc, "Incorrect passcode!", px, py + ph - 36, pw, 24, 16, true, ColorRed);

        EndPaint(hwnd, ref ps);
    }

    private static void DrawCentered(IntPtr hdc, string text, int x, int y, int w, int h, int fontSize, bool bold, uint color)
    {
        var font = CreateFontW(fontSize, 0, 0, 0, bold ? 700 : 400, 0, 0, 0, 0, 0, 0, 0, 0, "Segoe UI");
        var old = SelectObject(hdc, font);
        SetTextColor(hdc, color);
        var rect = new RECT { left = x, top = y, right = x + w, bottom = y + h };
        DrawTextW(hdc, text, text.Length, ref rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
        SelectObject(hdc, old);
        DeleteObject(font);
    }

    private static IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HC_ACTION && OverlayState.Locked)
        {
            var info = System.Runtime.InteropServices.Marshal
                .PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
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

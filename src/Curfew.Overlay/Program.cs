using System.Runtime.InteropServices;
using Curfew.Core;
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
    /// <summary>Win32 mini countdown overlay. Plain Win32 so it starts reliably
    /// when the service spawns it. The lock screen is added next.</summary>
    internal static class OverlayApp
    {
        private const string ClassName = "CurfewOverlayClass";
        private const int Width = 160;
        private const int Height = 44;
        private const int Margin = 12;

        private const uint ColorBg = 0x00222222;
        private const uint ColorWhite = 0x00FFFFFF;
        private const uint ColorAccent = 0x00756CE0; // soft red (BGR)
        private const uint ColorRed = 0x004444FF;

        // Keep the delegate alive for the window's lifetime.
        private static readonly WndProc Proc = WindowProc;
        private static SettingsStore _settings = null!;
        private static int _remaining;

        public static void Run()
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            _settings = SettingsStore.Open(CurfewPaths.DatabaseFile, today);

            int? saved = int.TryParse(_settings.Get(RemainingKey(today)), out var s) ? s : null;
            var weekday = TimeMath.MondayBasedWeekday(today);
            _remaining = TimeKeeper.InitialRemaining(saved, _settings.GetDailyLimit(weekday));

            var hInstance = GetModuleHandleW(null);
            OverlayLog.Write($"settings opened, remaining={_remaining}, hInstance={hInstance}");

            var wc = new WNDCLASSW
            {
                lpfnWndProc = Proc,
                hInstance = hInstance,
                lpszClassName = ClassName,
                hbrBackground = CreateSolidBrush(ColorBg),
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

            // Semi-transparent so it reads as a gentle reminder.
            SetLayeredWindowAttributes(hwnd, 0, 220, LWA_ALPHA);
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            SetTimer(hwnd, new IntPtr(1), 1000, IntPtr.Zero);
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
                case WM_PAINT:
                    Paint(hwnd);
                    return IntPtr.Zero;

                case WM_TIMER:
                    Tick(hwnd);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    PostQuitMessage(0);
                    return IntPtr.Zero;

                default:
                    return DefWindowProcW(hwnd, msg, wParam, lParam);
            }
        }

        private static void Tick(IntPtr hwnd)
        {
            _remaining = TimeKeeper.Tick(_remaining);
            if (TimeKeeper.ShouldPersist(_remaining))
            {
                _settings.Set(RemainingKey(DateOnly.FromDateTime(DateTime.Now)), _remaining.ToString());
            }
            InvalidateRect(hwnd, IntPtr.Zero, true);
            // Lock screen at zero is wired up in the next increment.
        }

        private static void Paint(IntPtr hwnd)
        {
            var hdc = BeginPaint(hwnd, out var ps);
            GetClientRect(hwnd, out var rect);

            var bg = CreateSolidBrush(ColorBg);
            FillRect(hdc, ref rect, bg);
            DeleteObject(bg);

            var font = CreateFontW(24, 0, 0, 0, 700, 0, 0, 0, 0, 0, 0, 0, 0, "Consolas");
            var oldFont = SelectObject(hdc, font);
            SetTextColor(hdc, ColorForRemaining(_remaining));
            SetBkMode(hdc, TRANSPARENT);

            var text = TimeMath.FormatCompact(_remaining);
            DrawTextW(hdc, text, text.Length, ref rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);

            SelectObject(hdc, oldFont);
            DeleteObject(font);
            EndPaint(hwnd, ref ps);
        }

        private static uint ColorForRemaining(int seconds)
        {
            if (seconds <= 60) return ColorRed;
            if (seconds <= 300) return ColorAccent;
            return ColorWhite;
        }

        private static string RemainingKey(DateOnly date) => $"remaining_time_{date:yyyy-MM-dd}";
    }
}

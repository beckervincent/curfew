using System.Diagnostics;
using System.Runtime.InteropServices;
using Curfew.Core.Localization;

namespace Curfew.Overlay;

/// <summary>
/// System-tray (notification-area) icon for the overlay process. Shows the
/// remaining budget in its tooltip, a right-click menu to open Settings, and
/// balloon notifications at the warning thresholds. Self-contained Win32 interop
/// so it doesn't disturb the rest of the overlay's P/Invoke surface.
/// </summary>
internal static class TrayIcon
{
    /// <summary>Callback message the shell posts to our window for tray events (WM_APP + 1).</summary>
    public const uint WM_TRAYICON = 0x8000 + 1;

    private const uint TrayId = 1;
    private const int IdSettings = 1;

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x01;

    private const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;
    private const uint MF_STRING = 0x0000;
    private const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;

    private static IntPtr _icon;
    private static bool _added;

    /// <summary>Adds the tray icon for <paramref name="hwnd"/>.</summary>
    public static void Add(IntPtr hwnd, IntPtr hInstance)
    {
        _icon = LoadAppIcon(hInstance);

        var data = NewData(hwnd);
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = _icon;
        data.szTip = Loc.T("tray.idle");

        _added = Shell_NotifyIconW(NIM_ADD, ref data);
        OverlayLog.Write($"tray icon add={_added}");
    }

    /// <summary>Updates the hover tooltip text.</summary>
    public static void UpdateTooltip(string tip)
    {
        if (!_added) return;
        var data = NewData(OverlayState.MiniHwnd);
        data.uFlags = NIF_TIP;
        data.szTip = Truncate(tip, 127);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>Shows a balloon notification (used for time warnings).</summary>
    public static void ShowBalloon(string title, string message)
    {
        if (!_added) return;
        var data = NewData(OverlayState.MiniHwnd);
        data.uFlags = NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>Removes the tray icon (call on shutdown).</summary>
    public static void Remove()
    {
        if (!_added) return;
        var data = NewData(OverlayState.MiniHwnd);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = false;
    }

    /// <summary>Handles a tray callback: any click opens the context menu.</summary>
    public static void OnMessage(IntPtr hwnd, IntPtr lParam)
    {
        var evt = (uint)(lParam.ToInt64() & 0xFFFF);
        if (evt is WM_RBUTTONUP or WM_CONTEXTMENU or WM_LBUTTONUP)
            ShowMenu(hwnd);
    }

    private static void ShowMenu(IntPtr hwnd)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        AppendMenuW(menu, MF_STRING, (nuint)IdSettings, Loc.T("tray.settings"));

        // Required so the menu dismisses correctly when focus is elsewhere.
        SetForegroundWindow(hwnd);
        GetCursorPos(out var pt);
        var cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
        DestroyMenu(menu);

        if (cmd == IdSettings) LaunchSettings();
    }

    /// <summary>Launches the passcode-gated Settings UI from the sibling app folder.</summary>
    private static void LaunchSettings()
    {
        try
        {
            var overlayPath = Environment.ProcessPath; // ...\overlay\Curfew.Overlay.exe
            if (overlayPath is null) return;

            var installRoot = Path.GetDirectoryName(Path.GetDirectoryName(overlayPath)!);
            if (installRoot is null) return;

            var app = Path.Combine(installRoot, "app", "Curfew.App.exe");
            if (File.Exists(app))
                Process.Start(new ProcessStartInfo(app, "--settings") { UseShellExecute = false });
        }
        catch
        {
            // Best effort: failing to launch settings must never crash the overlay.
        }
    }

    private static IntPtr LoadAppIcon(IntPtr hInstance)
    {
        var path = Environment.ProcessPath;
        if (path is not null)
        {
            var icon = ExtractIconW(hInstance, path, 0);
            // ExtractIcon returns 1 when the file has no icons; treat that as none.
            if (icon != IntPtr.Zero && icon.ToInt64() != 1) return icon;
        }
        return LoadIconW(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
    }

    private static NOTIFYICONDATAW NewData(IntPtr hwnd) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = hwnd,
        uID = TrayId,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIconW(IntPtr hInst, string exeFileName, int iconIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}

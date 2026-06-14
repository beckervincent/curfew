using System.Diagnostics;
using System.Runtime.InteropServices;
using Curfew.Core;
using Curfew.Core.Localization;

namespace Curfew.Overlay;

/// <summary>system-tray icon for overlay process: tooltip shows remaining budget, right-click menu opens Settings, balloons at warning thresholds. self-contained Win32 interop, separate from rest of overlay P/Invoke</summary>
internal static class TrayIcon
{
    /// <summary>callback msg shell posts to our window for tray events (WM_APP + 1)</summary>
    public const uint WM_TRAYICON = 0x8000 + 1;

    /// <summary>list debug/test items ("Show Warning", "Show Blocking Overlay") in menu; <c>false</c> for release build</summary>
    internal const bool ShowDebugItems = true;

    /// <summary>repo opened by About item</summary>
    private const string GitHubUrl = "https://github.com/beckervincent/curfew";

    private const uint TrayId = 1;

    // Menu command ids.
    private const int IdStats = 1;
    private const int IdSettings = 2;
    private const int IdExtend15 = 3;
    private const int IdExtend45 = 4;
    private const int IdPause = 5;
    private const int IdCheckUpdate = 6;
    private const int IdAbout = 7;
    private const int IdQuit = 8;
    private const int IdShowWarning = 9;
    private const int IdShowOverlay = 10;

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;
    private const uint NIIF_INFO = 0x01;

    private const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;
    private const uint MF_STRING = 0x0000, MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;

    private static IntPtr _icon;
    private static bool _added;
    private static IntPtr _hwnd;
    private static IntPtr _hInstance;

    /// <summary>add tray icon for <paramref name="hwnd"/></summary>
    public static void Add(IntPtr hwnd, IntPtr hInstance)
    {
        // Remember the handles so the icon can be re-added on a "TaskbarCreated"
        // broadcast (see Readd): the overlay is launched by a logon scheduled task and
        // commonly wins the race against Explorer, so the very first NIM_ADD lands
        // before the notification area exists and is silently dropped.
        _hwnd = hwnd;
        _hInstance = hInstance;
        _icon = LoadAppIcon(hInstance);
        AddCore();
    }

    /// <summary>
    /// Re-adds the icon after the shell (re)creates the notification area. Explorer
    /// broadcasts "TaskbarCreated" when it starts and on every restart; without
    /// re-adding, an overlay that started before Explorer — or kept running across an
    /// Explorer crash — would have no visible tray icon for the rest of the session.
    /// </summary>
    public static void Readd()
    {
        if (_hwnd == IntPtr.Zero) return; // Add() not called yet
        if (_icon == IntPtr.Zero) _icon = LoadAppIcon(_hInstance);
        AddCore();
    }

    /// <summary>Issues the NIM_ADD with the current handles/icon; shared by Add and Readd.</summary>
    private static void AddCore()
    {
        var data = NewData(_hwnd);
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = _icon;
        data.szTip = Loc.T("tray.idle");

        // Only ever latch _added on success. A redundant re-add (the icon already
        // exists, e.g. Explorer did not actually drop it) returns false; we must not
        // let that clear a flag an earlier successful add set, or UpdateTooltip /
        // ShowBalloon would stop working until the next add.
        var ok = Shell_NotifyIconW(NIM_ADD, ref data);
        if (ok) _added = true;
        OverlayLog.Write($"tray icon add ok={ok} added={_added}");
    }

    /// <summary>update hover tooltip text</summary>
    public static void UpdateTooltip(string tip)
    {
        if (!_added) return;
        var data = NewData(OverlayState.MiniHwnd);
        data.uFlags = NIF_TIP;
        data.szTip = Truncate(tip, 127);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>show balloon notification (time warnings)</summary>
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

    /// <summary>remove tray icon (call on shutdown)</summary>
    public static void Remove()
    {
        if (!_added) return;
        var data = NewData(OverlayState.MiniHwnd);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = false;
    }

    /// <summary>handle tray callback: any click opens context menu</summary>
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

        // privileged items (stats, settings, extend, pause, quit) gated: hand off to Curfew.App which verifies passcode before overlay acts. harmless items (update check, about) + debug items run directly
        AppendMenuW(menu, MF_STRING, (nuint)IdStats, Loc.T("tray.stats"));
        AppendMenuW(menu, MF_STRING, (nuint)IdSettings, Loc.T("tray.settings"));
        AppendMenuW(menu, MF_SEPARATOR, 0, string.Empty);
        AppendMenuW(menu, MF_STRING, (nuint)IdExtend15, Loc.T("tray.extend", 15));
        AppendMenuW(menu, MF_STRING, (nuint)IdExtend45, Loc.T("tray.extend", 45));
        AppendMenuW(menu, MF_STRING, (nuint)IdPause,
            OverlayState.IsPaused ? Loc.T("tray.resume") : Loc.T("tray.pause"));
        AppendMenuW(menu, MF_SEPARATOR, 0, string.Empty);
        AppendMenuW(menu, MF_STRING, (nuint)IdCheckUpdate, Loc.T("tray.update"));
        AppendMenuW(menu, MF_STRING, (nuint)IdAbout, Loc.T("tray.about"));

        if (ShowDebugItems)
        {
            AppendMenuW(menu, MF_SEPARATOR, 0, string.Empty);
            AppendMenuW(menu, MF_STRING, (nuint)IdShowWarning, Loc.T("tray.warning.test"));
            AppendMenuW(menu, MF_STRING, (nuint)IdShowOverlay, Loc.T("tray.overlay.test"));
        }

        AppendMenuW(menu, MF_SEPARATOR, 0, string.Empty);
        AppendMenuW(menu, MF_STRING, (nuint)IdQuit, Loc.T("tray.quit"));

        // needed so menu dismisses correctly when focus elsewhere
        SetForegroundWindow(hwnd);
        GetCursorPos(out var pt);
        var cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
        DestroyMenu(menu);

        Dispatch(cmd);
    }

    /// <summary>route chosen menu command to its action</summary>
    private static void Dispatch(int cmd)
    {
        switch (cmd)
        {
            // Settings already passcode-gated; stats live inside it
            case IdStats:
            case IdSettings: LaunchApp("--settings"); break;

            // gated in Curfew.App, which writes a command the overlay applies
            case IdExtend15: LaunchApp("--tray=extend15"); break;
            case IdExtend45: LaunchApp("--tray=extend45"); break;
            case IdPause: LaunchApp(OverlayState.IsPaused ? "--tray=resume" : "--tray=pause"); break;
            case IdQuit: LaunchApp("--tray=quit"); break;

            case IdCheckUpdate: CheckForUpdates(); break;
            case IdAbout: OpenUrl(GitHubUrl); break;

            // debug items -- harmless, ungated
            case IdShowWarning: ShowBalloon(Loc.T("tray.warning.test.title"), Loc.T("tray.warning.test.body")); break;
            case IdShowOverlay: LockScreen.Show(); break;
        }
    }

    /// <summary>launch Curfew.App from sibling app folder with given arguments</summary>
    private static void LaunchApp(string arguments)
    {
        try
        {
            var overlayPath = Environment.ProcessPath; // ...\overlay\Curfew.Overlay.exe
            if (overlayPath is null) return;

            var installRoot = Path.GetDirectoryName(Path.GetDirectoryName(overlayPath)!);
            if (installRoot is null) return;

            var app = Path.Combine(installRoot, "app", "Curfew.App.exe");
            if (File.Exists(app))
                Process.Start(new ProcessStartInfo(app, arguments) { UseShellExecute = false });
        }
        catch
        {
            // best effort: launch failure must never crash overlay
        }
    }

    /// <summary>check GitHub for newer release, report result as balloon</summary>
    private static void CheckForUpdates()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var version = typeof(TrayIcon).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
                var release = await Updater.CheckForUpdateAsync(version, Updater.HttpFetchAsync);
                ShowBalloon(
                    Loc.T("tray.idle"),
                    release is null
                        ? Loc.T("tray.update.none")
                        : Loc.T("tray.update.available", release.Value.Tag.TrimStart('v', 'V')));
            }
            catch
            {
                ShowBalloon(Loc.T("tray.idle"), Loc.T("tray.update.failed"));
            }
        });
    }

    /// <summary>open URL in default browser</summary>
    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* best effort */ }
    }

    private static IntPtr LoadAppIcon(IntPtr hInstance)
    {
        var path = Environment.ProcessPath;
        if (path is not null)
        {
            var icon = ExtractIconW(hInstance, path, 0);
            // ExtractIcon returns 1 when file has no icons -> treat as none
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

using System.Runtime.InteropServices;

namespace Curfew.Overlay;

/// <summary>
/// Extra Win32 surface used exclusively by the full-screen lock screen: child
/// control / button / edit styles, the low-level keyboard hook used to swallow
/// escape shortcuts, and the privileged system-shutdown path.
/// </summary>
/// <remarks>
/// This is a thin, dependency-free P/Invoke shim. The general overlay window
/// plumbing (paint, GDI brushes/fonts, window class) lives in
/// <see cref="Native"/>; only the lock-specific extras live here so the two
/// concerns stay separable. Every value below mirrors the corresponding Win32
/// SDK definition exactly — do not change a literal without checking winuser.h.
/// </remarks>
internal static class LockNative
{
    // ---- Window / control styles ------------------------------------------

    /// <summary>WS_TABSTOP — control can receive focus via Tab.</summary>
    public const int WS_TABSTOP = 0x00010000;

    // Edit-control (EDIT) styles.
    /// <summary>ES_CENTER — centre text horizontally in the edit box.</summary>
    public const int ES_CENTER = 0x0001;
    /// <summary>ES_PASSWORD — mask characters (passcode entry).</summary>
    public const int ES_PASSWORD = 0x0020;
    /// <summary>ES_NUMBER — accept digits only.</summary>
    public const int ES_NUMBER = 0x2000;

    // Button (BUTTON) styles.
    /// <summary>BS_PUSHBUTTON — standard command button.</summary>
    public const int BS_PUSHBUTTON = 0x0000;
    /// <summary>BS_OWNERDRAW — button is drawn by the parent via WM_DRAWITEM, so we
    /// can render the WinUI-style rounded fill instead of the classic grey chrome.</summary>
    public const int BS_OWNERDRAW = 0x0000000B;

    // WM_DRAWITEM itemState flags (subset we react to).
    /// <summary>ODS_SELECTED — the button is currently pressed.</summary>
    public const uint ODS_SELECTED = 0x0001;
    /// <summary>ODS_FOCUS — the button has keyboard focus (draw the focus ring).</summary>
    public const uint ODS_FOCUS = 0x0010;

    // ---- Window messages / notifications ----------------------------------

    /// <summary>EM_SETLIMITTEXT — cap the number of characters an edit accepts.</summary>
    public const uint EM_SETLIMITTEXT = 0x00C5;
    /// <summary>BN_CLICKED — WM_COMMAND notification code for a button press.</summary>
    public const uint BN_CLICKED = 0;

    // ---- Low-level keyboard hook ------------------------------------------

    /// <summary>WH_KEYBOARD_LL — low-level keyboard input hook.</summary>
    public const int WH_KEYBOARD_LL = 13;
    /// <summary>HC_ACTION — hook code: wParam/lParam carry a real event.</summary>
    public const int HC_ACTION = 0;

    // Virtual-key codes for the shortcuts the lock screen must block or honour.
    public const int VK_TAB = 0x09;
    public const int VK_RETURN = 0x0D;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_MENU = 0x12;   // Alt
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_D = 0x44;
    public const int VK_M = 0x4D;
    public const int VK_F4 = 0x73;

    /// <summary>High bit of <see cref="GetAsyncKeyState"/> — key currently down.</summary>
    public const ushort KEY_PRESSED = 0x8000;

    // ---- MessageBox flags / results ---------------------------------------

    public const uint MB_YESNO = 0x0004;
    public const uint MB_ICONQUESTION = 0x0020;
    public const uint MB_DEFBUTTON2 = 0x0100;
    public const int IDYES = 6;

    // ---- Shutdown / privilege adjustment ----------------------------------

    public const uint EWX_SHUTDOWN = 0x0001;
    /// <summary>EWX_LOGOFF — end the calling user's session (not a machine shutdown).</summary>
    public const uint EWX_LOGOFF = 0x0000;
    /// <summary>EWX_FORCE — close apps without waiting; a child must not be able to veto the curfew.</summary>
    public const uint EWX_FORCE = 0x0004;
    /// <summary>EWX_FORCEIFHUNG — proceed even if a window stops responding.</summary>
    public const uint EWX_FORCEIFHUNG = 0x0010;
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x0002;

    /// <summary>SeShutdownPrivilege — required before <see cref="ExitWindowsEx"/>.</summary>
    private const string SE_SHUTDOWN_NAME = "SeShutdownPrivilege";

    /// <summary>
    /// SHTDN_REASON_MAJOR_APPLICATION | SHTDN_REASON_MINOR_MAINTENANCE |
    /// SHTDN_REASON_FLAG_PLANNED — a tidy entry in the shutdown event log so an
    /// administrator can see Curfew (not a crash) triggered the shutdown.
    /// </summary>
    private const uint SHUTDOWN_REASON = 0x00040000u | 0x00000001u | 0x80000000u;

    // ---- Delegates --------------------------------------------------------

    /// <summary>Signature for the low-level keyboard hook callback. The instance
    /// passed to <see cref="SetWindowsHookExW"/> must be rooted by the caller for
    /// the lifetime of the hook so it is not collected while native code holds
    /// the pointer.</summary>
    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    // ---- Structures -------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    // ---- user32 -----------------------------------------------------------

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hwnd, [Out] char[] text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetWindowTextW(IntPtr hwnd, string text);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ExitWindowsEx(uint flags, uint reason);

    // ---- kernel32 ---------------------------------------------------------

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);

    // ---- advapi32 ---------------------------------------------------------

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValueW(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr retLen);

    // ---- Helpers ----------------------------------------------------------

    /// <summary>
    /// Logs the current user off, ending their session. The SYSTEM service runs in
    /// session 0 and is unaffected, so enforcement keeps running across the logoff.
    /// Unlike a shutdown, this needs no special privilege — a user may always end
    /// their own session — so there is no token/privilege dance. <c>EWX_FORCE</c>
    /// closes apps without waiting, so a child cannot veto the curfew by holding a
    /// dialog open. Best-effort: any failure is logged, never thrown, because the
    /// lock screen must never crash.
    /// </summary>
    public static void Logoff()
    {
        if (!ExitWindowsEx(EWX_LOGOFF | EWX_FORCE, SHUTDOWN_REASON))
            OverlayLog.Write($"logoff: ExitWindowsEx failed err={Marshal.GetLastWin32Error()}");
        else
            OverlayLog.Write("logoff: requested");
    }
}

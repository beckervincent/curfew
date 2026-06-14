using System.Runtime.InteropServices;

namespace Curfew.Overlay;

/// <summary>Win32 for lock hard floor: keyboard hook, topmost/taskbar helpers, forced logoff</summary>
/// <remarks>thin P/Invoke shim; general window plumbing in <see cref="Native"/>; values mirror Win32 SDK exactly -- check winuser.h before changing a literal</remarks>
internal static class LockNative
{
    // ---- Low-level keyboard hook ------------------------------------------

    /// <summary>WH_KEYBOARD_LL -- low-level keyboard input hook</summary>
    public const int WH_KEYBOARD_LL = 13;
    /// <summary>HC_ACTION -- hook code: wParam/lParam carry real event</summary>
    public const int HC_ACTION = 0;

    // VK codes for shortcuts lock screen blocks
    public const int VK_TAB = 0x09;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_MENU = 0x12;   // Alt
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_D = 0x44;
    public const int VK_M = 0x4D;
    public const int VK_F4 = 0x73;

    // ---- Logoff -----------------------------------------------------------

    /// <summary>EWX_LOGOFF -- end calling user session, not machine shutdown</summary>
    public const uint EWX_LOGOFF = 0x0000;
    /// <summary>EWX_FORCE -- close apps without waiting; child can't veto curfew</summary>
    public const uint EWX_FORCE = 0x0004;

    /// <summary>SHTDN_REASON_MAJOR_APPLICATION | MINOR_MAINTENANCE | FLAG_PLANNED -- admin sees Curfew (not crash) ended session</summary>
    private const uint LOGOFF_REASON = 0x00040000u | 0x00000001u | 0x80000000u;

    // ---- Delegates --------------------------------------------------------

    /// <summary>low-level keyboard hook callback sig; instance passed to <see cref="SetWindowsHookExW"/> must stay rooted for hook lifetime so GC won't collect while native holds pointer</summary>
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

    // ---- user32 -----------------------------------------------------------

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string? className, string? windowName);

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

    // ---- Helpers ----------------------------------------------------------

    /// <summary>log current user off; SYSTEM service in session 0 unaffected so enforcement survives. no privilege needed (own session). <c>EWX_FORCE</c> closes apps so child can't veto with open dialog. best-effort: failure logged not thrown</summary>
    public static void Logoff()
    {
        if (!ExitWindowsEx(EWX_LOGOFF | EWX_FORCE, LOGOFF_REASON))
            OverlayLog.Write($"logoff: ExitWindowsEx failed err={Marshal.GetLastWin32Error()}");
        else
            OverlayLog.Write("logoff: requested");
    }
}

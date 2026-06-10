using System.Runtime.InteropServices;

namespace Curfew.Overlay;

/// <summary>Extra Win32 used by the lock screen: child controls, keyboard hook
/// and shutdown.</summary>
internal static class LockNative
{
    public const int WS_TABSTOP = 0x00010000;

    // Edit / button styles.
    public const int ES_CENTER = 0x0001;
    public const int ES_PASSWORD = 0x0020;
    public const int ES_NUMBER = 0x2000;
    public const int BS_PUSHBUTTON = 0x0000;

    public const uint EM_SETLIMITTEXT = 0x00C5;
    public const uint BN_CLICKED = 0;

    public const int WH_KEYBOARD_LL = 13;
    public const int HC_ACTION = 0;

    public const int VK_TAB = 0x09;
    public const int VK_RETURN = 0x0D;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_MENU = 0x12;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_D = 0x44;
    public const int VK_M = 0x4D;
    public const int VK_F4 = 0x73;

    public const uint MB_YESNO = 0x4;
    public const uint MB_ICONQUESTION = 0x20;
    public const uint MB_DEFBUTTON2 = 0x100;
    public const int IDYES = 6;

    public const uint EWX_SHUTDOWN = 0x1;
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    public const uint TOKEN_QUERY = 0x8;
    public const uint SE_PRIVILEGE_ENABLED = 0x2;

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

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
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

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

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValueW(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr retLen);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ExitWindowsEx(uint flags, uint reason);

    public static void Shutdown()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return;
        if (LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid))
        {
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        CloseHandle(token);
        ExitWindowsEx(EWX_SHUTDOWN, 0);
    }
}

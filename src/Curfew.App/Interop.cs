using System.Runtime.InteropServices;

namespace Curfew.App;

/// <summary>
/// Low-level keyboard hook that suppresses shortcuts which could escape the
/// lock screen (Alt+F4, Alt+Tab, Esc, Win, Win+D, Win+M). Win+L and
/// Ctrl+Alt+Del are enforced by the OS and cannot be blocked from user mode.
/// </summary>
internal sealed class KeyboardBlocker : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int HC_ACTION = 0;

    private const int VK_TAB = 0x09;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_MENU = 0x12;   // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_D = 0x44;
    private const int VK_M = 0x4D;
    private const int VK_F4 = 0x73;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook;

    public KeyboardBlocker()
    {
        _proc = HookProc; // keep the delegate alive for the hook's lifetime
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var vk = (int)info.vkCode;
            var alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            var win = ((GetAsyncKeyState(VK_LWIN) | GetAsyncKeyState(VK_RWIN)) & 0x8000) != 0;

            var block = vk is VK_ESCAPE or VK_LWIN or VK_RWIN
                        || (alt && vk is VK_F4 or VK_TAB)
                        || (win && vk is VK_D or VK_M);
            if (block) return 1;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}

/// <summary>Initiates a Windows shutdown after enabling the shutdown privilege.</summary>
internal static class SystemPower
{
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const uint TOKEN_QUERY = 0x8;
    private const uint SE_PRIVILEGE_ENABLED = 0x2;
    private const uint EWX_SHUTDOWN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    public static void Shutdown()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return;

        if (LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid))
        {
            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED,
            };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }

        CloseHandle(token);
        ExitWindowsEx(EWX_SHUTDOWN, 0);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);
}

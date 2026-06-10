using System.Runtime.InteropServices;

namespace Curfew.Service;

/// <summary>Win32 interop for enumerating user sessions and launching a process
/// inside one. Used to spawn the tray app into each interactive session.</summary>
internal static class SessionInterop
{
    public enum WtsConnectState
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
        Idle = 5,
        Listen = 6,
        Reset = 7,
        Down = 8,
        Init = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr pWinStationName;
        public WtsConnectState State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    private const int CREATE_UNICODE_ENVIRONMENT = 0x0400;

    /// <summary>Active interactive session ids (excludes session 0).</summary>
    public static List<uint> ActiveSessions()
    {
        var result = new List<uint>();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count))
            return result;

        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var ptr = buffer + i * size;
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(ptr);
                var interactive = info.State is WtsConnectState.Active or WtsConnectState.Connected;
                if (interactive && info.SessionId != 0)
                    result.Add(info.SessionId);
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return result;
    }

    /// <summary>Launches <paramref name="exePath"/> in the given session as the
    /// logged-in user. Returns the process handle, or zero on failure.</summary>
    public static IntPtr LaunchInSession(uint sessionId, string exePath)
    {
        if (!WTSQueryUserToken(sessionId, out var token))
            return IntPtr.Zero;

        var envBlock = IntPtr.Zero;
        try
        {
            CreateEnvironmentBlock(out envBlock, token, false);

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
            };

            var commandLine = $"\"{exePath}\"";
            var ok = CreateProcessAsUserW(
                token, exePath, commandLine,
                IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT,
                envBlock, null, ref si, out var pi);

            if (!ok) return IntPtr.Zero;

            CloseHandle(pi.hThread);
            return pi.hProcess;
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            CloseHandle(token);
        }
    }

    public static bool HasProcessExited(IntPtr processHandle)
    {
        const uint STILL_ACTIVE = 259;
        return !GetExitCodeProcess(processHandle, out var code) || code != STILL_ACTIVE;
    }

    public static void Close(IntPtr handle)
    {
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version,
        out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr env);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(
        IntPtr token, string? applicationName, string commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        int creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

using System.Runtime.InteropServices;

namespace Curfew.Service;

/// <summary>
/// Win32 interop for enumerating Windows Terminal Services (WTS) sessions and
/// launching a process inside one as the logged-in user. Used to spawn the tray
/// app into each interactive session.
/// </summary>
/// <remarks>
/// All methods are best-effort and self-contained: failures are reported through
/// return values (and the on-device <see cref="ServiceLog"/>) rather than thrown,
/// because this code runs inside the service loop where an unhandled exception
/// would take down the whole worker. Native handles are always released.
/// </remarks>
internal static class SessionInterop
{
    /// <summary>WTS connection states (the <c>WTS_CONNECTSTATE_CLASS</c> enum).</summary>
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

    // CreateProcessAsUser flags.
    private const int CREATE_UNICODE_ENVIRONMENT = 0x0400;

    // WTSEnumerateSessions interface version; must be 1 per the API contract.
    private const int WTS_CURRENT_SERVER_VERSION = 1;

    // The console/services session. Never interactive for a real user, so it is
    // excluded from the overlay-launch logic.
    private const uint ServicesSessionId = 0;

    // Sentinel returned by GetExitCodeProcess for a process that is still running.
    private const uint STILL_ACTIVE = 259;

    /// <summary>
    /// Returns the ids of interactive user sessions, excluding the services
    /// session (0). Never throws; returns an empty list if enumeration fails.
    /// </summary>
    public static List<uint> ActiveSessions()
    {
        var result = new List<uint>();

        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, WTS_CURRENT_SERVER_VERSION,
                out var buffer, out var count))
        {
            ServiceLog.Write($"WTSEnumerateSessions failed (err {Marshal.GetLastWin32Error()})");
            return result;
        }

        // A success return with a null buffer would otherwise lead to invalid
        // pointer arithmetic below; guard defensively.
        if (buffer == IntPtr.Zero)
            return result;

        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var ptr = buffer + i * size;
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(ptr);

                // Active      = the user is at the screen.
                // Disconnected = logged in but not currently viewing
                //                (workstation locked, or RDP session switched out).
                // Both states need the overlay/lock running, so treat them alike.
                var interactive = info.State is WtsConnectState.Active or WtsConnectState.Disconnected;
                if (interactive && info.SessionId != ServicesSessionId)
                    result.Add(info.SessionId);
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return result;
    }

    /// <summary>
    /// Launches <paramref name="exePath"/> in the given session as the logged-in
    /// user. Returns the process handle (<see cref="IntPtr.Zero"/> on failure)
    /// and the associated Win32 error code (0 on success) for diagnostics. The
    /// caller owns the returned handle and must release it with <see cref="Close"/>.
    /// </summary>
    public static (IntPtr Handle, int Error) LaunchInSession(uint sessionId, string exePath)
    {
        // ERROR_INVALID_PARAMETER (87) / ERROR_FILE_NOT_FOUND (2): fail fast with a
        // meaningful Win32 code instead of letting CreateProcessAsUser report a
        // confusing one (or, for an empty path, throwing inside the marshaller).
        if (string.IsNullOrWhiteSpace(exePath))
            return (IntPtr.Zero, 87);
        if (!File.Exists(exePath))
            return (IntPtr.Zero, 2);

        if (!WTSQueryUserToken(sessionId, out var token))
        {
            var err = Marshal.GetLastWin32Error();
            ServiceLog.Write($"WTSQueryUserToken({sessionId}) failed (err {err})");
            return (IntPtr.Zero, err);
        }

        var envBlock = IntPtr.Zero;
        try
        {
            // A null environment block combined with CREATE_UNICODE_ENVIRONMENT
            // gives the child an EMPTY environment (no SystemRoot/TEMP/PATH),
            // which crashes a WinUI app. Only pass the user's block — and set the
            // flag — when CreateEnvironmentBlock actually produced one.
            var haveEnv = CreateEnvironmentBlock(out envBlock, token, false) && envBlock != IntPtr.Zero;
            var creationFlags = haveEnv ? CREATE_UNICODE_ENVIRONMENT : 0;

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                // Target the interactive desktop so the launched window is visible.
                lpDesktop = @"winsta0\default",
            };

            // Resolve the child's working directory to the executable's own folder
            // so its co-located dependencies load. Fall back to null (inherit the
            // service's directory) only if the path has no directory component.
            var workingDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(workingDir))
                workingDir = null;

            // CreateProcessAsUser may modify the command-line buffer in place, so
            // it must be a writable string distinct from applicationName.
            var commandLine = $"\"{exePath}\"";

            var ok = CreateProcessAsUserW(
                token, exePath, commandLine,
                IntPtr.Zero, IntPtr.Zero, false,
                creationFlags,
                envBlock, workingDir, ref si, out var pi);

            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                ServiceLog.Write($"CreateProcessAsUser('{exePath}', session {sessionId}) failed (err {err})");
                return (IntPtr.Zero, err);
            }

            // We only track the process; the primary thread handle is not needed.
            CloseHandle(pi.hThread);
            return (pi.hProcess, 0);
        }
        finally
        {
            if (envBlock != IntPtr.Zero) DestroyEnvironmentBlock(envBlock);
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Reports whether the process behind <paramref name="processHandle"/> has
    /// exited. Treats a failed query as "exited" so a dead/invalid handle is not
    /// mistaken for a live process.
    /// </summary>
    public static bool HasProcessExited(IntPtr processHandle)
    {
        if (processHandle == IntPtr.Zero)
            return true;
        return !GetExitCodeProcess(processHandle, out var code) || code != STILL_ACTIVE;
    }

    /// <summary>Closes a handle previously returned by <see cref="LaunchInSession"/>; safe on zero.</summary>
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

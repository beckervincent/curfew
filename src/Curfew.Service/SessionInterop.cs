using System.Runtime.InteropServices;

namespace Curfew.Service;

/// <summary>
/// Win32 interop for enumerating interactive Windows Terminal Services (WTS)
/// sessions. The overlay is launched into each session by a logon scheduled task
/// (a WinUI app fails to start under <c>CreateProcessAsUser</c>), so this only
/// needs to report which sessions are interactive.
/// </summary>
/// <remarks>
/// Best-effort and self-contained: failure is reported through the return value
/// (and the on-device <see cref="ServiceLog"/>) rather than thrown, because this
/// runs inside the service loop where an unhandled exception would take down the
/// whole worker. Native memory is always released.
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

    // WTSEnumerateSessions interface version; must be 1 per the API contract.
    private const int WTS_CURRENT_SERVER_VERSION = 1;

    // The console/services session. Never interactive for a real user, so it is
    // excluded from the overlay-launch logic.
    private const uint ServicesSessionId = 0;

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

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version,
        out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}

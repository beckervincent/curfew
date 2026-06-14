using System.Runtime.InteropServices;

namespace Curfew.Service;

/// <summary>Win32 interop to enumerate interactive WTS sessions. Overlay launched per session by logon task (WinUI app fails to start under <c>CreateProcessAsUser</c>), so this only reports which sessions interactive.</summary>
/// <remarks>Best-effort, self-contained: failure via return value (and <see cref="ServiceLog"/>) not thrown, since runs in service loop where unhandled exception takes down worker. Native memory always released.</remarks>
internal static class SessionInterop
{
    /// <summary>WTS connection states (<c>WTS_CONNECTSTATE_CLASS</c> enum).</summary>
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

    // WTSEnumerateSessions interface version; must be 1 per API contract
    private const int WTS_CURRENT_SERVER_VERSION = 1;

    // console/services session. never interactive for real user, excluded from overlay-launch
    private const uint ServicesSessionId = 0;

    /// <summary>Return ids of interactive user sessions, excluding services session (0). Never throws; empty list if enumeration fails.</summary>
    public static List<uint> ActiveSessions()
    {
        var result = new List<uint>();

        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, WTS_CURRENT_SERVER_VERSION,
                out var buffer, out var count))
        {
            ServiceLog.Write($"WTSEnumerateSessions failed (err {Marshal.GetLastWin32Error()})");
            return result;
        }

        // success with null buffer = invalid pointer arithmetic below; guard defensively
        if (buffer == IntPtr.Zero)
            return result;

        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var ptr = buffer + i * size;
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(ptr);

                // Active = user at screen. Disconnected = logged in not viewing (locked, or RDP switched out). both need overlay/lock running, treat alike
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

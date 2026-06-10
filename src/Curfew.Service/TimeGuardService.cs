using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Curfew.Core;

namespace Curfew.Service;

/// <summary>
/// Time Manipulation Guarding. Queries trusted NTP time and, if the local clock
/// has been moved beyond tolerance, overwrites it with the trusted time and
/// forces a Windows Time resync. Runs as SYSTEM, which holds the
/// <c>SE_SYSTEMTIME_NAME</c> privilege required by <c>SetSystemTime</c>.
/// </summary>
/// <remarks>
/// The pure decision logic (tolerance, tamper direction) lives in
/// <see cref="TimeGuard"/> in the Core layer and is unit-tested there; this type
/// only performs the privileged side effects — talking to the network and
/// writing the clock — and is intentionally tolerant of transient failures so a
/// flaky network or an offline machine is never penalised.
/// </remarks>
internal static class TimeGuardService
{
    /// <summary>The well-known UDP port for NTP/SNTP traffic (RFC 4330).</summary>
    private const int NtpPort = 123;

    /// <summary>
    /// Queries a single NTP server for the current trusted time.
    /// </summary>
    /// <param name="host">Hostname or IP address of the NTP server.</param>
    /// <param name="timeoutMs">Send/receive timeout for the UDP exchange, in milliseconds.</param>
    /// <returns>
    /// The server's transmit timestamp, or <c>null</c> on any failure (DNS,
    /// network, timeout, or a malformed reply). Never throws.
    /// </returns>
    public static DateTimeOffset? QueryNtp(string host, int timeoutMs = 3000)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = timeoutMs;
            udp.Client.SendTimeout = timeoutMs;

            // Connect() resolves the host and pins the remote endpoint, so the
            // socket will only accept datagrams from this server.
            udp.Connect(host, NtpPort);
            udp.Send(Sntp.BuildRequest());

            // The endpoint is filled in with the actual sender; because the
            // socket is connected, anything other than the queried server is
            // dropped at the OS layer before it reaches us.
            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            var data = udp.Receive(ref endpoint);

            // Sntp.ParseReply enforces the minimum packet size and rejects the
            // all-zero "unspecified" timestamp; it throws on a malformed reply.
            return Sntp.ParseReply(data);
        }
        catch (SocketException)
        {
            // Unreachable host, DNS failure, or a receive timeout — expected on
            // a blocked or offline network. Let the caller fall through to the
            // next server.
            return null;
        }
        catch (Exception ex)
        {
            // A malformed reply (ArgumentException) or any other unexpected
            // error. Record it for diagnostics, but never propagate.
            ServiceLog.Write($"time guard: NTP query to '{host}' failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns trusted time from the first NTP server that answers, trying each
    /// of <see cref="Sntp.DefaultServers"/> in order.
    /// </summary>
    /// <returns>The trusted time, or <c>null</c> when every server fails.</returns>
    public static DateTimeOffset? TrustedNow()
    {
        foreach (var host in Sntp.DefaultServers)
        {
            var time = QueryNtp(host);
            if (time is not null) return time;
        }

        ServiceLog.Write("time guard: no NTP server reachable; skipping clock check");
        return null;
    }

    /// <summary>
    /// Checks the local clock against trusted time and corrects it when tampering
    /// is detected. A no-op when NTP is unreachable, so offline machines are not
    /// penalised, and when the clock is already within tolerance.
    /// </summary>
    public static void Enforce()
    {
        var trusted = TrustedNow();
        if (trusted is null) return;

        var verdict = TimeGuard.Evaluate(DateTimeOffset.Now, trusted.Value);
        if (!TimeGuard.ShouldCorrect(verdict)) return;

        // SetSystemTime takes a UTC SYSTEMTIME regardless of the machine's time zone.
        var trustedUtc = trusted.Value.ToUniversalTime();
        ServiceLog.Write(
            $"time guard: clock {verdict} (local now {DateTimeOffset.Now:O}); " +
            $"correcting to trusted {trustedUtc:O}");

        if (SetSystemClock(trustedUtc))
        {
            // Re-anchor the Windows Time service so it does not drift the clock
            // back toward the tampered value on its next sync.
            PowerShellRunner.Run("w32tm /resync /force");
        }
        else
        {
            ServiceLog.Write("time guard: SetSystemTime failed; clock not corrected");
        }
    }

    /// <summary>
    /// Win32 <c>SYSTEMTIME</c> structure. All fields are UTC when passed to
    /// <see cref="SetSystemTime"/>; <c>wDayOfWeek</c> is ignored by the API and
    /// left at zero.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSystemTime(ref SYSTEMTIME lpSystemTime);

    /// <summary>
    /// Writes the system clock from a UTC instant.
    /// </summary>
    /// <param name="utc">The trusted time to set, in UTC.</param>
    /// <returns>
    /// <c>true</c> when the clock was set; <c>false</c> when the underlying
    /// <c>SetSystemTime</c> call failed (e.g. the privilege is not held). The
    /// Win32 error is recorded to the service log on failure.
    /// </returns>
    private static bool SetSystemClock(DateTimeOffset utc)
    {
        var st = new SYSTEMTIME
        {
            wYear = (ushort)utc.Year,
            wMonth = (ushort)utc.Month,
            wDay = (ushort)utc.Day,
            wHour = (ushort)utc.Hour,
            wMinute = (ushort)utc.Minute,
            wSecond = (ushort)utc.Second,
            wMilliseconds = (ushort)utc.Millisecond,
        };

        if (SetSystemTime(ref st)) return true;

        ServiceLog.Write($"time guard: SetSystemTime Win32 error {Marshal.GetLastWin32Error()}");
        return false;
    }
}

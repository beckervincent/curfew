using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Curfew.Core;

namespace Curfew.Service;

/// <summary>Time Manipulation Guarding. Query trusted NTP, if local clock moved beyond tolerance overwrite with trusted time and force Windows Time resync. Runs as SYSTEM holding <c>SE_SYSTEMTIME_NAME</c> needed by <c>SetSystemTime</c>.</summary>
/// <remarks>
/// Pure decision logic (tolerance, tamper direction) lives in <see cref="TimeGuard"/> in Core and unit-tested there; this type only does privileged side effects — network + writing clock — and tolerates transient failures so flaky/offline machine never penalised.
/// </remarks>
internal static class TimeGuardService
{
    /// <summary>Well-known UDP port for NTP/SNTP (RFC 4330).</summary>
    private const int NtpPort = 123;

    /// <summary>Query single NTP server for current trusted time.</summary>
    /// <param name="host">NTP server hostname or IP.</param>
    /// <param name="timeoutMs">Send/receive timeout for UDP exchange, ms.</param>
    /// <returns>Server transmit timestamp, or <c>null</c> on any failure (DNS, network, timeout, malformed reply). Never throws.</returns>
    public static DateTimeOffset? QueryNtp(string host, int timeoutMs = 3000)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = timeoutMs;
            udp.Client.SendTimeout = timeoutMs;

            // Connect() resolves host, pins remote endpoint; socket accepts datagrams only from this server
            udp.Connect(host, NtpPort);
            udp.Send(Sntp.BuildRequest());

            // socket connected, so anything but queried server dropped at OS layer before reaching us
            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            var data = udp.Receive(ref endpoint);

            // Sntp.ParseReply enforces min packet size, rejects all-zero "unspecified" timestamp; throws on malformed
            return Sntp.ParseReply(data);
        }
        catch (SocketException)
        {
            // unreachable host, DNS fail, receive timeout — expected blocked/offline; fall through to next server
            return null;
        }
        catch (Exception ex)
        {
            // malformed reply (ArgumentException) or other unexpected. log, never propagate
            ServiceLog.Write($"time guard: NTP query to '{host}' failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Trusted time corroborated across <see cref="Sntp.DefaultServers"/>: query every server, trust only when at least <see cref="TimeGuard.MinAgreeingSources"/> agree (see <see cref="TimeGuard.Corroborate"/>). Single spoofed/redirected server cannot move clock; offline machine (too few answer) left alone (fail closed: no correction).</summary>
    /// <returns>Corroborated trusted time, or <c>null</c> when too few servers agree.</returns>
    public static DateTimeOffset? TrustedNow()
    {
        var samples = new List<DateTimeOffset>();
        foreach (var host in Sntp.DefaultServers)
        {
            var time = QueryNtp(host);
            if (time is not null) samples.Add(time.Value);
        }

        var trusted = TimeGuard.Corroborate(samples);
        if (trusted is null)
        {
            ServiceLog.Write(
                $"time guard: only {samples.Count} NTP source(s) answered and too few agreed " +
                $"(need {TimeGuard.MinAgreeingSources} within {TimeGuard.AgreementWindow.TotalSeconds:0}s); " +
                "skipping clock check");
        }

        return trusted;
    }

    /// <summary>Check local clock vs trusted time, correct on tamper. No-op when NTP unreachable (offline not penalised) or clock within tolerance.</summary>
    public static void Enforce()
    {
        var trusted = TrustedNow();
        if (trusted is null) return;

        var verdict = TimeGuard.Evaluate(DateTimeOffset.Now, trusted.Value);
        if (!TimeGuard.ShouldCorrect(verdict)) return;

        // SetSystemTime takes UTC SYSTEMTIME regardless of machine time zone
        var trustedUtc = trusted.Value.ToUniversalTime();
        ServiceLog.Write(
            $"time guard: clock {verdict} (local now {DateTimeOffset.Now:O}); " +
            $"correcting to trusted {trustedUtc:O}");

        if (SetSystemClock(trustedUtc))
        {
            EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.ClockTamper, verdict.ToString());
            // re-anchor Windows Time so it does not drift back toward tampered value next sync
            PowerShellRunner.Run("w32tm /resync /force");
        }
        else
        {
            ServiceLog.Write("time guard: SetSystemTime failed; clock not corrected");
        }
    }

    /// <summary>Config key holding parent-approved Windows time-zone id.</summary>
    private const string ExpectedTimeZoneKey = "expected_timezone";

    /// <summary>Pin machine time zone. Windows grants ordinary users "Change the time zone"; moving zone shifts every local-time decision (schedule windows, daily allowance date) without touching absolute clock <see cref="Enforce"/> watches — child can farm allowance or dodge curfew with <c>tzutil</c> alone. First run records current zone as expected; later drift logged as tampering and reverted.</summary>
    /// <param name="settings">Config-writable settings store (the service's).</param>
    public static void EnforceTimeZone(SettingsStore settings)
    {
        try
        {
            // re-read registry: TimeZoneInfo.Local cached per process, never sees change made after service started
            TimeZoneInfo.ClearCachedData();
            var current = TimeZoneInfo.Local.Id;

            var expected = settings.Get(ExpectedTimeZoneKey);
            if (string.IsNullOrEmpty(expected))
            {
                settings.Set(ExpectedTimeZoneKey, current);
                return;
            }

            if (string.Equals(current, expected, StringComparison.Ordinal)) return;

            ServiceLog.Write($"time guard: time zone changed to '{current}' (expected '{expected}'); reverting");
            EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.ClockTamper, $"timezone {current}");

            // tzutil needs id quoted (names have spaces). id from registry via TimeZoneInfo, never user input, but reject quotes anyway vs arg injection
            if (!expected.Contains('"'))
            {
                PowerShellRunner.Run($"tzutil /s \"{expected}\"");
                TimeZoneInfo.ClearCachedData();
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"time guard: time zone check failed: {ex.Message}");
        }
    }

    /// <summary>Win32 <c>SYSTEMTIME</c>. All fields UTC when passed to <see cref="SetSystemTime"/>; <c>wDayOfWeek</c> ignored by API, left zero.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSystemTime(ref SYSTEMTIME lpSystemTime);

    /// <summary>Write system clock from UTC instant.</summary>
    /// <param name="utc">Trusted time to set, UTC.</param>
    /// <returns><c>true</c> when clock set; <c>false</c> when <c>SetSystemTime</c> failed (e.g. privilege not held). Win32 error logged on failure.</returns>
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

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Curfew.Core;

namespace Curfew.Service;

/// <summary>
/// Time Manipulation Guarding. Queries trusted NTP time and, if the local clock
/// has been moved beyond tolerance, overwrites it with the trusted time and
/// forces a Windows Time resync. Runs as SYSTEM, which holds the privilege
/// required by <c>SetSystemTime</c>.
/// </summary>
internal static class TimeGuardService
{
    /// <summary>Queries one NTP server; null on any failure.</summary>
    public static DateTimeOffset? QueryNtp(string host, int timeoutMs = 3000)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = timeoutMs;
            udp.Client.SendTimeout = timeoutMs;
            udp.Connect(host, 123);
            udp.Send(Sntp.BuildRequest());

            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            var data = udp.Receive(ref endpoint);
            return Sntp.ParseReply(data);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>First server that answers, or null when all fail.</summary>
    public static DateTimeOffset? TrustedNow()
    {
        foreach (var host in Sntp.DefaultServers)
        {
            var time = QueryNtp(host);
            if (time is not null) return time;
        }
        return null;
    }

    /// <summary>Corrects the clock when tampering is detected. No-op when NTP is
    /// unreachable (offline machines are not penalised).</summary>
    public static void Enforce()
    {
        var trusted = TrustedNow();
        if (trusted is null) return;

        var verdict = TimeGuard.Evaluate(DateTimeOffset.Now, trusted.Value);
        if (TimeGuard.ShouldCorrect(verdict))
        {
            SetSystemClock(trusted.Value.ToUniversalTime());
            PowerShellRunner.Run("w32tm /resync /force");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetSystemTime(ref SYSTEMTIME lpSystemTime);

    private static void SetSystemClock(DateTimeOffset utc)
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
        SetSystemTime(ref st);
    }
}

using System.Buffers.Binary;

namespace Curfew.Core;

/// <summary>
/// Minimal SNTP (RFC 4330) packet building and parsing. Pure and testable; the
/// actual UDP exchange happens in the service. Used by Time Manipulation
/// Guarding to obtain a trusted wall-clock time.
/// </summary>
public static class Sntp
{
    /// <summary>Seconds between the NTP epoch (1900-01-01) and the Unix epoch.</summary>
    private const long NtpToUnixSeconds = 2_208_988_800L;

    /// <summary>A 48-byte client request (LI=0, VN=4, Mode=3).</summary>
    public static byte[] BuildRequest()
    {
        var packet = new byte[48];
        packet[0] = 0x23; // 00 100 011
        return packet;
    }

    /// <summary>Parses the transmit timestamp from a 48-byte server reply.</summary>
    public static DateTimeOffset ParseReply(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 48)
            throw new ArgumentException("SNTP reply must be at least 48 bytes", nameof(reply));

        // Transmit Timestamp is at byte offset 40: 32-bit seconds + 32-bit fraction.
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(reply.Slice(40, 4));
        var fraction = BinaryPrimitives.ReadUInt32BigEndian(reply.Slice(44, 4));

        var unixSeconds = (long)seconds - NtpToUnixSeconds;
        var milliseconds = (long)(fraction * 1000.0 / uint.MaxValue);

        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).AddMilliseconds(milliseconds);
    }

    /// <summary>Default NTP servers, queried in order until one answers.</summary>
    public static readonly string[] DefaultServers =
    {
        "time.cloudflare.com",
        "time.windows.com",
        "pool.ntp.org",
    };
}

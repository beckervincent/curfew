using System.Buffers.Binary;

namespace Curfew.Core;

/// <summary>Minimal SNTP (RFC 4330) packet build + parse. Pure, testable; actual UDP exchange in the service. Time Manipulation Guarding uses it for trusted wall-clock time.</summary>
/// <remarks>
/// Only the bits to read the server's transmit timestamp. No networking, so unit tests exercise it with hand-crafted byte buffers.
/// </remarks>
public static class Sntp
{
    /// <summary>Fixed wire size, bytes, of an SNTP/NTP packet (RFC 4330 §4).</summary>
    public const int PacketSize = 48;

    /// <summary>Seconds between NTP epoch (1900-01-01) and Unix epoch (1970-01-01).</summary>
    private const long NtpToUnixSeconds = 2_208_988_800L;

    /// <summary>Divisor for 32-bit NTP fractional-seconds field. Fixed point, implied denominator 2^32 (one full second), so 0x8000_0000 = exactly half a second.</summary>
    private const double FractionScale = 4_294_967_296.0; // 2^32

    /// <summary>Byte offset of 64-bit Transmit Timestamp field in the packet.</summary>
    private const int TransmitTimestampOffset = 40;

    /// <summary>First byte of client request: Leap Indicator = 0 (no warning), Version = 4, Mode = 3 (client).</summary>
    private const byte ClientLeapVersionMode = 0x23; // 00 100 011

    /// <summary>Build 48-byte SNTP client request (LI=0, VN=4, Mode=3). Remaining fields left zero (valid for request — server fills them in on reply).</summary>
    /// <returns>Fresh caller-owned 48-byte request buffer.</returns>
    public static byte[] BuildRequest()
    {
        var packet = new byte[PacketSize];
        packet[0] = ClientLeapVersionMode;
        return packet;
    }

    /// <summary>Parse Transmit Timestamp (moment server sent reply) from an SNTP server response.</summary>
    /// <param name="reply">Raw datagram from server; at least 48 bytes.</param>
    /// <returns>Server's transmit time as UTC <see cref="DateTimeOffset"/>.</returns>
    /// <exception cref="ArgumentException">When <paramref name="reply"/> shorter than 48 bytes or its Transmit Timestamp is the NTP "unspecified" (all-zero) value.</exception>
    public static DateTimeOffset ParseReply(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < PacketSize)
            throw new ArgumentException($"SNTP reply must be at least {PacketSize} bytes", nameof(reply));

        // Transmit Timestamp: 32-bit seconds since NTP epoch, then 32-bit fraction
        // of a second.
        var timestamp = reply.Slice(TransmitTimestampOffset, 8);
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(timestamp.Slice(0, 4));
        var fraction = BinaryPrimitives.ReadUInt32BigEndian(timestamp.Slice(4, 4));

        // all-zero transmit timestamp = NTP "unspecified"; malformed reply, not a
        // silent 1900-01-01.
        if (seconds == 0 && fraction == 0)
            throw new ArgumentException("SNTP reply has an unset (zero) Transmit Timestamp", nameof(reply));

        // NTP 32-bit seconds field wraps Feb 2036 (end of era 0). values below the
        // 1970 offset are era-1 timestamps, not 1900-era; without the pivot every
        // server would "agree" on ~1900 after rollover and the time guard would
        // force-set the clock 136 years back.
        var unixSeconds = (long)seconds
            + (seconds < NtpToUnixSeconds ? 0x1_0000_0000L : 0L)
            - NtpToUnixSeconds;

        // fractional part to ticks (100 ns) for sub-millisecond precision instead
        // of rounding straight to whole milliseconds.
        var subSecondTicks = (long)(fraction / FractionScale * TimeSpan.TicksPerSecond);

        return DateTimeOffset
            .FromUnixTimeSeconds(unixSeconds)
            .AddTicks(subSecondTicks);
    }

    /// <summary>Default NTP servers, three independent operators. Time guard queries all, needs several to agree (see <see cref="TimeGuard.Corroborate"/>), so spoofing one — e.g. via hosts file — won't move the clock.</summary>
    public static readonly string[] DefaultServers =
    {
        "time.cloudflare.com",
        "time.google.com",
        "pool.ntp.org",
    };
}

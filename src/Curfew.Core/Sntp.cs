using System.Buffers.Binary;

namespace Curfew.Core;

/// <summary>
/// Minimal SNTP (RFC 4330) packet building and parsing. Pure and testable; the
/// actual UDP exchange happens in the service. Used by Time Manipulation
/// Guarding to obtain a trusted wall-clock time.
/// </summary>
/// <remarks>
/// Only the pieces required to read the server's transmit timestamp are
/// implemented. The class is deliberately free of any networking so it can be
/// exercised by unit tests with hand-crafted byte buffers.
/// </remarks>
public static class Sntp
{
    /// <summary>The fixed wire size, in bytes, of an SNTP/NTP packet (RFC 4330 §4).</summary>
    public const int PacketSize = 48;

    /// <summary>Seconds between the NTP epoch (1900-01-01) and the Unix epoch (1970-01-01).</summary>
    private const long NtpToUnixSeconds = 2_208_988_800L;

    /// <summary>
    /// Divisor for the 32-bit NTP fractional-seconds field. The fraction is fixed
    /// point with an implied denominator of 2^32 (one full second), so a value of
    /// 0x8000_0000 means exactly half a second.
    /// </summary>
    private const double FractionScale = 4_294_967_296.0; // 2^32

    /// <summary>Byte offset of the 64-bit Transmit Timestamp field within the packet.</summary>
    private const int TransmitTimestampOffset = 40;

    /// <summary>
    /// First byte of a client request: Leap Indicator = 0 (no warning),
    /// Version = 4, Mode = 3 (client).
    /// </summary>
    private const byte ClientLeapVersionMode = 0x23; // 00 100 011

    /// <summary>
    /// Builds a 48-byte SNTP client request (LI=0, VN=4, Mode=3). All remaining
    /// fields are left zero, which is valid for a request — the server fills them
    /// in on the reply.
    /// </summary>
    /// <returns>A fresh, caller-owned 48-byte request buffer.</returns>
    public static byte[] BuildRequest()
    {
        var packet = new byte[PacketSize];
        packet[0] = ClientLeapVersionMode;
        return packet;
    }

    /// <summary>
    /// Parses the Transmit Timestamp (the moment the server sent the reply) from
    /// an SNTP server response.
    /// </summary>
    /// <param name="reply">The raw datagram received from the server; must be at least 48 bytes.</param>
    /// <returns>The server's transmit time as a UTC <see cref="DateTimeOffset"/>.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="reply"/> is shorter than 48 bytes or when its
    /// Transmit Timestamp is the NTP "unspecified" (all-zero) value.
    /// </exception>
    public static DateTimeOffset ParseReply(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < PacketSize)
            throw new ArgumentException($"SNTP reply must be at least {PacketSize} bytes", nameof(reply));

        // Transmit Timestamp: 32-bit seconds since the NTP epoch followed by a
        // 32-bit fraction of a second.
        var timestamp = reply.Slice(TransmitTimestampOffset, 8);
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(timestamp.Slice(0, 4));
        var fraction = BinaryPrimitives.ReadUInt32BigEndian(timestamp.Slice(4, 4));

        // An all-zero transmit timestamp is the NTP "unspecified" value; treat it
        // as a malformed reply rather than silently returning 1900-01-01.
        if (seconds == 0 && fraction == 0)
            throw new ArgumentException("SNTP reply has an unset (zero) Transmit Timestamp", nameof(reply));

        var unixSeconds = (long)seconds - NtpToUnixSeconds;

        // Convert the fractional part to ticks (100 ns) for sub-millisecond
        // precision instead of rounding straight to whole milliseconds.
        var subSecondTicks = (long)(fraction / FractionScale * TimeSpan.TicksPerSecond);

        return DateTimeOffset
            .FromUnixTimeSeconds(unixSeconds)
            .AddTicks(subSecondTicks);
    }

    /// <summary>Default NTP servers, queried in order until one answers.</summary>
    public static readonly string[] DefaultServers =
    {
        "time.cloudflare.com",
        "time.windows.com",
        "pool.ntp.org",
    };
}

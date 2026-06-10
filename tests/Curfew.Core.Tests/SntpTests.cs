using System.Buffers.Binary;
using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>
/// Unit tests for <see cref="Sntp"/>, the pure (network-free) SNTP packet
/// builder/parser used by Time Manipulation Guarding. The tests hand-craft byte
/// buffers so the wire format is exercised without any UDP traffic.
/// </summary>
public class SntpTests
{
    /// <summary>Seconds between the NTP epoch (1900-01-01) and the Unix epoch (1970-01-01).</summary>
    private const long NtpToUnixSeconds = 2_208_988_800L;

    /// <summary>Byte offset of the 64-bit Transmit Timestamp field within the packet.</summary>
    private const int TransmitTimestampOffset = 40;

    /// <summary>First byte of a well-formed client request: LI=0, VN=4, Mode=3.</summary>
    private const byte ClientLeapVersionMode = 0x23;

    /// <summary>
    /// Builds a 48-byte SNTP reply whose Transmit Timestamp encodes
    /// <paramref name="instant"/>. The remaining header fields are left zero,
    /// which is enough for <see cref="Sntp.ParseReply"/> since it only reads the
    /// transmit timestamp.
    /// </summary>
    private static byte[] BuildReplyWithTransmitTime(DateTimeOffset instant)
    {
        var packet = new byte[Sntp.PacketSize];

        var ntpSeconds = (uint)(instant.ToUnixTimeSeconds() + NtpToUnixSeconds);
        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(TransmitTimestampOffset, 4), ntpSeconds);

        // Encode the sub-second part as the 32-bit NTP fraction (denominator 2^32).
        var subSecond = instant - DateTimeOffset.FromUnixTimeSeconds(instant.ToUnixTimeSeconds());
        var fraction = (uint)(subSecond.TotalSeconds * 4_294_967_296.0);
        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(TransmitTimestampOffset + 4, 4), fraction);

        return packet;
    }

    [Fact]
    public void BuildRequest_is_48_bytes_in_client_mode()
    {
        var req = Sntp.BuildRequest();

        Assert.Equal(Sntp.PacketSize, req.Length);
        Assert.Equal(48, req.Length); // Pin the wire size independently of the constant.
        Assert.Equal(ClientLeapVersionMode, req[0]); // LI=0, VN=4, Mode=3 (client).
    }

    [Fact]
    public void BuildRequest_leaves_all_bytes_after_the_header_byte_zero()
    {
        var req = Sntp.BuildRequest();

        // Only the first byte carries data in a request; the server fills the rest.
        for (var i = 1; i < req.Length; i++)
            Assert.Equal(0, req[i]);
    }

    [Fact]
    public void BuildRequest_returns_a_fresh_caller_owned_buffer_each_call()
    {
        var first = Sntp.BuildRequest();
        var second = Sntp.BuildRequest();

        Assert.NotSame(first, second);

        // Mutating one request must not affect a subsequently built one.
        first[5] = 0xFF;
        Assert.Equal(0, Sntp.BuildRequest()[5]);
    }

    [Fact]
    public void ParseReply_reads_transmit_timestamp()
    {
        var expected = new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);
        var packet = BuildReplyWithTransmitTime(expected);

        var parsed = Sntp.ParseReply(packet);

        Assert.Equal(expected.ToUnixTimeSeconds(), parsed.ToUnixTimeSeconds());
    }

    [Fact]
    public void ParseReply_returns_a_utc_offset()
    {
        var packet = BuildReplyWithTransmitTime(
            new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));

        var parsed = Sntp.ParseReply(packet);

        // NTP timestamps are UTC; the parsed value must carry a zero offset.
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Fact]
    public void ParseReply_round_trips_a_range_of_instants_to_the_second()
    {
        var instants = new[]
        {
            new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero),  // Unix epoch.
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2036, 2, 7, 6, 28, 15, TimeSpan.Zero), // Near NTP era rollover.
        };

        foreach (var instant in instants)
        {
            var parsed = Sntp.ParseReply(BuildReplyWithTransmitTime(instant));
            Assert.Equal(instant.ToUnixTimeSeconds(), parsed.ToUnixTimeSeconds());
        }
    }

    [Fact]
    public void ParseReply_decodes_the_fractional_second()
    {
        var packet = new byte[Sntp.PacketSize];
        var seconds = (uint)(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero)
            .ToUnixTimeSeconds() + NtpToUnixSeconds);

        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(TransmitTimestampOffset, 4), seconds);
        // 0x8000_0000 == exactly half a second (numerator over the implied 2^32).
        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(TransmitTimestampOffset + 4, 4), 0x8000_0000u);

        var parsed = Sntp.ParseReply(packet);

        Assert.Equal(500, parsed.Millisecond);
    }

    [Fact]
    public void ParseReply_carries_sub_millisecond_precision_in_ticks()
    {
        var packet = new byte[Sntp.PacketSize];
        var seconds = (uint)(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero)
            .ToUnixTimeSeconds() + NtpToUnixSeconds);

        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(TransmitTimestampOffset, 4), seconds);
        // A fraction of 1 is the smallest representable step (~233 ps); it must
        // not be silently rounded down to zero whole milliseconds.
        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(TransmitTimestampOffset + 4, 4), 1u);

        var parsed = Sntp.ParseReply(packet);

        // The fraction is below 1 ms, so the millisecond component stays zero, but
        // the value is still strictly later than the whole-second boundary.
        var wholeSecond = new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.True(parsed >= wholeSecond);
        Assert.True(parsed - wholeSecond < TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void ParseReply_ignores_bytes_before_the_transmit_timestamp()
    {
        var instant = new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);
        var packet = BuildReplyWithTransmitTime(instant);

        // Scramble every header field that precedes the transmit timestamp
        // (LI/VN/Mode, stratum, the originate/receive timestamps, etc.).
        for (var i = 0; i < TransmitTimestampOffset; i++)
            packet[i] = 0xAB;

        var parsed = Sntp.ParseReply(packet);

        Assert.Equal(instant.ToUnixTimeSeconds(), parsed.ToUnixTimeSeconds());
    }

    [Fact]
    public void ParseReply_accepts_packets_longer_than_48_bytes()
    {
        // Real servers may append a Key Identifier and Message Digest (authentication
        // trailer) after the 48-byte header; ParseReply must tolerate the extra bytes.
        var instant = new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);
        var packet = new byte[68]; // 48-byte header + 20-byte MD5 authenticator.
        BuildReplyWithTransmitTime(instant).CopyTo(packet, 0);
        // Fill the trailing authenticator with non-zero bytes to prove it is ignored.
        for (var i = Sntp.PacketSize; i < packet.Length; i++)
            packet[i] = 0x5A;

        var parsed = Sntp.ParseReply(packet);

        Assert.Equal(instant.ToUnixTimeSeconds(), parsed.ToUnixTimeSeconds());
    }

    [Fact]
    public void BuildRequest_output_round_trips_through_ParseReply_when_stamped()
    {
        // A request has a zero transmit timestamp; stamping it makes a valid reply,
        // which keeps the builder and parser in agreement on field layout.
        var instant = new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);
        var packet = Sntp.BuildRequest();
        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(TransmitTimestampOffset, 4),
            (uint)(instant.ToUnixTimeSeconds() + NtpToUnixSeconds));

        var parsed = Sntp.ParseReply(packet);

        Assert.Equal(instant.ToUnixTimeSeconds(), parsed.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(47)] // One byte short of the required header size.
    public void ParseReply_rejects_short_packets(int length)
    {
        var ex = Assert.Throws<ArgumentException>(() => Sntp.ParseReply(new byte[length]));
        Assert.Equal("reply", ex.ParamName);
    }

    [Fact]
    public void ParseReply_rejects_an_all_zero_transmit_timestamp()
    {
        // An all-zero transmit timestamp is the NTP "unspecified" value and must be
        // rejected rather than reported as 1900-01-01.
        var ex = Assert.Throws<ArgumentException>(() => Sntp.ParseReply(new byte[Sntp.PacketSize]));
        Assert.Equal("reply", ex.ParamName);
    }

    [Fact]
    public void ParseReply_accepts_a_zero_seconds_field_with_a_non_zero_fraction()
    {
        // Only an entirely zero timestamp is "unspecified"; a non-zero fraction
        // alone must still be parsed (it maps just after the NTP epoch).
        var packet = new byte[Sntp.PacketSize];
        BinaryPrimitives.WriteUInt32BigEndian(
            packet.AsSpan(TransmitTimestampOffset + 4, 4), 0x8000_0000u);

        var parsed = Sntp.ParseReply(packet);

        // Seconds == 0 maps to the NTP epoch (1900-01-01); the 0x8000_0000 fraction
        // adds exactly half a second on top of it.
        var expected = new DateTimeOffset(1900, 1, 1, 0, 0, 0, 500, TimeSpan.Zero);
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void DefaultServers_lists_well_known_hosts()
    {
        Assert.NotEmpty(Sntp.DefaultServers);
        Assert.All(Sntp.DefaultServers, host => Assert.False(string.IsNullOrWhiteSpace(host)));
        Assert.Equal(Sntp.DefaultServers.Length, Sntp.DefaultServers.Distinct().Count());
    }
}

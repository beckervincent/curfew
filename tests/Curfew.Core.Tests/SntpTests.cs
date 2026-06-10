using System.Buffers.Binary;
using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class SntpTests
{
    [Fact]
    public void Request_is_48_bytes_client_mode()
    {
        var req = Sntp.BuildRequest();
        Assert.Equal(48, req.Length);
        Assert.Equal(0x23, req[0]); // LI=0, VN=4, Mode=3 (client)
    }

    [Fact]
    public void ParseReply_reads_transmit_timestamp()
    {
        // Build a reply whose transmit timestamp is a known instant.
        var expected = new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);
        var ntpSeconds = (uint)(expected.ToUnixTimeSeconds() + 2_208_988_800L);

        var packet = new byte[48];
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(40, 4), ntpSeconds);

        var parsed = Sntp.ParseReply(packet);
        Assert.Equal(expected.ToUnixTimeSeconds(), parsed.ToUnixTimeSeconds());
    }

    [Fact]
    public void ParseReply_rejects_short_packets()
    {
        Assert.Throws<ArgumentException>(() => Sntp.ParseReply(new byte[10]));
    }
}

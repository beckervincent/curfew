using Curfew.Core.Security;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>
/// Tests for <see cref="UnlockCode"/> (RFC 6238 TOTP) and <see cref="Base32"/>.
/// The known-answer cases use the secret from RFC 6238 Appendix B
/// ("12345678901234567890"), whose base32 form is the value below.
/// </summary>
public class UnlockCodeTests
{
    // base32("12345678901234567890")
    private const string RfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59L, "287082")]            // RFC 6238 vector (8-digit 94287082, truncated to 6)
    [InlineData(1111111109L, "081804")]    // RFC 6238 vector (8-digit 07081804)
    [InlineData(1111111111L, "050471")]
    public void Generate_matches_rfc6238_vectors(long unixSeconds, string expected)
    {
        Assert.Equal(expected, UnlockCode.Generate(RfcSecret, unixSeconds));
    }

    [Fact]
    public void Generated_code_is_six_digits()
    {
        var code = UnlockCode.Generate(RfcSecret, 0);
        Assert.Equal(6, code.Length);
        Assert.All(code, c => Assert.True(char.IsAsciiDigit(c)));
    }

    [Fact]
    public void Verify_accepts_the_current_code()
    {
        var now = 1_700_000_000L;
        var code = UnlockCode.Generate(RfcSecret, now);
        Assert.True(UnlockCode.Verify(RfcSecret, code, now));
    }

    [Fact]
    public void Verify_accepts_one_step_of_skew_within_window()
    {
        var now = 1_700_000_000L;
        var earlier = UnlockCode.Generate(RfcSecret, now - UnlockCode.StepSeconds);
        Assert.True(UnlockCode.Verify(RfcSecret, earlier, now, window: 1));
    }

    [Fact]
    public void Verify_rejects_codes_outside_the_window()
    {
        var now = 1_700_000_000L;
        var stale = UnlockCode.Generate(RfcSecret, now - 5 * UnlockCode.StepSeconds);
        Assert.False(UnlockCode.Verify(RfcSecret, stale, now, window: 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]    // too short
    [InlineData("1234567")]  // too long
    [InlineData("12ab56")]   // non-digit
    public void Verify_rejects_malformed_codes(string? code)
    {
        Assert.False(UnlockCode.Verify(RfcSecret, code, 1_700_000_000L));
    }

    [Fact]
    public void Verify_with_replay_guard_reports_and_blocks_reuse()
    {
        var now = 1_700_000_000L;
        var code = UnlockCode.Generate(RfcSecret, now);

        Assert.True(UnlockCode.Verify(RfcSecret, code, now, 1, long.MinValue, out var counter));
        Assert.Equal(now / UnlockCode.StepSeconds, counter);

        // Re-using the same code once its counter has been recorded must fail.
        Assert.False(UnlockCode.Verify(RfcSecret, code, now, 1, counter, out _));
    }

    [Fact]
    public void Verify_rejects_a_wrong_secret()
    {
        var now = 1_700_000_000L;
        var code = UnlockCode.Generate(RfcSecret, now);
        var otherSecret = UnlockCode.GenerateSecret();
        Assert.False(UnlockCode.Verify(otherSecret, code, now));
    }

    [Fact]
    public void GenerateSecret_round_trips_and_is_usable()
    {
        var secret = UnlockCode.GenerateSecret();
        Assert.NotEmpty(secret);
        Assert.All(secret, c => Assert.Contains(c, "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"));

        var now = 1_700_000_000L;
        Assert.True(UnlockCode.Verify(secret, UnlockCode.Generate(secret, now), now));
    }

    [Theory]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF })]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    public void Base32_round_trips(byte[] data)
    {
        Assert.Equal(data, Base32.Decode(Base32.Encode(data)));
    }

    [Fact]
    public void Base32_decode_is_lenient_with_spaces_and_case()
    {
        var canonical = Base32.Decode("GEZDGNBV");
        Assert.Equal(canonical, Base32.Decode("gezd gnbv"));
        Assert.Equal(canonical, Base32.Decode("GEZD=GNBV="));
    }

    [Fact]
    public void Base32_decode_rejects_invalid_characters()
    {
        Assert.Throws<FormatException>(() => Base32.Decode("0189")); // 0,1,8,9 not in alphabet
    }
}

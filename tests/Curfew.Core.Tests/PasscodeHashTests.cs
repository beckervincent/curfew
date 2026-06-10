using Curfew.Core.Security;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>Tests for <see cref="PasscodeHash"/> (PBKDF2 passcode hashing).</summary>
public class PasscodeHashTests
{
    [Theory]
    [InlineData("1234")]
    [InlineData("correct horse battery staple")]
    [InlineData("Pa$$w0rd!")]
    [InlineData("üñîçødé")]
    public void Hash_then_verify_accepts_the_right_passcode(string passcode)
    {
        var stored = PasscodeHash.Hash(passcode);
        Assert.True(PasscodeHash.IsHashed(stored));
        Assert.True(PasscodeHash.Verify(passcode, stored));
    }

    [Fact]
    public void Verify_rejects_the_wrong_passcode()
    {
        var stored = PasscodeHash.Hash("1234");
        Assert.False(PasscodeHash.Verify("1235", stored));
        Assert.False(PasscodeHash.Verify("", stored));
        Assert.False(PasscodeHash.Verify(null, stored));
    }

    [Fact]
    public void Each_hash_uses_a_fresh_salt()
    {
        Assert.NotEqual(PasscodeHash.Hash("1234"), PasscodeHash.Hash("1234"));
    }

    [Fact]
    public void Legacy_plaintext_value_still_verifies()
    {
        // Existing installs store the PIN as plaintext; it must keep working.
        Assert.True(PasscodeHash.Verify("1234", "1234"));
        Assert.False(PasscodeHash.Verify("0000", "1234"));
        Assert.False(PasscodeHash.IsHashed("1234"));
    }

    [Theory]
    [InlineData("pbkdf2$")]
    [InlineData("pbkdf2$100000$notbase64$alsonot")]
    [InlineData("pbkdf2$abc$AAAA$BBBB")]
    [InlineData("pbkdf2$100000$AAAA")]
    public void Verify_rejects_malformed_hash_strings(string stored)
    {
        Assert.False(PasscodeHash.Verify("1234", stored));
    }

    [Fact]
    public void Verify_against_empty_or_null_store_is_false()
    {
        Assert.False(PasscodeHash.Verify("1234", null));
        Assert.False(PasscodeHash.Verify("1234", ""));
    }
}

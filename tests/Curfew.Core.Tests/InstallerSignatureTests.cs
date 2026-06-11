using System.Text.RegularExpressions;
using Curfew.Core.Security;
using Xunit;

namespace Curfew.Core.Tests;

public class InstallerSignatureTests
{
    [Fact]
    public void Verify_fails_closed_for_a_missing_file()
    {
        Assert.False(InstallerSignature.Verify("/no/such/installer.exe"));
        Assert.False(InstallerSignature.Verify(""));
        Assert.False(InstallerSignature.Verify("   "));
    }

    [Fact]
    public void Pinned_public_key_is_a_sha256_hex_string()
    {
        // 64 uppercase hex chars — guards against an accidentally truncated or
        // wrongly-cased pin (the runtime compares against uppercase ToHexString).
        Assert.Matches(new Regex("^[0-9A-F]{64}$"), InstallerSignature.PinnedPublicKeySha256);
    }
}

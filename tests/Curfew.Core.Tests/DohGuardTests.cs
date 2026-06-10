using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class DohGuardTests
{
    [Fact]
    public void Block_script_targets_known_resolvers_and_ports()
    {
        var script = DohGuard.BuildBlockScript();
        Assert.Contains("8.8.8.8", script);   // Google
        Assert.Contains("9.9.9.9", script);   // Quad9
        Assert.Contains("443,853", script);
        Assert.Contains("New-NetFirewallRule", script);
    }

    [Fact]
    public void Cloudflare_is_never_blocked()
    {
        // Blocking the resolver Curfew itself uses would break filtering.
        Assert.DoesNotContain("1.1.1.", DohGuard.BuildBlockScript());
        Assert.DoesNotContain("1.0.0.", DohGuard.BuildBlockScript());
    }

    [Fact]
    public void Clear_script_removes_rules()
    {
        var script = DohGuard.BuildClearScript();
        Assert.Contains("Remove-NetFirewallRule", script);
        Assert.Contains(DohGuard.RulePrefix, script);
    }
}

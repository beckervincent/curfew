using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>Tests for <see cref="DohGuard"/>, generating PowerShell that blocks well-known third-party DNS-over-HTTPS/TLS resolvers at Windows firewall. Scripts are pure strings here; service runs them as SYSTEM. Pin security-critical invariants (Cloudflare stays reachable, scripts idempotent, resolver/port contract honoured) so careless edit cannot silently weaken or break filtering.</summary>
public class DohGuardTests
{
    // ---- BuildBlockScript: resolver and port coverage -------------------

    [Fact]
    public void Block_script_targets_known_resolvers_and_ports()
    {
        var script = DohGuard.BuildBlockScript();

        Assert.Contains("8.8.8.8", script);                 // Google
        Assert.Contains("9.9.9.9", script);                 // Quad9
        Assert.Contains("208.67.222.222", script);          // OpenDNS
        Assert.Contains("94.140.14.14", script);            // AdGuard
        Assert.Contains(DohGuard.BlockedPorts, script);     // 443,853
        Assert.Contains("New-NetFirewallRule", script);
    }

    [Fact]
    public void Block_script_contains_every_blocked_resolver()
    {
        // whole point: *all* resolvers covered; a dropped resolver = silent bypass
        var script = DohGuard.BuildBlockScript();

        Assert.NotEmpty(DohGuard.BlockedResolvers);
        foreach (var resolver in DohGuard.BlockedResolvers)
        {
            Assert.Contains(resolver, script);
        }
    }

    [Fact]
    public void Block_script_covers_ipv6_resolvers()
    {
        // dual-stack nets bypass if only IPv4 blocked; confirm IPv6 literals survive into script
        var script = DohGuard.BuildBlockScript();

        Assert.Contains("2001:4860:4860::8888", script);    // Google IPv6
        Assert.Contains("2620:fe::fe", script);             // Quad9 IPv6
    }

    [Fact]
    public void Block_script_includes_cidr_ranges()
    {
        // some providers (e.g. NextDNS) blocked by range not single host; slash notation must reach firewall rule intact
        var script = DohGuard.BuildBlockScript();

        Assert.Contains("45.90.28.0/24", script);
    }

    // ---- BuildBlockScript: Cloudflare must never be blocked -------------

    [Fact]
    public void Unfiltered_cloudflare_is_blocked_but_filtered_family_is_not()
    {
        // unfiltered Cloudflare (1.1.1.1 / 1.0.0.1 + IPv6 twins) = first-class bypass, must block. filtered family Curfew pins (1.1.1.2/1.1.1.3, 1.0.0.2/1.0.0.3) must stay usable
        var script = DohGuard.BuildBlockScript();

        Assert.Contains("'1.1.1.1'", script);
        Assert.Contains("'1.0.0.1'", script);
        Assert.Contains("2606:4700:4700::1111", script);
        Assert.Contains("2606:4700:4700::1001", script);

        Assert.DoesNotContain("1.1.1.2", script);
        Assert.DoesNotContain("1.1.1.3", script);
        Assert.DoesNotContain("1.0.0.2", script);
        Assert.DoesNotContain("1.0.0.3", script);
        Assert.DoesNotContain("2606:4700:4700::1112", script);
        Assert.DoesNotContain("2606:4700:4700::1113", script);
    }

    [Fact]
    public void Blocked_resolver_list_excludes_filtered_cloudflare_family()
    {
        // guard source-of-truth list directly: filtered family resolvers must never slip into blocklist, or content filtering breaks
        string[] filtered = { "1.1.1.2", "1.1.1.3", "1.0.0.2", "1.0.0.3" };
        foreach (var resolver in DohGuard.BlockedResolvers)
        {
            Assert.DoesNotContain(resolver, filtered);
        }

        // unfiltered pair must be present
        Assert.Contains("1.1.1.1", DohGuard.BlockedResolvers);
        Assert.Contains("1.0.0.1", DohGuard.BlockedResolvers);
    }

    // ---- BuildBlockScript: rule shape and idempotency ------------------

    [Fact]
    public void Block_script_creates_both_tcp_and_udp_rules()
    {
        // New-NetFirewallRule takes single protocol, so DoH/DoT needs one rule per protocol; missing either leaves a transport open
        var script = DohGuard.BuildBlockScript();

        Assert.Contains("-Protocol TCP", script);
        Assert.Contains("-Protocol UDP", script);
    }

    [Fact]
    public void Block_script_only_blocks_outbound_traffic()
    {
        var script = DohGuard.BuildBlockScript();

        Assert.Contains("-Direction Outbound", script);
        Assert.Contains("-Action Block", script);
        Assert.DoesNotContain("-Direction Inbound", script);
    }

    [Fact]
    public void Block_script_removes_existing_rules_first_for_idempotency()
    {
        // re-run on network change must refresh not duplicate, so block script clears prior Curfew rules before re-adding
        var script = DohGuard.BuildBlockScript();

        var removeIndex = script.IndexOf("Remove-NetFirewallRule", StringComparison.Ordinal);
        var addIndex = script.IndexOf("New-NetFirewallRule", StringComparison.Ordinal);

        Assert.NotEqual(-1, removeIndex);
        Assert.NotEqual(-1, addIndex);
        Assert.True(removeIndex < addIndex,
            "Existing rules must be removed before new rules are added.");
    }

    [Fact]
    public void Block_script_names_rules_with_the_shared_prefix()
    {
        // clear/refresh path finds rules by display-name prefix, so every rule block script creates must carry RulePrefix
        var script = DohGuard.BuildBlockScript();

        Assert.Contains(DohGuard.RulePrefix, script);
    }

    [Fact]
    public void Block_script_fails_closed_on_rule_creation_failure()
    {
        var script = DohGuard.BuildBlockScript();

        // runs under 'Stop' (failed New-NetFirewallRule aborts non-zero) + verifies rules exist after, exiting 1 if silent teardown left them missing
        Assert.Contains("$ErrorActionPreference = 'Stop'", script);
        Assert.Contains("exit 1", script);
        // removal step still tolerates "no such rule" on clean first run
        Assert.Contains("Remove-NetFirewallRule -ErrorAction SilentlyContinue", script);
    }

    // ---- BuildClearScript ----------------------------------------------

    [Fact]
    public void Clear_script_removes_rules()
    {
        var script = DohGuard.BuildClearScript();

        Assert.Contains("Remove-NetFirewallRule", script);
        Assert.Contains(DohGuard.RulePrefix, script);
    }

    [Fact]
    public void Clear_script_does_not_create_any_rules()
    {
        // clearing only tears down; never (re)installs a block rule
        var script = DohGuard.BuildClearScript();

        Assert.DoesNotContain("New-NetFirewallRule", script);
    }

    [Fact]
    public void Clear_script_does_not_reference_resolver_addresses()
    {
        // pure teardown matches by rule name only, so no resolver IP in clear script
        var script = DohGuard.BuildClearScript();

        foreach (var resolver in DohGuard.BlockedResolvers)
        {
            Assert.DoesNotContain(resolver, script);
        }
    }

    [Fact]
    public void Block_and_clear_scripts_share_the_same_removal_command()
    {
        // both paths target exact same rules, else refresh leaves stale rules clear cannot remove (or vice versa)
        var removal = $"Get-NetFirewallRule -DisplayName '{DohGuard.RulePrefix}*'";

        Assert.Contains(removal, DohGuard.BuildBlockScript());
        Assert.Contains(removal, DohGuard.BuildClearScript());
    }

    // ---- Constants: stable cross-release contract ----------------------

    [Fact]
    public void Rule_prefix_is_the_documented_constant()
    {
        // other components + prior installs find rules by this exact prefix; changing it orphans rules from earlier versions
        Assert.Equal("Curfew-Block-DoH", DohGuard.RulePrefix);
    }

    [Fact]
    public void Blocked_ports_cover_doh_and_dot_but_not_plain_dns()
    {
        // 443 (DoH over HTTPS) + 853 (DoT); plain DNS port 53 stays open so OS resolver Curfew controls keeps working
        Assert.Equal("443,853", DohGuard.BlockedPorts);
        Assert.DoesNotContain("53", DohGuard.BlockedPorts.Split(','));
    }

    // ---- Determinism ---------------------------------------------------

    [Fact]
    public void Scripts_are_deterministic()
    {
        // pure generation: identical input yields byte-for-byte identical output, keeps SYSTEM-run scripts predictable
        Assert.Equal(DohGuard.BuildBlockScript(), DohGuard.BuildBlockScript());
        Assert.Equal(DohGuard.BuildClearScript(), DohGuard.BuildClearScript());
    }
}

using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>Tests for <see cref="ContentFilter"/> — pure host-agnostic helper mapping persisted <c>dns_filter_mode</c> onto Cloudflare resolvers, emitting PowerShell the service runs as SYSTEM.</summary>
public class ContentFilterTests
{
    // ---- Parse -------------------------------------------------------------

    [Theory]
    [InlineData("off", FilterMode.Off)]
    [InlineData("malware", FilterMode.Malware)]
    [InlineData("family", FilterMode.Family)]
    [InlineData(null, FilterMode.Off)]
    [InlineData("nonsense", FilterMode.Off)]
    public void Parse_maps_known_values_and_falls_back_to_off(string? value, FilterMode expected)
    {
        Assert.Equal(expected, ContentFilter.Parse(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Parse_treats_blank_input_as_off(string value)
    {
        // empty/whitespace = common "unset"; must never accidentally enable or change filter mode
        Assert.Equal(FilterMode.Off, ContentFilter.Parse(value));
    }

    [Theory]
    [InlineData("MALWARE", FilterMode.Malware)]
    [InlineData("Family", FilterMode.Family)]
    [InlineData("  off  ", FilterMode.Off)]
    [InlineData("\tFAMILY \n", FilterMode.Family)]
    public void Parse_is_case_insensitive_and_trims_whitespace(string value, FilterMode expected)
    {
        // contract: case-insensitive + tolerant of surrounding whitespace so hand-edited settings still parse
        Assert.Equal(expected, ContentFilter.Parse(value));
    }

    // ---- ToSetting ---------------------------------------------------------

    [Theory]
    [InlineData(FilterMode.Off, "off")]
    [InlineData(FilterMode.Malware, "malware")]
    [InlineData(FilterMode.Family, "family")]
    public void ToSetting_emits_the_persisted_literal(FilterMode mode, string expected)
    {
        // on-disk contract shared with WinUI app + service; stay lower-case, exactly as written
        Assert.Equal(expected, ContentFilter.ToSetting(mode));
    }

    [Theory]
    [InlineData(FilterMode.Off)]
    [InlineData(FilterMode.Malware)]
    [InlineData(FilterMode.Family)]
    public void ToSetting_then_Parse_roundtrips(FilterMode mode)
    {
        Assert.Equal(mode, ContentFilter.Parse(ContentFilter.ToSetting(mode)));
    }

    // ---- Servers -----------------------------------------------------------

    [Fact]
    public void Malware_mode_uses_security_servers()
    {
        var (v4, v6, doh) = ContentFilter.Servers(FilterMode.Malware);
        Assert.Equal(new[] { "1.1.1.2", "1.0.0.2" }, v4);
        Assert.Equal(new[] { "2606:4700:4700::1112", "2606:4700:4700::1002" }, v6);
        Assert.Equal("https://security.cloudflare-dns.com/dns-query", doh);
    }

    [Fact]
    public void Family_mode_uses_family_servers()
    {
        var (v4, v6, doh) = ContentFilter.Servers(FilterMode.Family);
        Assert.Equal(new[] { "1.1.1.3", "1.0.0.3" }, v4);
        Assert.Equal(new[] { "2606:4700:4700::1113", "2606:4700:4700::1003" }, v6);
        Assert.Equal("https://family.cloudflare-dns.com/dns-query", doh);
    }

    [Fact]
    public void Off_mode_yields_no_servers_and_no_doh_template()
    {
        // Off = "use adapter's own DHCP DNS", nothing to pin
        var (v4, v6, doh) = ContentFilter.Servers(FilterMode.Off);
        Assert.Empty(v4);
        Assert.Empty(v6);
        Assert.Equal(string.Empty, doh);
    }

    [Theory]
    [InlineData(FilterMode.Malware)]
    [InlineData(FilterMode.Family)]
    public void Servers_returns_defensive_copies(FilterMode mode)
    {
        // contract: each call returns fresh arrays so a caller sorting/mutating result cannot corrupt shared source of truth seen by other callers
        var (firstV4, firstV6, _) = ContentFilter.Servers(mode);
        firstV4[0] = "9.9.9.9";
        firstV6[0] = "::1";

        var (secondV4, secondV6, _) = ContentFilter.Servers(mode);
        Assert.NotEqual("9.9.9.9", secondV4[0]);
        Assert.NotEqual("::1", secondV6[0]);
    }

    // ---- BuildApplyScript: filtering on ------------------------------------

    [Fact]
    public void Apply_script_pins_servers_and_doh()
    {
        var script = ContentFilter.BuildApplyScript(FilterMode.Family);
        Assert.Contains("1.1.1.3", script);
        Assert.Contains("2606:4700:4700::1113", script);
        Assert.Contains("Set-DnsClientServerAddress", script);
        Assert.Contains("Add-DnsClientDohServerAddress", script);
        Assert.Contains("family.cloudflare-dns.com", script);
        Assert.Contains("Clear-DnsClientCache", script);
    }

    [Fact]
    public void Apply_script_pins_every_ipv4_and_ipv6_resolver()
    {
        var script = ContentFilter.BuildApplyScript(FilterMode.Malware);

        // all four resolvers must appear in $servers so adapter pinned to primary + secondary on each protocol
        var (v4, v6, _) = ContentFilter.Servers(FilterMode.Malware);
        foreach (var server in v4.Concat(v6))
        {
            Assert.Contains(server, script);
        }
    }

    [Fact]
    public void Apply_script_adds_one_doh_mapping_per_resolver()
    {
        var script = ContentFilter.BuildApplyScript(FilterMode.Family);
        var (v4, v6, _) = ContentFilter.Servers(FilterMode.Family);
        var expectedMappings = v4.Length + v6.Length;

        Assert.Equal(
            expectedMappings,
            CountOccurrences(script, "Add-DnsClientDohServerAddress"));
    }

    [Fact]
    public void Apply_script_enforces_encrypted_dns_only()
    {
        // DoH bindings must forbid clear-text fallback + auto-upgrade to encrypted DNS; weakening either flag leaks queries in plain text past filter
        var script = ContentFilter.BuildApplyScript(FilterMode.Family);
        Assert.Contains("-AllowFallbackToUdp $false", script);
        Assert.Contains("-AutoUpgrade $true", script);
    }

    [Fact]
    public void Apply_script_only_touches_active_physical_adapters()
    {
        // virtual/loopback adapters + down links skipped so we don't fight Windows over interfaces carrying no traffic
        var script = ContentFilter.BuildApplyScript(FilterMode.Family);
        Assert.Contains("Get-NetAdapter -Physical", script);
        Assert.Contains("$_.Status -eq 'Up'", script);
    }

    [Fact]
    public void Apply_script_fails_closed_on_pin_failure_but_tolerates_benign_steps()
    {
        // resolver pin is security-critical, runs under 'Stop', so failed Set-DnsClientServerAddress aborts non-zero (service logs). benign DoH registration + cache flush suppress own errors so idempotent re-run (duplicate DoH entry) not a failure
        var script = ContentFilter.BuildApplyScript(FilterMode.Family);
        Assert.Contains("$ErrorActionPreference = 'Stop'", script);
        Assert.Contains("Add-DnsClientDohServerAddress", script);
        Assert.Contains("-ErrorAction SilentlyContinue", script);
        Assert.DoesNotContain("$ErrorActionPreference = 'SilentlyContinue'", script);
    }

    [Fact]
    public void Apply_script_uses_only_lf_line_endings()
    {
        // output byte-for-byte deterministic across host OSes (generated even on non-Windows CI), so no CRLF leaks in
        var script = ContentFilter.BuildApplyScript(FilterMode.Family);
        Assert.DoesNotContain("\r", script);
    }

    [Theory]
    [InlineData(FilterMode.Malware, "1.0.0.3")]
    [InlineData(FilterMode.Family, "1.0.0.2")]
    public void Apply_script_excludes_other_modes_servers(FilterMode mode, string foreignServer)
    {
        // malware-mode script must not leak family-mode addresses + vice versa; distinct third octet (.2 vs .3) separates modes
        Assert.DoesNotContain(foreignServer, ContentFilter.BuildApplyScript(mode));
    }

    // ---- BuildApplyScript: filtering off -----------------------------------

    [Fact]
    public void Off_script_resets_to_dhcp()
    {
        var script = ContentFilter.BuildApplyScript(FilterMode.Off);
        Assert.Contains("-ResetServerAddresses", script);
        Assert.DoesNotContain("1.1.1.3", script);
    }

    [Fact]
    public void Off_script_does_not_pin_or_encrypt_dns()
    {
        // clearing filter must remove static servers + register no DoH bindings — else adapters stay pinned after disable
        var script = ContentFilter.BuildApplyScript(FilterMode.Off);
        Assert.DoesNotContain("-ServerAddresses", script);
        Assert.DoesNotContain("Add-DnsClientDohServerAddress", script);
        Assert.DoesNotContain("cloudflare-dns.com", script);
    }

    [Fact]
    public void Off_script_still_flushes_the_dns_cache()
    {
        // resolver cache flushed every mode so stale answer from previous config never served after a switch
        Assert.Contains("Clear-DnsClientCache", ContentFilter.BuildApplyScript(FilterMode.Off));
    }

    [Fact]
    public void Off_script_uses_only_lf_line_endings()
    {
        Assert.DoesNotContain("\r", ContentFilter.BuildApplyScript(FilterMode.Off));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>Count non-overlapping occurrences of <paramref name="needle"/> in <paramref name="haystack"/>.</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}

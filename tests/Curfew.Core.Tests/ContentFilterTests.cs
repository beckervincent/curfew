using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>
/// Unit tests for <see cref="ContentFilter"/> — the pure, host-agnostic helper
/// that maps the persisted <c>dns_filter_mode</c> setting onto Cloudflare's
/// resolvers and emits the PowerShell the service runs as SYSTEM.
/// </summary>
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
        // Empty/whitespace is the common "unset" representation and must never
        // accidentally enable or change a filter mode.
        Assert.Equal(FilterMode.Off, ContentFilter.Parse(value));
    }

    [Theory]
    [InlineData("MALWARE", FilterMode.Malware)]
    [InlineData("Family", FilterMode.Family)]
    [InlineData("  off  ", FilterMode.Off)]
    [InlineData("\tFAMILY \n", FilterMode.Family)]
    public void Parse_is_case_insensitive_and_trims_whitespace(string value, FilterMode expected)
    {
        // Documented contract: matching is case-insensitive and tolerant of
        // surrounding whitespace so hand-edited settings still parse correctly.
        Assert.Equal(expected, ContentFilter.Parse(value));
    }

    // ---- ToSetting ---------------------------------------------------------

    [Theory]
    [InlineData(FilterMode.Off, "off")]
    [InlineData(FilterMode.Malware, "malware")]
    [InlineData(FilterMode.Family, "family")]
    public void ToSetting_emits_the_persisted_literal(FilterMode mode, string expected)
    {
        // These literals are the on-disk contract shared with the WinUI app and
        // the service; they must stay lower-case and exactly as written.
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
        // Off means "use the adapter's own DHCP DNS", so there is nothing to pin.
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
        // Documented contract: each call hands back fresh arrays so a caller that
        // sorts or mutates the result cannot corrupt the shared source of truth
        // observed by every other caller.
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

        // All four configured resolvers must appear in the $servers list so the
        // adapter is pinned to both the primary and secondary on each protocol.
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
        // The DoH bindings must forbid clear-text fallback and auto-upgrade to
        // encrypted DNS; weakening either flag would let queries leak in plain
        // text past the filter.
        var script = ContentFilter.BuildApplyScript(FilterMode.Family);
        Assert.Contains("-AllowFallbackToUdp $false", script);
        Assert.Contains("-AutoUpgrade $true", script);
    }

    [Fact]
    public void Apply_script_only_touches_active_physical_adapters()
    {
        // Virtual/loopback adapters and down links are deliberately skipped so we
        // don't fight Windows over interfaces that aren't carrying traffic.
        var script = ContentFilter.BuildApplyScript(FilterMode.Family);
        Assert.Contains("Get-NetAdapter -Physical", script);
        Assert.Contains("$_.Status -eq 'Up'", script);
    }

    [Fact]
    public void Apply_script_fails_closed_on_pin_failure_but_tolerates_benign_steps()
    {
        // The resolver pin is security-critical and runs under 'Stop', so a failed
        // Set-DnsClientServerAddress aborts with a non-zero exit the service logs.
        // The benign DoH registration and cache flush suppress their own errors so
        // an idempotent re-run (duplicate DoH entry) is not treated as a failure.
        var script = ContentFilter.BuildApplyScript(FilterMode.Family);
        Assert.Contains("$ErrorActionPreference = 'Stop'", script);
        Assert.Contains("Add-DnsClientDohServerAddress", script);
        Assert.Contains("-ErrorAction SilentlyContinue", script);
        Assert.DoesNotContain("$ErrorActionPreference = 'SilentlyContinue'", script);
    }

    [Fact]
    public void Apply_script_uses_only_lf_line_endings()
    {
        // Output must be byte-for-byte deterministic across host OSes (the script
        // is generated here even on non-Windows CI runners), so no CRLF leaks in.
        var script = ContentFilter.BuildApplyScript(FilterMode.Family);
        Assert.DoesNotContain("\r", script);
    }

    [Theory]
    [InlineData(FilterMode.Malware, "1.0.0.3")]
    [InlineData(FilterMode.Family, "1.0.0.2")]
    public void Apply_script_excludes_other_modes_servers(FilterMode mode, string foreignServer)
    {
        // A malware-mode script must not leak family-mode addresses and vice
        // versa; the distinct third octet (.2 vs .3) makes the modes separable.
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
        // Clearing the filter must remove the static servers and not register any
        // DoH bindings — otherwise adapters would stay pinned after disable.
        var script = ContentFilter.BuildApplyScript(FilterMode.Off);
        Assert.DoesNotContain("-ServerAddresses", script);
        Assert.DoesNotContain("Add-DnsClientDohServerAddress", script);
        Assert.DoesNotContain("cloudflare-dns.com", script);
    }

    [Fact]
    public void Off_script_still_flushes_the_dns_cache()
    {
        // The resolver cache is flushed in every mode so a stale answer from the
        // previous configuration is never served after a switch.
        Assert.Contains("Clear-DnsClientCache", ContentFilter.BuildApplyScript(FilterMode.Off));
    }

    [Fact]
    public void Off_script_uses_only_lf_line_endings()
    {
        Assert.DoesNotContain("\r", ContentFilter.BuildApplyScript(FilterMode.Off));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>Counts non-overlapping occurrences of <paramref name="needle"/> in <paramref name="haystack"/>.</summary>
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

using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class ContentFilterTests
{
    [Theory]
    [InlineData("off", FilterMode.Off)]
    [InlineData("malware", FilterMode.Malware)]
    [InlineData("family", FilterMode.Family)]
    [InlineData(null, FilterMode.Off)]
    [InlineData("nonsense", FilterMode.Off)]
    public void Parse_roundtrips(string? value, FilterMode expected)
    {
        Assert.Equal(expected, ContentFilter.Parse(value));
    }

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
    public void Off_script_resets_to_dhcp()
    {
        var script = ContentFilter.BuildApplyScript(FilterMode.Off);
        Assert.Contains("-ResetServerAddresses", script);
        Assert.DoesNotContain("1.1.1.3", script);
    }
}

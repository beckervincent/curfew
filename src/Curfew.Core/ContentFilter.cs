using System.Globalization;
using System.Text;

namespace Curfew.Core;

/// <summary>Cloudflare DNS content-filtering modes.</summary>
public enum FilterMode
{
    /// <summary>No filtering — adapters use their normal (DHCP) DNS.</summary>
    Off,
    /// <summary>Cloudflare "1.1.1.2" — block malware only.</summary>
    Malware,
    /// <summary>Cloudflare "1.1.1.3" — block malware and adult content.</summary>
    Family,
}

/// <summary>
/// Builds the PowerShell used to apply or clear a Cloudflare content filter on
/// every active adapter. Script generation lives here (pure, testable); the
/// service executes it as SYSTEM.
/// </summary>
public static class ContentFilter
{
    public static FilterMode Parse(string? value) => value switch
    {
        "malware" => FilterMode.Malware,
        "family" => FilterMode.Family,
        _ => FilterMode.Off,
    };

    public static string ToSetting(FilterMode mode) => mode switch
    {
        FilterMode.Malware => "malware",
        FilterMode.Family => "family",
        _ => "off",
    };

    /// <summary>IPv4, IPv6 server lists and DoH template for a mode.</summary>
    public static (string[] V4, string[] V6, string DohTemplate) Servers(FilterMode mode) => mode switch
    {
        FilterMode.Malware => (
            new[] { "1.1.1.2", "1.0.0.2" },
            new[] { "2606:4700:4700::1112", "2606:4700:4700::1002" },
            "https://security.cloudflare-dns.com/dns-query"),
        FilterMode.Family => (
            new[] { "1.1.1.3", "1.0.0.3" },
            new[] { "2606:4700:4700::1113", "2606:4700:4700::1003" },
            "https://family.cloudflare-dns.com/dns-query"),
        _ => (Array.Empty<string>(), Array.Empty<string>(), ""),
    };

    /// <summary>PowerShell to pin the chosen Cloudflare servers (with DoH) on
    /// every "Up" adapter, or to reset adapters to DHCP when mode is Off.</summary>
    public static string BuildApplyScript(FilterMode mode)
    {
        if (mode == FilterMode.Off)
        {
            return string.Join('\n', new[]
            {
                "$ErrorActionPreference = 'SilentlyContinue'",
                "foreach ($if in (Get-NetAdapter -Physical | Where-Object Status -eq 'Up')) {",
                "    Set-DnsClientServerAddress -InterfaceIndex $if.ifIndex -ResetServerAddresses",
                "}",
                "Clear-DnsClientCache",
            });
        }

        var (v4, v6, doh) = Servers(mode);
        var all = string.Join(",", v4.Concat(v6).Select(Quote));
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine($"$servers = @({all})");
        sb.AppendLine("foreach ($if in (Get-NetAdapter -Physical | Where-Object Status -eq 'Up')) {");
        sb.AppendLine("    Set-DnsClientServerAddress -InterfaceIndex $if.ifIndex -ServerAddresses $servers");
        sb.AppendLine("}");
        // Encrypt with the matching Cloudflare DoH endpoint.
        foreach (var server in v4.Concat(v6))
        {
            sb.AppendLine(
                $"Add-DnsClientDohServerAddress -ServerAddress {Quote(server)} " +
                $"-DohTemplate {Quote(doh)} -AllowFallbackToUdp $false -AutoUpgrade $true");
        }
        sb.AppendLine("Clear-DnsClientCache");
        return sb.ToString().TrimEnd('\n', '\r');
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    // Keeps CultureInfo referenced for deterministic formatting if extended later.
    internal static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
}

namespace Curfew.Core;

/// <summary>
/// Optional hardening: block well-known third-party DNS-over-HTTPS resolver IPs
/// at the firewall so browsers fall back to the filtered system DNS instead of
/// bypassing it over their own encrypted DNS. Cloudflare's own ranges are never
/// blocked — that is the resolver Curfew relies on.
/// </summary>
public static class DohGuard
{
    public const string RulePrefix = "Curfew-Block-DoH";

    /// <summary>Common public DoH resolvers (excluding Cloudflare).</summary>
    public static readonly string[] BlockedResolvers =
    {
        "8.8.8.8", "8.8.4.4",                 // Google
        "9.9.9.9", "149.112.112.112",         // Quad9
        "208.67.222.222", "208.67.220.220",   // OpenDNS
        "45.90.28.0/24", "45.90.30.0/24",     // NextDNS
        "94.140.14.14", "94.140.15.15",       // AdGuard
        "185.228.168.9", "185.228.169.9",     // CleanBrowsing
    };

    /// <summary>PowerShell adding outbound block rules for the resolver IPs
    /// on the DoH/DoT ports.</summary>
    public static string BuildBlockScript()
    {
        var addresses = string.Join(",", BlockedResolvers.Select(a => "'" + a + "'"));
        return string.Join('\n', new[]
        {
            "$ErrorActionPreference = 'SilentlyContinue'",
            $"Get-NetFirewallRule -DisplayName '{RulePrefix}*' | Remove-NetFirewallRule",
            $"$ips = @({addresses})",
            $"New-NetFirewallRule -DisplayName '{RulePrefix}-out' -Direction Outbound -Action Block " +
            "-Protocol TCP -RemoteAddress $ips -RemotePort 443,853 -Profile Any | Out-Null",
            $"New-NetFirewallRule -DisplayName '{RulePrefix}-udp' -Direction Outbound -Action Block " +
            "-Protocol UDP -RemoteAddress $ips -RemotePort 443,853 | Out-Null",
        });
    }

    /// <summary>PowerShell removing the block rules.</summary>
    public static string BuildClearScript() => string.Join('\n', new[]
    {
        "$ErrorActionPreference = 'SilentlyContinue'",
        $"Get-NetFirewallRule -DisplayName '{RulePrefix}*' | Remove-NetFirewallRule",
    });
}

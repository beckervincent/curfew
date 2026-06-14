using System.Security.Cryptography;
using System.Text;

namespace Curfew.Core;

/// <summary>optional hardening: block well-known third-party DoH/DoT resolver IPs at firewall so browsers fall back to filtered system DNS instead of bypassing over their own encrypted DNS</summary>
/// <remarks>
/// <para>browsers ship encrypted DNS clients resolving against a hard-coded list of public resolvers, ignoring DNS Curfew pins on adapters. blocking outbound traffic on encrypted-DNS ports forces fall back to the OS resolver Curfew controls</para>
/// <para>Cloudflare's own ranges never blocked — that's the resolver Curfew uses for filtering, blocking it defeats the feature. script gen pure + testable; service runs result as SYSTEM via PowerShell</para>
/// </remarks>
public static class DohGuard
{
    /// <summary>display-name prefix on every firewall rule this guard creates. used to add + find/remove rules, so must stay stable across releases</summary>
    public const string RulePrefix = "Curfew-Block-DoH";

    /// <summary>encrypted-DNS ports blocked for listed resolvers: 443 (DoH, rides on HTTPS) + 853 (DoT). plain port 53 left open so OS resolver Curfew controls keeps working</summary>
    public const string BlockedPorts = "443,853";

    /// <summary>common public DoH/DoT resolvers to block — single IPv4/IPv6 addresses + CIDR ranges</summary>
    /// <remarks>
    /// <para>IPv6 included alongside IPv4 because encrypted-DNS clients use either family; IPv4-only would leave an obvious bypass on dual-stack nets</para>
    /// <para>Cloudflare's <em>unfiltered</em> endpoints (1.1.1.1 / 1.0.0.1 + IPv6 twins) ARE blocked: else a child points a browser at unfiltered DoH and bypasses the filter. the <em>filtered</em> family resolvers Curfew pins (1.1.1.2/1.1.1.3, 1.0.0.2/1.0.0.3 via security/family.cloudflare-dns.com) are NOT here, so filtering keeps working</para>
    /// </remarks>
    public static readonly string[] BlockedResolvers =
    {
        // Cloudflare — unfiltered only (filtered 1.1.1.2/1.1.1.3 family kept usable)
        "1.1.1.1", "1.0.0.1",
        "2606:4700:4700::1111", "2606:4700:4700::1001",
        // Google Public DNS
        "8.8.8.8", "8.8.4.4",
        "2001:4860:4860::8888", "2001:4860:4860::8844",
        // Quad9
        "9.9.9.9", "149.112.112.112",
        "2620:fe::fe", "2620:fe::9",
        // OpenDNS (Cisco)
        "208.67.222.222", "208.67.220.220",
        "2620:119:35::35", "2620:119:53::53",
        // NextDNS
        "45.90.28.0/24", "45.90.30.0/24",
        // AdGuard DNS
        "94.140.14.14", "94.140.15.15",
        "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff",
        // CleanBrowsing
        "185.228.168.9", "185.228.169.9",
        "2a0d:2a00:1::2", "2a0d:2a00:2::2",
    };

    /// <summary>build PowerShell blocking outbound traffic to every <see cref="BlockedResolvers"/> address on encrypted-DNS ports</summary>
    /// <remarks>rules carry a content stamp (hash of resolver list + ports) in Description. if rules with current stamp exist, script exits untouched: tear-down + recreate every reconcile opens a brief allow window, and a transient create failure after removal leaves encrypted DNS wide open until next pass. only removed + rebuilt when stamp differs (release changed resolver list) or a rule is missing. separate TCP + UDP rules because <c>New-NetFirewallRule</c> takes one protocol per rule</remarks>
    public static string BuildBlockScript()
    {
        var addresses = string.Join(",", BlockedResolvers.Select(Quote));
        var stamp = RuleStamp;
        var sb = new StringBuilder();
        // fail closed: failure to (re)create block rules must surface as non-zero exit, not be swallowed. only removal — which may find no rules — continues on error
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$out = Get-NetFirewallRule -DisplayName '{RulePrefix}-out' -ErrorAction SilentlyContinue");
        sb.AppendLine($"$udp = Get-NetFirewallRule -DisplayName '{RulePrefix}-udp' -ErrorAction SilentlyContinue");
        sb.AppendLine(
            $"if ($out -and $udp -and $out.Description -eq '{stamp}' -and $udp.Description -eq '{stamp}') {{ exit 0 }}");
        sb.AppendLine(RemoveRulesCommand);
        sb.AppendLine($"$ips = @({addresses})");
        sb.AppendLine(
            $"New-NetFirewallRule -DisplayName '{RulePrefix}-out' -Description '{stamp}' -Direction Outbound -Action Block " +
            $"-Protocol TCP -RemoteAddress $ips -RemotePort {BlockedPorts} -Profile Any | Out-Null");
        sb.AppendLine(
            $"New-NetFirewallRule -DisplayName '{RulePrefix}-udp' -Description '{stamp}' -Direction Outbound -Action Block " +
            $"-Protocol UDP -RemoteAddress $ips -RemotePort {BlockedPorts} -Profile Any | Out-Null");
        // verify both rules exist; if removal tore down enforcement and re-add silently failed, exit non-zero so service logs it and next reconcile retries instead of leaving DNS wide open
        sb.AppendLine(
            $"if (-not (Get-NetFirewallRule -DisplayName '{RulePrefix}-out' -ErrorAction SilentlyContinue) -or " +
            $"-not (Get-NetFirewallRule -DisplayName '{RulePrefix}-udp' -ErrorAction SilentlyContinue)) {{ exit 1 }}");
        return sb.ToString().TrimEnd('\n', '\r');
    }

    /// <summary>stable fingerprint of blocked-resolver list + ports, stored in rules' Description so a reconcile pass tells "current" from "predates a resolver-list change" without rebuilding</summary>
    public static string RuleStamp
    {
        get
        {
            var content = string.Join(",", BlockedResolvers) + "|" + BlockedPorts;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
            return "curfew-" + Convert.ToHexString(hash)[..16];
        }
    }

    /// <summary>build PowerShell removing every firewall rule this guard added, restoring access to third-party resolvers</summary>
    public static string BuildClearScript()
    {
        var sb = new StringBuilder();
        // clearing best-effort: removing rules that may not exist must not fail
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine(RemoveRulesCommand);
        return sb.ToString().TrimEnd('\n', '\r');
    }

    /// <summary>PowerShell removing every rule whose display name starts with <see cref="RulePrefix"/>. shared by block (idempotency) + clear scripts. both cmdlets suppress errors so a missing rule isn't a failure even under <c>$ErrorActionPreference = 'Stop'</c></summary>
    private static readonly string RemoveRulesCommand =
        $"Get-NetFirewallRule -DisplayName '{RulePrefix}*' -ErrorAction SilentlyContinue | " +
        "Remove-NetFirewallRule -ErrorAction SilentlyContinue";

    /// <summary>wrap in PowerShell single-quoted string, doubling embedded quote so literal can't break out. resolver list is hard-coded, but defensive quoting keeps scripts injection-safe</summary>
    private static string Quote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

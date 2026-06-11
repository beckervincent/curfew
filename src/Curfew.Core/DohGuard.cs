using System.Text;

namespace Curfew.Core;

/// <summary>
/// Optional hardening: block well-known third-party DNS-over-HTTPS (DoH) and
/// DNS-over-TLS (DoT) resolver IPs at the firewall so browsers fall back to the
/// filtered system DNS instead of bypassing it over their own encrypted DNS.
/// </summary>
/// <remarks>
/// <para>
/// Modern browsers ship their own encrypted DNS clients that resolve names
/// directly against a hard-coded list of public resolvers, ignoring the DNS
/// servers Curfew pins on the network adapters. Blocking outbound traffic to
/// those resolvers on the encrypted-DNS ports forces the browser to fall back
/// to the operating system resolver, which Curfew controls.
/// </para>
/// <para>
/// Cloudflare's own ranges are deliberately never blocked — that is the resolver
/// Curfew relies on for content filtering, so blocking it would defeat the whole
/// feature. All script generation here is pure and testable; the service
/// executes the result as SYSTEM via PowerShell.
/// </para>
/// </remarks>
public static class DohGuard
{
    /// <summary>
    /// Display-name prefix shared by every firewall rule this guard creates.
    /// Used both to add new rules and to find/remove previously added ones, so
    /// it must stay stable across releases.
    /// </summary>
    public const string RulePrefix = "Curfew-Block-DoH";

    /// <summary>
    /// Encrypted-DNS ports blocked for the listed resolvers: 443 (DoH, which
    /// rides on standard HTTPS) and 853 (DoT). Plain UDP/TCP port 53 is left
    /// open so the OS resolver Curfew controls keeps working.
    /// </summary>
    public const string BlockedPorts = "443,853";

    /// <summary>
    /// Common public DoH/DoT resolvers to block, as a mix of single IPv4/IPv6
    /// addresses and CIDR ranges.
    /// </summary>
    /// <remarks>
    /// <para>
    /// IPv6 entries are included alongside IPv4 because the encrypted-DNS clients
    /// will happily use either family; blocking only IPv4 would leave an obvious
    /// bypass on dual-stack networks.
    /// </para>
    /// <para>
    /// Cloudflare's <em>unfiltered</em> endpoints (1.1.1.1 / 1.0.0.1 and their IPv6
    /// twins) ARE blocked here: a child could otherwise point a browser at
    /// Cloudflare's unfiltered DoH and bypass the content filter entirely. The
    /// <em>filtered</em> family resolvers Curfew actually pins (1.1.1.2/1.1.1.3 and
    /// 1.0.0.2/1.0.0.3, reached via security/family.cloudflare-dns.com) are NOT in
    /// this list, so content filtering keeps working.
    /// </para>
    /// </remarks>
    public static readonly string[] BlockedResolvers =
    {
        // Cloudflare — unfiltered only (the filtered 1.1.1.2/1.1.1.3 family is kept usable)
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

    /// <summary>
    /// Builds the PowerShell that blocks outbound traffic to every
    /// <see cref="BlockedResolvers"/> address on the encrypted-DNS ports.
    /// </summary>
    /// <remarks>
    /// Existing Curfew DoH rules are removed first so the script is idempotent:
    /// re-running it (for example on a network change) refreshes the rules
    /// without piling up duplicates. Separate TCP and UDP rules are created
    /// because <c>New-NetFirewallRule</c> does not accept multiple protocols in
    /// a single rule.
    /// </remarks>
    public static string BuildBlockScript()
    {
        var addresses = string.Join(",", BlockedResolvers.Select(Quote));
        var sb = new StringBuilder();
        // Fail closed: a failure to (re)create the block rules must surface as a
        // non-zero exit, not be silently swallowed. Only the removal — which may
        // legitimately find no existing rules — is allowed to continue on error.
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine(RemoveRulesCommand);
        sb.AppendLine($"$ips = @({addresses})");
        sb.AppendLine(
            $"New-NetFirewallRule -DisplayName '{RulePrefix}-out' -Direction Outbound -Action Block " +
            $"-Protocol TCP -RemoteAddress $ips -RemotePort {BlockedPorts} -Profile Any | Out-Null");
        sb.AppendLine(
            $"New-NetFirewallRule -DisplayName '{RulePrefix}-udp' -Direction Outbound -Action Block " +
            $"-Protocol UDP -RemoteAddress $ips -RemotePort {BlockedPorts} -Profile Any | Out-Null");
        // Verify both rules exist; if the removal above tore down enforcement and a
        // re-add silently failed to take, exit non-zero so the service logs it and
        // the next reconcile pass retries rather than leaving DNS wide open.
        sb.AppendLine(
            $"if (-not (Get-NetFirewallRule -DisplayName '{RulePrefix}-out' -ErrorAction SilentlyContinue) -or " +
            $"-not (Get-NetFirewallRule -DisplayName '{RulePrefix}-udp' -ErrorAction SilentlyContinue)) {{ exit 1 }}");
        return sb.ToString().TrimEnd('\n', '\r');
    }

    /// <summary>
    /// Builds the PowerShell that removes every firewall rule this guard added,
    /// restoring normal access to the third-party resolvers.
    /// </summary>
    public static string BuildClearScript()
    {
        var sb = new StringBuilder();
        // Clearing is best-effort: removing rules that may not exist must not fail.
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine(RemoveRulesCommand);
        return sb.ToString().TrimEnd('\n', '\r');
    }

    /// <summary>
    /// PowerShell that finds and removes every rule whose display name starts
    /// with <see cref="RulePrefix"/>. Shared by the block (for idempotency) and
    /// clear scripts. Both cmdlets suppress their own errors so a missing rule is
    /// not treated as a failure even when the surrounding script uses
    /// <c>$ErrorActionPreference = 'Stop'</c>.
    /// </summary>
    private static readonly string RemoveRulesCommand =
        $"Get-NetFirewallRule -DisplayName '{RulePrefix}*' -ErrorAction SilentlyContinue | " +
        "Remove-NetFirewallRule -ErrorAction SilentlyContinue";

    /// <summary>
    /// Wraps a value in a PowerShell single-quoted string, doubling any embedded
    /// single quote so the literal cannot break out of the quoting. The resolver
    /// list is hard-coded, but quoting defensively keeps the scripts injection-safe.
    /// </summary>
    private static string Quote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

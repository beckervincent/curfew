namespace Curfew.Core;

/// <summary>Cloudflare DNS content-filtering modes</summary>
public enum FilterMode
{
    /// <summary>no filtering — adapters use normal (DHCP) DNS</summary>
    Off,

    /// <summary>Cloudflare "1.1.1.2" — block malware only</summary>
    Malware,

    /// <summary>Cloudflare "1.1.1.3" — block malware + adult content</summary>
    Family,

    /// <summary>OpenDNS FamilyShield (208.67.222.123 / .220.123) — block adult content. Alternative
    /// provider for networks where Cloudflare's resolvers are unreachable.</summary>
    FamilyOpenDns,
}

/// <summary>build PowerShell to apply or clear Cloudflare content filter on every active adapter. script gen lives here (pure, testable); service runs it as SYSTEM</summary>
public static class ContentFilter
{
    // canonical resolver data. private static so public surface hands out defensive copies — callers can't mutate the shared source of truth
    private static readonly string[] MalwareV4 = { "1.1.1.2", "1.0.0.2" };
    private static readonly string[] MalwareV6 = { "2606:4700:4700::1112", "2606:4700:4700::1002" };
    private const string MalwareDoh = "https://security.cloudflare-dns.com/dns-query";

    private static readonly string[] FamilyV4 = { "1.1.1.3", "1.0.0.3" };
    private static readonly string[] FamilyV6 = { "2606:4700:4700::1113", "2606:4700:4700::1003" };
    private const string FamilyDoh = "https://family.cloudflare-dns.com/dns-query";

    // OpenDNS FamilyShield: IPv4 only (no documented FamilyShield IPv6 / DoH), so DoH is left empty
    private static readonly string[] OpenDnsV4 = { "208.67.222.123", "208.67.220.123" };

    /// <summary>setting string ("off"/"malware"/"family") used by <see cref="ToSetting"/></summary>
    private const string OffSetting = "off";
    private const string MalwareSetting = "malware";
    private const string FamilySetting = "family";
    private const string FamilyOpenDnsSetting = "family-opendns";

    /// <summary>parse persisted <c>dns_filter_mode</c> into <see cref="FilterMode"/>. case-insensitive, whitespace-tolerant; unrecognized/null/empty falls back to <see cref="FilterMode.Off"/></summary>
    public static FilterMode Parse(string? value) => Normalize(value) switch
    {
        MalwareSetting => FilterMode.Malware,
        FamilySetting => FilterMode.Family,
        FamilyOpenDnsSetting => FilterMode.FamilyOpenDns,
        _ => FilterMode.Off,
    };

    /// <summary>serialize <see cref="FilterMode"/> to persisted setting string</summary>
    public static string ToSetting(FilterMode mode) => mode switch
    {
        FilterMode.Malware => MalwareSetting,
        FilterMode.Family => FamilySetting,
        FilterMode.FamilyOpenDns => FamilyOpenDnsSetting,
        _ => OffSetting,
    };

    /// <summary>IPv4 list, IPv6 list, DoH template for a mode. fresh arrays each call, safe to sort/mutate. <see cref="FilterMode.Off"/> yields empty lists + empty template</summary>
    public static (string[] V4, string[] V6, string DohTemplate) Servers(FilterMode mode) => mode switch
    {
        FilterMode.Malware => ((string[])MalwareV4.Clone(), (string[])MalwareV6.Clone(), MalwareDoh),
        FilterMode.Family => ((string[])FamilyV4.Clone(), (string[])FamilyV6.Clone(), FamilyDoh),
        FilterMode.FamilyOpenDns => ((string[])OpenDnsV4.Clone(), Array.Empty<string>(), string.Empty),
        _ => (Array.Empty<string>(), Array.Empty<string>(), string.Empty),
    };

    /// <summary>build PowerShell pinning chosen Cloudflare servers (with DoH) on every "Up" physical adapter, or — <paramref name="mode"/> = <see cref="FilterMode.Off"/> — reset those adapters to DHCP</summary>
    /// <remarks>lines joined with <c>\n</c> not <see cref="Environment.NewLine"/> so script is byte-for-byte deterministic regardless of host OS (tested on non-Windows runners)</remarks>
    public static string BuildApplyScript(FilterMode mode)
    {
        // fail closed: pinning/resetting resolver is security-critical, runs under 'Stop', failure surfaces as non-zero exit the service logs. only benign ops below — flush cache, register possibly-duplicate DoH template — suppress errors
        if (mode == FilterMode.Off)
        {
            return Join(
                "$ErrorActionPreference = 'Stop'",
                "foreach ($if in (Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' })) {",
                "    Set-DnsClientServerAddress -InterfaceIndex $if.ifIndex -ResetServerAddresses",
                "}",
                "Clear-DnsClientCache -ErrorAction SilentlyContinue");
        }

        var (v4, v6, doh) = Servers(mode);
        var servers = v4.Concat(v6).ToArray();
        var serverList = string.Join(",", servers.Select(Quote));

        var lines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            $"$servers = @({serverList})",
            "foreach ($if in (Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' })) {",
            "    Set-DnsClientServerAddress -InterfaceIndex $if.ifIndex -ServerAddresses $servers",
            "}",
        };

        // encrypt each pinned resolver with its matching DoH endpoint. existing DoH entry throws under 'Stop',
        // benign here (resolver still pinned), so suppress its errors. skipped when the provider has no DoH
        // template (e.g. OpenDNS FamilyShield) — the resolver is still pinned over plain DNS.
        if (!string.IsNullOrEmpty(doh))
            foreach (var server in servers)
            {
                lines.Add(
                    $"Add-DnsClientDohServerAddress -ServerAddress {Quote(server)} " +
                    $"-DohTemplate {Quote(doh)} -AllowFallbackToUdp $false -AutoUpgrade $true " +
                    "-ErrorAction SilentlyContinue");
            }

        lines.Add("Clear-DnsClientCache -ErrorAction SilentlyContinue");
        return Join(lines.ToArray());
    }

    /// <summary>lower-case + trim raw setting for case-insensitive parsing</summary>
    private static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>join script lines with literal LF for deterministic output</summary>
    private static string Join(params string[] lines) => string.Join('\n', lines);

    /// <summary>wrap in single quotes, double any embedded quote (PowerShell escaping)</summary>
    private static string Quote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

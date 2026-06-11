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
/// every active adapter. Script generation lives here (pure and testable); the
/// service executes the generated script as SYSTEM.
/// </summary>
public static class ContentFilter
{
    // Canonical resolver data. Kept as private static fields so the public
    // surface can hand out defensive copies — callers must never be able to
    // mutate the shared source of truth for every other caller.
    private static readonly string[] MalwareV4 = { "1.1.1.2", "1.0.0.2" };
    private static readonly string[] MalwareV6 = { "2606:4700:4700::1112", "2606:4700:4700::1002" };
    private const string MalwareDoh = "https://security.cloudflare-dns.com/dns-query";

    private static readonly string[] FamilyV4 = { "1.1.1.3", "1.0.0.3" };
    private static readonly string[] FamilyV6 = { "2606:4700:4700::1113", "2606:4700:4700::1003" };
    private const string FamilyDoh = "https://family.cloudflare-dns.com/dns-query";

    /// <summary>The setting string ("off"/"malware"/"family") used by <see cref="ToSetting"/>.</summary>
    private const string OffSetting = "off";
    private const string MalwareSetting = "malware";
    private const string FamilySetting = "family";

    /// <summary>
    /// Parses a persisted <c>dns_filter_mode</c> setting into a <see cref="FilterMode"/>.
    /// Matching is case-insensitive and tolerant of surrounding whitespace; any
    /// unrecognized or null/empty value falls back to <see cref="FilterMode.Off"/>.
    /// </summary>
    public static FilterMode Parse(string? value) => Normalize(value) switch
    {
        MalwareSetting => FilterMode.Malware,
        FamilySetting => FilterMode.Family,
        _ => FilterMode.Off,
    };

    /// <summary>Serializes a <see cref="FilterMode"/> to its persisted setting string.</summary>
    public static string ToSetting(FilterMode mode) => mode switch
    {
        FilterMode.Malware => MalwareSetting,
        FilterMode.Family => FamilySetting,
        _ => OffSetting,
    };

    /// <summary>
    /// Returns the IPv4 server list, IPv6 server list and DoH template for a mode.
    /// Each call returns fresh arrays, so callers may safely sort or mutate them.
    /// <see cref="FilterMode.Off"/> yields empty server lists and an empty template.
    /// </summary>
    public static (string[] V4, string[] V6, string DohTemplate) Servers(FilterMode mode) => mode switch
    {
        FilterMode.Malware => ((string[])MalwareV4.Clone(), (string[])MalwareV6.Clone(), MalwareDoh),
        FilterMode.Family => ((string[])FamilyV4.Clone(), (string[])FamilyV6.Clone(), FamilyDoh),
        _ => (Array.Empty<string>(), Array.Empty<string>(), string.Empty),
    };

    /// <summary>
    /// Builds the PowerShell that pins the chosen Cloudflare servers (with DoH)
    /// on every physical adapter that is "Up", or — when <paramref name="mode"/>
    /// is <see cref="FilterMode.Off"/> — resets those adapters to DHCP.
    /// </summary>
    /// <remarks>
    /// Lines are joined with <c>\n</c> rather than <see cref="Environment.NewLine"/>
    /// so the script is byte-for-byte deterministic regardless of the host OS
    /// (this assembly is also exercised by tests on non-Windows runners).
    /// </remarks>
    public static string BuildApplyScript(FilterMode mode)
    {
        // Fail closed: pinning (or resetting) the resolver is the security-critical
        // step, so it runs under 'Stop' and a failure surfaces as a non-zero exit
        // the service logs. Only genuinely benign operations below — flushing the
        // cache, registering a possibly-duplicate DoH template — suppress errors.
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

        // Encrypt each pinned resolver with the matching Cloudflare DoH endpoint. A
        // DoH entry that already exists throws under 'Stop', which is benign here
        // (the resolver is still pinned), so this step suppresses its own errors.
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

    /// <summary>Lower-cases and trims a raw setting value for case-insensitive parsing.</summary>
    private static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;

    /// <summary>Joins script lines with a literal LF for deterministic output.</summary>
    private static string Join(params string[] lines) => string.Join('\n', lines);

    /// <summary>Wraps a value in single quotes, doubling any embedded quote (PowerShell escaping).</summary>
    private static string Quote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

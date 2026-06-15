namespace Curfew.Core;

/// <summary>
/// The settings a one-tap "Strict protection" action turns on: the safety-focused
/// content and anti-circumvention defenses, all of which are individually verified.
/// Pure (returns the key/value map); the Settings UI applies it and the SYSTEM service /
/// overlay enforce it. Deliberately covers protection only — it does not touch time
/// limits, schedules, or lifestyle categories (social/gaming/streaming), which stay the
/// parent's choice.
/// </summary>
public static class StrictPreset
{
    /// <summary>Device-wide setting key → value to write for strict protection.</summary>
    public static IReadOnlyDictionary<string, string> Settings() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["dns_filter_mode"] = ContentFilter.ToSetting(FilterMode.Family), // Cloudflare 1.1.1.3 (malware + adult)
        ["block_doh_bypass"] = "1",                                       // stop encrypted-DNS bypass of the filter
        ["safesearch_enabled"] = "1",                                     // force SafeSearch on the search engines
        ["blocked_categories"] = string.Join(',', BlockCategories.Adult, BlockCategories.Proxy),
        ["block_private_browsing"] = "1",                                 // disable incognito / private windows
        ["block_vpn_apps"] = "1",                                         // close known VPN / Tor clients
    };
}

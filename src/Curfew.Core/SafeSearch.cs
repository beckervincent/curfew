namespace Curfew.Core;

/// <summary>
/// Enforced SafeSearch for the major search engines, applied through the hosts
/// file. Each search domain is pointed at the engine's published "force safe
/// search" virtual IP so results are always filtered, regardless of the user's
/// account or cookie settings. Pure (returns hosts lines); the SYSTEM service
/// writes them alongside the domain blocklist in the same Curfew hosts section.
/// </summary>
/// <remarks>
/// Addresses are the vendors' documented SafeSearch VIPs (Google
/// forcesafesearch.google.com, Microsoft strict.bing.com, YouTube
/// restrict.youtube.com). If a vendor ever changes them the feature degrades to
/// "not forced" rather than breaking browsing — it never blocks the engine.
/// </remarks>
public static class SafeSearch
{
    // Google + YouTube SafeSearch/Restricted-mode VIP, and Bing Strict VIP.
    private const string GoogleSafe = "216.239.38.120";   // forcesafesearch.google.com
    private const string YouTubeRestrict = "216.239.38.120"; // restrict.youtube.com
    private const string BingStrict = "204.79.197.220";   // strict.bing.com

    /// <summary>
    /// Hosts lines (<c>"ip host"</c>) that force SafeSearch on Google, Bing and YouTube.
    /// Returned in a stable order for deterministic output.
    /// </summary>
    public static IReadOnlyList<string> HostsLines()
    {
        var lines = new List<string>();

        // Google search: .com plus the common country domains a child would otherwise use to
        // dodge SafeSearch (each maps to forcesafesearch.google.com's VIP, apex + www).
        var googleDomains = new[]
        {
            "google.com", "google.co.uk", "google.ca", "google.com.au", "google.de",
            "google.fr", "google.es", "google.it", "google.nl", "google.pl",
            "google.com.br", "google.co.in", "google.co.jp", "google.ru", "google.com.mx",
        };
        foreach (var host in googleDomains)
        {
            lines.Add($"{GoogleSafe} {host}");
            lines.Add($"{GoogleSafe} www.{host}");
        }

        // Bing
        foreach (var host in new[] { "bing.com", "www.bing.com" })
            lines.Add($"{BingStrict} {host}");

        // YouTube (restricted mode)
        foreach (var host in new[] { "youtube.com", "www.youtube.com", "m.youtube.com" })
            lines.Add($"{YouTubeRestrict} {host}");

        return lines;
    }
}

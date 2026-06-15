namespace Curfew.Core;

/// <summary>
/// Catalog of curated, public, downloadable blocklists the parent can enable (the
/// Pi-hole / StevenBlack model). Each maps to a stable raw static file — no Curfew
/// server is involved; the SYSTEM service fetches it like an update and caches it
/// locally. Pure data + parsing; the service does the download.
/// </summary>
public static class BlocklistSources
{
    /// <summary>StevenBlack "porn" alternate — adult sites. The child-safety default.</summary>
    public const string Adult = "adult";

    /// <summary>StevenBlack unified — ads + malware + tracking (large).</summary>
    public const string Ads = "ads";

    /// <summary>StevenBlack "fakenews" alternate — known fake-news / misinformation sites.</summary>
    public const string FakeNews = "fakenews";

    private static readonly IReadOnlyDictionary<string, string> Catalog =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Adult] = "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/porn/hosts",
            [Ads] = "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts",
            [FakeNews] = "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/fakenews/hosts",
        };

    /// <summary>All source keys, for building the settings UI.</summary>
    public static IReadOnlyList<string> AllKeys => new[] { Adult, Ads, FakeNews };

    /// <summary>Parse the stored comma/space/semicolon-separated source list into known keys (lower-case, de-duped).</summary>
    public static IReadOnlyList<string> Parse(string? stored)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(stored)) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in stored.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var key = raw.ToLowerInvariant();
            if (Catalog.ContainsKey(key) && seen.Add(key)) result.Add(key);
        }
        return result;
    }

    /// <summary>The download URLs for the enabled <paramref name="keys"/> (order-stable, de-duped).</summary>
    public static IReadOnlyList<string> UrlsFor(IReadOnlyList<string> keys)
    {
        var urls = new List<string>();
        foreach (var key in keys)
            if (Catalog.TryGetValue(key, out var url) && !urls.Contains(url))
                urls.Add(url);
        return urls;
    }
}

namespace Curfew.Core;

/// <summary>
/// One-tap content categories the parent can block as a bundle (social media,
/// gaming, streaming). Each category maps to a curated set of well-known domains
/// that are added to the hosts blocklist alongside any custom domains. Pure: parse
/// the enabled-category list and expand it to domains; the SYSTEM service writes
/// them. Deliberately conservative lists of obvious flagship domains — not an
/// exhaustive filter (the Cloudflare family filter and custom blocklist cover the
/// long tail).
/// </summary>
public static class BlockCategories
{
    /// <summary>Category keys persisted in <c>blocked_categories</c> (comma-separated).</summary>
    public const string Social = "social";
    public const string Gaming = "gaming";
    public const string Streaming = "streaming";
    public const string Adult = "adult";
    public const string Proxy = "proxy";

    private static readonly IReadOnlyDictionary<string, string[]> Catalog =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Social] = new[]
            {
                "facebook.com", "instagram.com", "tiktok.com", "snapchat.com",
                "twitter.com", "x.com", "reddit.com", "tumblr.com",
            },
            [Gaming] = new[]
            {
                "roblox.com", "epicgames.com", "steampowered.com", "minecraft.net",
            },
            [Streaming] = new[]
            {
                "netflix.com", "hulu.com", "disneyplus.com", "twitch.tv",
            },
            // Flagship adult sites. Defense-in-depth alongside enforced SafeSearch and the Cloudflare
            // family DNS filter (1.1.1.3), which covers the long tail; this hosts list still bites when
            // the DNS filter is off. Conservative on purpose — not an exhaustive adult-content filter.
            [Adult] = new[]
            {
                "pornhub.com", "xvideos.com", "xnxx.com", "xhamster.com", "redtube.com",
                "youporn.com", "onlyfans.com", "chaturbate.com", "stripchat.com", "spankbang.com",
            },
            // Anti-circumvention: VPN providers and web proxies a child would use to download a tunnel
            // or relay through to defeat the content filter. Blocks the sites (download/discovery + web
            // proxies); pair with the app blocklist for already-installed VPN clients.
            [Proxy] = new[]
            {
                "nordvpn.com", "expressvpn.com", "protonvpn.com", "surfshark.com", "tunnelbear.com",
                "windscribe.com", "hide.me", "hidemyass.com", "proxysite.com", "croxyproxy.com",
                "kproxy.com", "hidester.com", "4everproxy.com", "ultrasurf.us",
            },
        };

    /// <summary>All category keys, for building the settings UI.</summary>
    public static IReadOnlyList<string> AllKeys => new[] { Social, Gaming, Streaming, Adult, Proxy };

    /// <summary>Parse the stored comma/space/semicolon-separated category list into known keys (lower-case, de-duped).</summary>
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

    /// <summary>Domains to block for the enabled <paramref name="categories"/> (de-duped, order-stable).</summary>
    public static IReadOnlyList<string> DomainsFor(IReadOnlyList<string> categories)
    {
        var domains = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in categories)
            if (Catalog.TryGetValue(cat, out var list))
                foreach (var d in list)
                    if (seen.Add(d)) domains.Add(d);
        return domains;
    }
}

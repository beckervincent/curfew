using System.Linq;
using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class BlockCategoriesTests
{
    [Fact]
    public void Parse_keeps_known_keys_and_drops_unknown()
    {
        var keys = BlockCategories.Parse("social, nonsense ; GAMING social");
        Assert.Equal(new[] { "social", "gaming" }, keys); // de-duped, known-only, lower-cased
    }

    [Fact]
    public void Parse_empty_is_empty() => Assert.Empty(BlockCategories.Parse(""));

    [Fact]
    public void DomainsFor_expands_and_dedupes()
    {
        var domains = BlockCategories.DomainsFor(new[] { BlockCategories.Social, BlockCategories.Gaming });
        Assert.Contains("tiktok.com", domains);
        Assert.Contains("roblox.com", domains);
        Assert.DoesNotContain("netflix.com", domains); // streaming not enabled
        Assert.Equal(domains.Count, domains.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void DomainsFor_none_is_empty() =>
        Assert.Empty(BlockCategories.DomainsFor(System.Array.Empty<string>()));

    [Fact]
    public void Adult_category_is_known_and_expands()
    {
        Assert.Contains(BlockCategories.Adult, BlockCategories.AllKeys);
        Assert.Equal(new[] { "adult" }, BlockCategories.Parse("ADULT"));
        var domains = BlockCategories.DomainsFor(new[] { BlockCategories.Adult });
        Assert.Contains("pornhub.com", domains);
        Assert.DoesNotContain("tiktok.com", domains);
    }

    [Fact]
    public void Proxy_category_blocks_vpn_and_proxy_sites()
    {
        Assert.Contains(BlockCategories.Proxy, BlockCategories.AllKeys);
        var domains = BlockCategories.DomainsFor(new[] { BlockCategories.Proxy });
        Assert.Contains("nordvpn.com", domains);
        Assert.Contains("croxyproxy.com", domains);
    }

    [Fact]
    public void VpnApps_names_are_normalized_and_match_real_process_paths()
    {
        Assert.Contains("nordvpn", VpnApps.Names);
        Assert.Contains("tor", VpnApps.Names);
        // names match the overlay's foreground check (which uses AppAllowlist.Allows -> Normalize)
        Assert.True(AppAllowlist.Allows(VpnApps.Names, @"C:\Program Files\NordVPN\NordVPN.exe"));
        Assert.True(AppAllowlist.Allows(VpnApps.Names, "ProtonVPN.exe"));
        Assert.False(AppAllowlist.Allows(VpnApps.Names, "chrome.exe"));
    }

    [Fact]
    public void RemoteAccessApps_match_real_process_paths()
    {
        Assert.True(AppAllowlist.Allows(RemoteAccessApps.Names, @"C:\Program Files\TeamViewer\TeamViewer.exe"));
        Assert.True(AppAllowlist.Allows(RemoteAccessApps.Names, "AnyDesk.exe"));
        Assert.True(AppAllowlist.Allows(RemoteAccessApps.Names, "quickassist.exe")); // Windows Quick Assist
        Assert.False(AppAllowlist.Allows(RemoteAccessApps.Names, "notepad.exe"));
    }
}

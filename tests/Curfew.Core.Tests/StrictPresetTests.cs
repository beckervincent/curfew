using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class StrictPresetTests
{
    [Fact]
    public void Settings_turn_on_the_safety_defenses()
    {
        var s = StrictPreset.Settings();
        Assert.Equal("family", s["dns_filter_mode"]);
        Assert.Equal("1", s["block_doh_bypass"]);
        Assert.Equal("1", s["safesearch_enabled"]);
        Assert.Equal("1", s["block_private_browsing"]);
        Assert.Equal("1", s["block_vpn_apps"]);
    }

    [Fact]
    public void Settings_block_adult_and_proxy_categories()
    {
        var cats = BlockCategories.Parse(StrictPreset.Settings()["blocked_categories"]);
        Assert.Contains(BlockCategories.Adult, cats);
        Assert.Contains(BlockCategories.Proxy, cats);
    }

    [Fact]
    public void Settings_do_not_touch_lifestyle_categories_or_time_limits()
    {
        var cats = BlockCategories.Parse(StrictPreset.Settings()["blocked_categories"]);
        Assert.DoesNotContain(BlockCategories.Social, cats);
        Assert.DoesNotContain(BlockCategories.Gaming, cats);
        Assert.False(StrictPreset.Settings().ContainsKey("limit_enabled"));
    }
}

using System.Collections.Generic;
using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class AppTimeLimitsTests
{
    [Fact]
    public void Parse_reads_name_equals_minutes_and_normalizes_names()
    {
        var limits = AppTimeLimits.Parse("Minecraft.exe = 60, C:\\Games\\game.exe=30\nchrome;notalimit");
        Assert.Equal(60, limits["minecraft"]);
        Assert.Equal(30, limits["game"]);
        Assert.False(limits.ContainsKey("chrome")); // no '=' -> dropped
    }

    [Fact]
    public void Parse_clamps_minutes_to_a_day_and_keeps_zero()
    {
        var limits = AppTimeLimits.Parse("a=99999\nb=0\nc=-5");
        Assert.Equal(24 * 60, limits["a"]);
        Assert.Equal(0, limits["b"]);   // 0 = blocked outright, a valid policy
        Assert.Equal(0, limits["c"]);   // negative clamps to 0
    }

    [Fact]
    public void Parse_drops_unparseable_minutes_and_empty_names()
    {
        var limits = AppTimeLimits.Parse("good=15\nbad=abc\n=20");
        Assert.Equal(15, limits["good"]);
        Assert.Single(limits);
    }

    [Fact]
    public void Parse_last_duplicate_wins()
    {
        var limits = AppTimeLimits.Parse("game=60\ngame.exe=10");
        Assert.Equal(10, limits["game"]);
    }

    [Theory]
    [InlineData("minecraft", 60)]
    [InlineData("C:\\x\\Minecraft.exe", 60)]
    [InlineData("unknown", -1)]
    public void LimitMinutesFor_matches_by_normalized_name(string process, int expected) =>
        Assert.Equal(expected, AppTimeLimits.LimitMinutesFor(
            new Dictionary<string, int> { ["minecraft"] = 60 }, process));

    [Fact]
    public void LimitMinutesFor_empty_map_or_blank_returns_negative()
    {
        Assert.Equal(-1, AppTimeLimits.LimitMinutesFor(new Dictionary<string, int>(), "x"));
        Assert.Equal(-1, AppTimeLimits.LimitMinutesFor(new Dictionary<string, int> { ["x"] = 5 }, "  "));
    }

    [Fact]
    public void Serialize_roundtrips_through_parse()
    {
        var original = AppTimeLimits.Parse("minecraft=60\ngame=30");
        var reparsed = AppTimeLimits.Parse(AppTimeLimits.Serialize(original));
        Assert.Equal(60, reparsed["minecraft"]);
        Assert.Equal(30, reparsed["game"]);
    }
}

public class AppUsageMapTests
{
    [Fact]
    public void Parse_reads_comma_separated_name_equals_seconds()
    {
        var map = AppUsageMap.Parse("chrome=3600,game=1800");
        Assert.Equal(3600, map["chrome"]);
        Assert.Equal(1800, map["game"]);
    }

    [Fact]
    public void Parse_tolerates_torn_or_malformed_rows()
    {
        var map = AppUsageMap.Parse("ok=10,bad,worse=,=5,neg=-3");
        Assert.Equal(10, map["ok"]);
        Assert.Equal(0, map["neg"]); // negative clamps to 0
        Assert.Single(map.Keys, k => k == "ok");
        Assert.True(map.ContainsKey("neg"));
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void Serialize_is_stable_sorted_and_drops_zero_and_empty()
    {
        var map = new Dictionary<string, int> { ["b"] = 5, ["a"] = 10, ["c"] = 0 };
        Assert.Equal("a=10,b=5", AppUsageMap.Serialize(map));
    }

    [Fact]
    public void Serialize_parse_roundtrip()
    {
        var map = new Dictionary<string, int> { ["chrome"] = 120, ["game"] = 45 };
        var round = AppUsageMap.Parse(AppUsageMap.Serialize(map));
        Assert.Equal(120, round["chrome"]);
        Assert.Equal(45, round["game"]);
    }
}

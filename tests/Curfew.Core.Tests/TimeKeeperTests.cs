using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class TimeKeeperTests
{
    [Fact]
    public void InitialRemaining_uses_saved_when_present() =>
        Assert.Equal(500, TimeKeeper.InitialRemaining(500, 120));

    [Fact]
    public void InitialRemaining_falls_back_to_daily_limit() =>
        Assert.Equal(120 * 60, TimeKeeper.InitialRemaining(null, 120));

    [Fact]
    public void Tick_decrements_and_floors_at_zero()
    {
        Assert.Equal(99, TimeKeeper.Tick(100));
        Assert.Equal(0, TimeKeeper.Tick(0));
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(60, true)]
    [InlineData(45, false)]
    [InlineData(0, false)]
    public void ShouldPersist_every_30s(int remaining, bool expected) =>
        Assert.Equal(expected, TimeKeeper.ShouldPersist(remaining));

    [Fact]
    public void WarningFires_on_exact_threshold()
    {
        Assert.True(TimeKeeper.WarningFires(600, 10));
        Assert.False(TimeKeeper.WarningFires(601, 10));
        Assert.False(TimeKeeper.WarningFires(600, 0));
    }

    [Fact]
    public void IsExhausted_at_zero_or_below()
    {
        Assert.True(TimeKeeper.IsExhausted(0));
        Assert.True(TimeKeeper.IsExhausted(-5));
        Assert.False(TimeKeeper.IsExhausted(1));
    }

    [Fact]
    public void Extend_adds_or_starts_fresh()
    {
        Assert.Equal(900, TimeKeeper.Extend(0, 15));
        Assert.Equal(1800, TimeKeeper.Extend(900, 15));
        Assert.Equal(900, TimeKeeper.Extend(-1, 15));
    }
}

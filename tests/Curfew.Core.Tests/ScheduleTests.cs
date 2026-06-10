using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class ScheduleTests
{
    [Fact]
    public void Default_is_fully_allowed()
    {
        var s = Schedule.AllAllowed();
        Assert.True(s.IsAllowed(0, 0));
        Assert.True(s.IsAllowed(6, 23 * 60 + 59));
    }

    [Fact]
    public void Null_or_malformed_parses_to_all_allowed()
    {
        Assert.True(Schedule.Parse(null).IsAllowed(2, 600));
        Assert.True(Schedule.Parse("garbage").IsAllowed(2, 600));
        Assert.True(Schedule.Parse("0;0;0").IsAllowed(2, 600)); // wrong day count
    }

    [Fact]
    public void Serialize_roundtrips()
    {
        var s = Schedule.AllAllowed();
        s.SetSlot(0, 40, false); // Monday 10:00-10:15 blocked
        s.SetSlot(0, 41, false);

        var round = Schedule.Parse(s.Serialize());
        Assert.False(round.GetSlot(0, 40));
        Assert.False(round.GetSlot(0, 41));
        Assert.True(round.GetSlot(0, 39));
    }

    [Fact]
    public void IsAllowed_maps_minutes_to_slots()
    {
        var s = Schedule.AllAllowed();
        // Block 14:00-15:00 on Wednesday (slots 56..59).
        for (var slot = 56; slot < 60; slot++) s.SetSlot(2, slot, false);

        Assert.False(s.IsAllowed(2, 14 * 60));      // 14:00
        Assert.False(s.IsAllowed(2, 14 * 60 + 59)); // 14:59
        Assert.True(s.IsAllowed(2, 15 * 60));       // 15:00
        Assert.True(s.IsAllowed(2, 13 * 60 + 59));  // 13:59
    }

    [Fact]
    public void Out_of_range_weekday_is_allowed()
    {
        Assert.True(Schedule.AllAllowed().IsAllowed(99, 600));
    }
}

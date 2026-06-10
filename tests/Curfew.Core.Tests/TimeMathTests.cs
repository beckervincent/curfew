using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class TimeMathTests
{
    [Theory]
    [InlineData("2026-06-08", 0)] // Monday
    [InlineData("2026-06-13", 5)] // Saturday
    [InlineData("2026-06-14", 6)] // Sunday
    public void MondayBasedWeekday_maps_days(string date, int expected)
    {
        var d = DateOnly.Parse(date);
        Assert.Equal(expected, TimeMath.MondayBasedWeekday(d));
    }

    [Theory]
    [InlineData(-1, "--")]
    [InlineData(45, "45s")]
    [InlineData(125, "2m 5s")]
    [InlineData(3661, "1h 1m 1s")]
    public void FormatDuration_formats(int seconds, string expected)
    {
        Assert.Equal(expected, TimeMath.FormatDuration(seconds));
    }

    [Theory]
    [InlineData(-1, "--:--")]
    [InlineData(125, "2:05")]
    [InlineData(3661, "1:01:01")]
    public void FormatCompact_formats(int seconds, string expected)
    {
        Assert.Equal(expected, TimeMath.FormatCompact(seconds));
    }
}

using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>
/// Unit tests for <see cref="TimeMath"/>, the pure time/duration helpers shared by
/// the App, Overlay and Service. Coverage focuses on the boundaries where the
/// formatting branches change (sub-minute, sub-hour, exact hour, multi-day) and on
/// the negative "unavailable" placeholders, since those values flow straight into
/// the user-facing countdowns.
/// </summary>
public class TimeMathTests
{
    [Theory]
    [InlineData("2026-06-08", 0)] // Monday
    [InlineData("2026-06-09", 1)] // Tuesday
    [InlineData("2026-06-10", 2)] // Wednesday
    [InlineData("2026-06-11", 3)] // Thursday
    [InlineData("2026-06-12", 4)] // Friday
    [InlineData("2026-06-13", 5)] // Saturday
    [InlineData("2026-06-14", 6)] // Sunday
    public void MondayBasedWeekday_maps_every_day_of_the_week(string date, int expected)
    {
        var d = DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, TimeMath.MondayBasedWeekday(d));
    }

    [Fact]
    public void MondayBasedWeekday_always_returns_value_in_zero_to_six()
    {
        // Walk a full week starting on a known Monday and assert the result is a
        // valid Monday-based index for every day, with Sunday wrapping to 6.
        var monday = new DateOnly(2026, 6, 8);

        for (var offset = 0; offset < 7; offset++)
        {
            var index = TimeMath.MondayBasedWeekday(monday.AddDays(offset));

            Assert.InRange(index, 0, 6);
            Assert.Equal(offset, index);
        }
    }

    [Theory]
    // Negative input renders the "unavailable" placeholder.
    [InlineData(-1, "--")]
    [InlineData(-3600, "--")]
    // Seconds only: the most significant non-zero unit appears first.
    [InlineData(0, "0s")]
    [InlineData(45, "45s")]
    [InlineData(59, "59s")]
    // Minutes appear once we cross a full minute; trailing seconds are not padded.
    [InlineData(60, "1m 0s")]
    [InlineData(125, "2m 5s")]
    [InlineData(599, "9m 59s")]
    // Hours appear once we cross a full hour.
    [InlineData(3600, "1h 0m 0s")]
    [InlineData(3599, "59m 59s")]
    [InlineData(3661, "1h 1m 1s")]
    [InlineData(7199, "1h 59m 59s")]
    // Multi-day durations keep counting hours rather than rolling over to days.
    [InlineData(86400, "24h 0m 0s")]
    [InlineData(93784, "26h 3m 4s")]
    public void FormatDuration_formats(int seconds, string expected)
    {
        Assert.Equal(expected, TimeMath.FormatDuration(seconds));
    }

    [Theory]
    // Negative input renders the "unavailable" placeholder.
    [InlineData(-1, "--:--")]
    [InlineData(-3600, "--:--")]
    // Under an hour: "m:ss" with the seconds zero-padded and the minutes not.
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(125, "2:05")]
    [InlineData(600, "10:00")]
    [InlineData(3599, "59:59")]
    // An hour or more: "h:mm:ss" with both minutes and seconds zero-padded.
    [InlineData(3600, "1:00:00")]
    [InlineData(3661, "1:01:01")]
    [InlineData(93784, "26:03:04")]
    public void FormatCompact_formats(int seconds, string expected)
    {
        Assert.Equal(expected, TimeMath.FormatCompact(seconds));
    }

    [Fact]
    public void FormatCompact_zero_pads_minutes_and_seconds_in_the_hours_form()
    {
        // 1 hour, 2 minutes, 3 seconds: minutes and seconds must be two digits each.
        Assert.Equal("1:02:03", TimeMath.FormatCompact(3723));
    }

    [Fact]
    public void Formatters_handle_int_max_without_overflow()
    {
        // The largest int corresponds to ~596,523 hours; the helpers must still
        // produce a well-formed string rather than throwing or wrapping negative.
        Assert.Equal("596523h 14m 7s", TimeMath.FormatDuration(int.MaxValue));
        Assert.Equal("596523:14:07", TimeMath.FormatCompact(int.MaxValue));
    }

    [Fact]
    public void Formatting_is_culture_invariant()
    {
        // Switch to a culture whose number formatting differs (comma decimal
        // separators, non-ASCII digits would be a risk) and confirm the output is
        // unchanged, since these strings feed both the UI and serialized state.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("de-DE");

            Assert.Equal("1h 1m 1s", TimeMath.FormatDuration(3661));
            Assert.Equal("1:01:01", TimeMath.FormatCompact(3661));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }
}

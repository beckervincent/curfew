using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>
/// Unit tests for <see cref="Schedule"/>, the weekly 15-minute allowed-time grid.
/// </summary>
/// <remarks>
/// The schedule is "fail open": missing, short or malformed data must always be
/// treated as <em>allowed</em> so that enabling scheduling can never accidentally
/// lock the user out. A large share of these tests pins that guarantee down,
/// since it is the property other projects (App, Overlay, Service) rely on.
/// </remarks>
public class ScheduleTests
{
    // Monday = 0 … Sunday = 6.
    private const int Monday = 0;
    private const int Wednesday = 2;
    private const int Sunday = 6;

    [Fact]
    public void Constants_match_a_15_minute_weekly_grid()
    {
        Assert.Equal(7, Schedule.Days);
        Assert.Equal(96, Schedule.SlotsPerDay);
        Assert.Equal(15, Schedule.SlotMinutes);

        // The grid must tile a full 24h day with no remainder.
        Assert.Equal(24 * 60, Schedule.SlotsPerDay * Schedule.SlotMinutes);
    }

    // ---------------------------------------------------------------------
    // AllAllowed
    // ---------------------------------------------------------------------

    [Fact]
    public void AllAllowed_allows_every_slot_of_every_day()
    {
        var s = Schedule.AllAllowed();

        for (var day = 0; day < Schedule.Days; day++)
            for (var slot = 0; slot < Schedule.SlotsPerDay; slot++)
                Assert.True(s.GetSlot(day, slot), $"day {day}, slot {slot} should be allowed");
    }

    [Fact]
    public void AllAllowed_allows_the_first_and_last_minute_of_the_week()
    {
        var s = Schedule.AllAllowed();

        Assert.True(s.IsAllowed(Monday, 0));                 // Monday 00:00
        Assert.True(s.IsAllowed(Sunday, 23 * 60 + 59));      // Sunday 23:59
    }

    [Fact]
    public void AllAllowed_returns_independent_instances()
    {
        var a = Schedule.AllAllowed();
        var b = Schedule.AllAllowed();

        a.SetSlot(Monday, 0, false);

        // Mutating one instance must not bleed into another.
        Assert.False(a.GetSlot(Monday, 0));
        Assert.True(b.GetSlot(Monday, 0));
    }

    // ---------------------------------------------------------------------
    // Constructor / normalisation (fail-open)
    // ---------------------------------------------------------------------

    [Fact]
    public void Constructor_treats_null_grid_as_fully_allowed()
    {
        var s = new Schedule(null);

        Assert.True(s.IsAllowed(Wednesday, 12 * 60));
        Assert.True(s.GetSlot(Sunday, Schedule.SlotsPerDay - 1));
    }

    [Fact]
    public void Constructor_normalises_a_ragged_grid_defaulting_missing_cells_to_allowed()
    {
        // Fewer than Days rows, and the present rows are shorter than SlotsPerDay.
        var ragged = new[]
        {
            new[] { false },          // Monday: only slot 0 specified (blocked)
            new[] { false, false },   // Tuesday: slots 0..1 specified (blocked)
        };

        var s = new Schedule(ragged);

        // Specified cells survive.
        Assert.False(s.GetSlot(Monday, 0));
        Assert.False(s.GetSlot(1, 0));
        Assert.False(s.GetSlot(1, 1));

        // Missing cells within a short row default to allowed.
        Assert.True(s.GetSlot(Monday, 1));
        Assert.True(s.GetSlot(1, 2));

        // Entirely missing rows default to allowed.
        Assert.True(s.GetSlot(Sunday, 0));
        Assert.True(s.GetSlot(Sunday, Schedule.SlotsPerDay - 1));
    }

    [Fact]
    public void Constructor_copies_its_input_so_later_mutations_do_not_leak_in()
    {
        var grid = new bool[Schedule.Days][];
        for (var d = 0; d < Schedule.Days; d++)
            grid[d] = Enumerable.Repeat(true, Schedule.SlotsPerDay).ToArray();

        var s = new Schedule(grid);

        // Mutate the caller's array after construction.
        grid[Monday][0] = false;

        // The schedule must not observe the post-construction change.
        Assert.True(s.GetSlot(Monday, 0));
    }

    // ---------------------------------------------------------------------
    // Parse (fail-open)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]            // single row, not Days rows
    [InlineData("0;0;0")]              // wrong row count
    [InlineData("0;1;0;1;0;1;0;1")]    // too many rows
    public void Parse_falls_back_to_all_allowed_for_missing_or_malformed_text(string? text)
    {
        var s = Schedule.Parse(text);

        // Probe a few arbitrary points; all must be allowed.
        Assert.True(s.IsAllowed(Monday, 0));
        Assert.True(s.IsAllowed(Wednesday, 10 * 60));
        Assert.True(s.IsAllowed(Sunday, 23 * 60 + 59));
    }

    [Fact]
    public void Parse_treats_only_zero_as_blocked()
    {
        // Each row is one character long; everything except '0' means allowed.
        var s = Schedule.Parse("0;1;x;9; ;-;a");

        Assert.False(s.GetSlot(Monday, 0));   // '0' -> blocked
        Assert.True(s.GetSlot(1, 0));         // '1' -> allowed
        Assert.True(s.GetSlot(2, 0));         // 'x' -> allowed
        Assert.True(s.GetSlot(3, 0));         // '9' -> allowed
        Assert.True(s.GetSlot(4, 0));         // ' ' -> allowed
        Assert.True(s.GetSlot(5, 0));         // '-' -> allowed
        Assert.True(s.GetSlot(6, 0));         // 'a' -> allowed
    }

    [Fact]
    public void Parse_defaults_missing_positions_in_a_short_row_to_allowed()
    {
        // Seven rows (valid count), but each row is far shorter than SlotsPerDay.
        var rows = Enumerable.Repeat("0", Schedule.Days);
        var s = Schedule.Parse(string.Join(';', rows));

        for (var day = 0; day < Schedule.Days; day++)
        {
            Assert.False(s.GetSlot(day, 0));                          // specified '0'
            Assert.True(s.GetSlot(day, 1));                           // beyond the row
            Assert.True(s.GetSlot(day, Schedule.SlotsPerDay - 1));    // beyond the row
        }
    }

    // ---------------------------------------------------------------------
    // Serialize + round-tripping
    // ---------------------------------------------------------------------

    [Fact]
    public void Serialize_produces_seven_rows_of_full_width()
    {
        var text = Schedule.AllAllowed().Serialize();
        var rows = text.Split(';');

        Assert.Equal(Schedule.Days, rows.Length);
        Assert.All(rows, row => Assert.Equal(Schedule.SlotsPerDay, row.Length));
    }

    [Fact]
    public void Serialize_uses_one_for_allowed_and_zero_for_blocked()
    {
        var s = Schedule.AllAllowed();
        s.SetSlot(Monday, 0, false);

        var mondayRow = s.Serialize().Split(';')[Monday];

        Assert.Equal('0', mondayRow[0]);
        Assert.All(mondayRow.Skip(1), c => Assert.Equal('1', c));
    }

    [Fact]
    public void Serialize_roundtrips_through_parse()
    {
        var s = Schedule.AllAllowed();
        s.SetSlot(Monday, 40, false); // Monday 10:00–10:15 blocked
        s.SetSlot(Monday, 41, false);

        var round = Schedule.Parse(s.Serialize());

        Assert.False(round.GetSlot(Monday, 40));
        Assert.False(round.GetSlot(Monday, 41));
        Assert.True(round.GetSlot(Monday, 39));
    }

    [Fact]
    public void Serialize_roundtrips_a_fully_blocked_schedule_exactly()
    {
        var s = Schedule.AllAllowed();
        for (var day = 0; day < Schedule.Days; day++)
            for (var slot = 0; slot < Schedule.SlotsPerDay; slot++)
                s.SetSlot(day, slot, false);

        var text = s.Serialize();
        var round = Schedule.Parse(text);

        // Re-serialising the parsed schedule yields identical text.
        Assert.Equal(text, round.Serialize());

        for (var day = 0; day < Schedule.Days; day++)
            for (var slot = 0; slot < Schedule.SlotsPerDay; slot++)
                Assert.False(round.GetSlot(day, slot), $"day {day}, slot {slot} should be blocked");
    }

    // ---------------------------------------------------------------------
    // IsAllowed: minute-to-slot mapping and boundaries
    // ---------------------------------------------------------------------

    [Fact]
    public void IsAllowed_maps_minutes_onto_their_15_minute_slot()
    {
        var s = Schedule.AllAllowed();
        // Block 14:00–15:00 on Wednesday (slots 56..59).
        for (var slot = 56; slot < 60; slot++) s.SetSlot(Wednesday, slot, false);

        Assert.False(s.IsAllowed(Wednesday, 14 * 60));      // 14:00 — start of block
        Assert.False(s.IsAllowed(Wednesday, 14 * 60 + 59)); // 14:59 — still inside
        Assert.True(s.IsAllowed(Wednesday, 15 * 60));       // 15:00 — first allowed minute after
        Assert.True(s.IsAllowed(Wednesday, 13 * 60 + 59));  // 13:59 — last allowed minute before
    }

    [Theory]
    [InlineData(0, 0)]        // 00:00 -> slot 0
    [InlineData(14, 0)]       // 00:14 -> slot 0 (boundary low)
    [InlineData(15, 1)]       // 00:15 -> slot 1 (boundary high)
    [InlineData(59, 3)]       // 00:59 -> slot 3
    [InlineData(60, 4)]       // 01:00 -> slot 4
    [InlineData(1439, 95)]    // 23:59 -> slot 95 (last slot)
    public void IsAllowed_reads_the_slot_a_given_minute_falls_into(int minute, int expectedSlot)
    {
        var s = Schedule.AllAllowed();
        s.SetSlot(Monday, expectedSlot, false);

        Assert.False(s.IsAllowed(Monday, minute));

        // Neighbouring slots remain allowed, confirming the minute hit exactly one slot.
        if (expectedSlot > 0) Assert.True(s.IsAllowed(Monday, (expectedSlot - 1) * Schedule.SlotMinutes));
        if (expectedSlot < Schedule.SlotsPerDay - 1)
            Assert.True(s.IsAllowed(Monday, (expectedSlot + 1) * Schedule.SlotMinutes));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    public void IsAllowed_clamps_negative_minutes_into_the_first_slot(int minute)
    {
        var s = Schedule.AllAllowed();
        s.SetSlot(Monday, 0, false);   // block the first slot

        Assert.False(s.IsAllowed(Monday, minute));
    }

    [Theory]
    [InlineData(1440)]            // first minute of the next day
    [InlineData(10_000)]
    [InlineData(int.MaxValue)]
    public void IsAllowed_clamps_minutes_past_midnight_into_the_last_slot(int minute)
    {
        var s = Schedule.AllAllowed();
        s.SetSlot(Monday, Schedule.SlotsPerDay - 1, false); // block the last slot

        Assert.False(s.IsAllowed(Monday, minute));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(Schedule.Days)]   // 7 is one past Sunday
    [InlineData(99)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void IsAllowed_treats_out_of_range_weekdays_as_allowed(int weekday)
    {
        // Even a fully blocked schedule reports "allowed" for a non-existent day,
        // because there is no day to block.
        var s = Schedule.AllAllowed();
        for (var slot = 0; slot < Schedule.SlotsPerDay; slot++)
            s.SetSlot(Monday, slot, false);

        Assert.True(s.IsAllowed(weekday, 12 * 60));
    }

    // ---------------------------------------------------------------------
    // GetSlot / SetSlot: out-of-range coordinates
    // ---------------------------------------------------------------------

    [Fact]
    public void SetSlot_then_GetSlot_reflects_the_value()
    {
        var s = Schedule.AllAllowed();

        s.SetSlot(Wednesday, 10, false);
        Assert.False(s.GetSlot(Wednesday, 10));

        s.SetSlot(Wednesday, 10, true);
        Assert.True(s.GetSlot(Wednesday, 10));
    }

    [Theory]
    [InlineData(-1, 0)]                          // weekday too low
    [InlineData(Schedule.Days, 0)]               // weekday too high
    [InlineData(0, -1)]                          // slot too low
    [InlineData(0, Schedule.SlotsPerDay)]        // slot too high
    public void GetSlot_treats_out_of_range_coordinates_as_allowed(int weekday, int slot)
    {
        Assert.True(Schedule.AllAllowed().GetSlot(weekday, slot));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(Schedule.Days, 0)]
    [InlineData(0, -1)]
    [InlineData(0, Schedule.SlotsPerDay)]
    public void SetSlot_ignores_out_of_range_coordinates_without_throwing(int weekday, int slot)
    {
        var s = Schedule.AllAllowed();

        // Must neither throw nor corrupt any in-range cell.
        s.SetSlot(weekday, slot, false);

        for (var day = 0; day < Schedule.Days; day++)
            for (var inSlot = 0; inSlot < Schedule.SlotsPerDay; inSlot++)
                Assert.True(s.GetSlot(day, inSlot));
    }
}

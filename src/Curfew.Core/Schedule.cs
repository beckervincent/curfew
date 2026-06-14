namespace Curfew.Core;

/// <summary>weekly allowed-time grid at 15-min granularity: 96 slots/day across 7 days, Monday = 0 … Sunday = 6 (see <see cref="TimeMath.MondayBasedWeekday"/>)</summary>
/// <remarks>when scheduling on, screen locked whenever current slot <em>not</em> allowed. grid defaults fully allowed, so enabling without painting blocked time never locks everything. for that "fail open" guarantee, every op treats missing/short/malformed data as allowed not throw</remarks>
public sealed class Schedule
{
    /// <summary>days in grid (Monday … Sunday)</summary>
    public const int Days = 7;

    /// <summary>15-min slots in a day (24h ÷ 15min)</summary>
    public const int SlotsPerDay = 96;

    /// <summary>single slot length, minutes</summary>
    public const int SlotMinutes = 15;

    private const int MinutesPerDay = 24 * 60;

    /// <summary><c>_allowed[day][slot]</c> — <c>true</c> = usage allowed. always rectangular <see cref="Days"/> × <see cref="SlotsPerDay"/> grid</summary>
    private readonly bool[][] _allowed;

    /// <summary>wrap existing <c>[day][slot]</c> grid, normalized to rectangular <see cref="Days"/> × <see cref="SlotsPerDay"/>: missing/short row or out-of-range cell defaults allowed, so queries never throw</summary>
    /// <param name="allowed"><c>[day][slot]</c> grid; <c>null</c> = fully allowed</param>
    public Schedule(bool[][]? allowed) => _allowed = Normalize(allowed);

    /// <summary>schedule with every slot of every day allowed</summary>
    public static Schedule AllAllowed()
    {
        var grid = new bool[Days][];
        for (var d = 0; d < Days; d++)
        {
            grid[d] = new bool[SlotsPerDay];
            Array.Fill(grid[d], true);
        }
        return new Schedule(grid);
    }

    /// <summary>usage allowed at given weekday + minute-of-day</summary>
    /// <param name="weekday">Monday = 0 … Sunday = 6. out-of-range treated as allowed</param>
    /// <param name="minuteOfDay">minutes since midnight; clamped into [0, 1439]</param>
    public bool IsAllowed(int weekday, int minuteOfDay)
    {
        if (weekday is < 0 or >= Days) return true;
        var minute = Math.Clamp(minuteOfDay, 0, MinutesPerDay - 1);
        return _allowed[weekday][minute / SlotMinutes];
    }

    /// <summary>
    /// Minutes from <paramref name="minuteOfDay"/> until the next blocked slot later today (the start of a
    /// bedtime/curfew window), or <c>-1</c> if the rest of the day is allowed. Used to warn before a block.
    /// Assumes the current moment is allowed (the caller checks that); since the current slot is then allowed,
    /// the result is always positive when a later block exists.
    /// </summary>
    public int MinutesUntilBlock(int weekday, int minuteOfDay)
    {
        if (weekday is < 0 or >= Days) return -1;
        var minute = Math.Clamp(minuteOfDay, 0, MinutesPerDay - 1);
        for (var slot = minute / SlotMinutes; slot < SlotsPerDay; slot++)
            if (!_allowed[weekday][slot])
                return slot * SlotMinutes - minute;
        return -1;
    }

    /// <summary>read single slot. out-of-range = allowed</summary>
    public bool GetSlot(int weekday, int slot) =>
        !InRange(weekday, slot) || _allowed[weekday][slot];

    /// <summary>set single slot. out-of-range ignored</summary>
    public void SetSlot(int weekday, int slot, bool allowed)
    {
        if (InRange(weekday, slot))
            _allowed[weekday][slot] = allowed;
    }

    /// <summary>parse serialized form from <see cref="Serialize"/>: 7 semicolon-separated rows of <see cref="SlotsPerDay"/> chars, '0' = blocked, anything else allowed</summary>
    /// <param name="text">serialized schedule. <c>null</c>/blank/row count != <see cref="Days"/> falls back to fully allowed. within a row, missing (short row) position defaults allowed</param>
    public static Schedule Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return AllAllowed();

        var rows = text.Split(';');
        if (rows.Length != Days) return AllAllowed();

        var grid = new bool[Days][];
        for (var d = 0; d < Days; d++)
        {
            grid[d] = new bool[SlotsPerDay];
            var row = rows[d];
            for (var s = 0; s < SlotsPerDay; s++)
            {
                // missing (short row) position defaults allowed
                grid[d][s] = s >= row.Length || row[s] != '0';
            }
        }
        return new Schedule(grid);
    }

    /// <summary>serialize grid to <see cref="Days"/> semicolon-separated rows of <see cref="SlotsPerDay"/> '1' (allowed) / '0' (blocked) chars, round-trips via <see cref="Parse"/></summary>
    public string Serialize()
    {
        var parts = new string[Days];
        for (var d = 0; d < Days; d++)
        {
            var chars = new char[SlotsPerDay];
            var row = _allowed[d];
            for (var s = 0; s < SlotsPerDay; s++)
                chars[s] = row[s] ? '1' : '0';
            parts[d] = new string(chars);
        }
        return string.Join(';', parts);
    }

    /// <summary>true when coordinates hit a real grid cell</summary>
    private static bool InRange(int weekday, int slot) =>
        weekday is >= 0 and < Days && slot is >= 0 and < SlotsPerDay;

    /// <summary>copy <paramref name="source"/> into guaranteed-rectangular <see cref="Days"/> × <see cref="SlotsPerDay"/> grid, missing cell defaults allowed. returns private array caller can't mutate</summary>
    private static bool[][] Normalize(bool[][]? source)
    {
        var grid = new bool[Days][];
        for (var d = 0; d < Days; d++)
        {
            var row = new bool[SlotsPerDay];
            var src = source is not null && d < source.Length ? source[d] : null;
            for (var s = 0; s < SlotsPerDay; s++)
                row[s] = src is null || s >= src.Length || src[s];
            grid[d] = row;
        }
        return grid;
    }
}

namespace Curfew.Core;

/// <summary>
/// A weekly allowed-time grid at 15-minute granularity: 96 slots per day across
/// 7 days, with Monday = 0 … Sunday = 6 (see <see cref="TimeMath.MondayBasedWeekday"/>).
/// </summary>
/// <remarks>
/// When scheduling is enabled the screen is locked whenever the current slot is
/// <em>not</em> allowed. The grid defaults to fully allowed, so enabling the
/// schedule without painting any blocked time never locks everything. To keep
/// that "fail open" guarantee, every operation treats missing, short, or
/// otherwise malformed data as allowed rather than throwing.
/// </remarks>
public sealed class Schedule
{
    /// <summary>Number of days represented by the grid (Monday … Sunday).</summary>
    public const int Days = 7;

    /// <summary>Number of 15-minute slots in a day (24h ÷ 15min).</summary>
    public const int SlotsPerDay = 96;

    /// <summary>Length of a single slot, in minutes.</summary>
    public const int SlotMinutes = 15;

    private const int MinutesPerDay = 24 * 60;

    /// <summary><c>_allowed[day][slot]</c> — <c>true</c> means usage is allowed.
    /// Always a rectangular <see cref="Days"/> × <see cref="SlotsPerDay"/> grid.</summary>
    private readonly bool[][] _allowed;

    /// <summary>
    /// Wraps an existing <c>[day][slot]</c> grid. The input is normalised to a
    /// rectangular <see cref="Days"/> × <see cref="SlotsPerDay"/> grid: any
    /// missing row, short row, or out-of-range cell defaults to allowed, so a
    /// caller can never produce a schedule that throws when queried.
    /// </summary>
    /// <param name="allowed">A <c>[day][slot]</c> grid; <c>null</c> is treated as fully allowed.</param>
    public Schedule(bool[][]? allowed) => _allowed = Normalize(allowed);

    /// <summary>Creates a schedule in which every slot of every day is allowed.</summary>
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

    /// <summary>Whether usage is allowed at the given weekday and minute-of-day.</summary>
    /// <param name="weekday">Monday = 0 … Sunday = 6. Values outside that range are treated as allowed.</param>
    /// <param name="minuteOfDay">Minutes since midnight; clamped into [0, 1439].</param>
    public bool IsAllowed(int weekday, int minuteOfDay)
    {
        if (weekday is < 0 or >= Days) return true;
        var minute = Math.Clamp(minuteOfDay, 0, MinutesPerDay - 1);
        return _allowed[weekday][minute / SlotMinutes];
    }

    /// <summary>Reads a single slot. Out-of-range coordinates are treated as allowed.</summary>
    public bool GetSlot(int weekday, int slot) =>
        !InRange(weekday, slot) || _allowed[weekday][slot];

    /// <summary>Sets a single slot. Out-of-range coordinates are ignored.</summary>
    public void SetSlot(int weekday, int slot, bool allowed)
    {
        if (InRange(weekday, slot))
            _allowed[weekday][slot] = allowed;
    }

    /// <summary>
    /// Parses the serialised form produced by <see cref="Serialize"/>:
    /// 7 semicolon-separated rows of <see cref="SlotsPerDay"/> characters, where
    /// '0' means blocked and anything else means allowed.
    /// </summary>
    /// <param name="text">
    /// The serialised schedule. <c>null</c>, blank, or a row count other than
    /// <see cref="Days"/> falls back to fully allowed. Within a valid row, any
    /// missing (short row) position also defaults to allowed.
    /// </param>
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
                // Default any missing (short row) position to allowed.
                grid[d][s] = s >= row.Length || row[s] != '0';
            }
        }
        return new Schedule(grid);
    }

    /// <summary>
    /// Serialises the grid to <see cref="Days"/> semicolon-separated rows of
    /// <see cref="SlotsPerDay"/> '1' (allowed) / '0' (blocked) characters,
    /// round-trippable via <see cref="Parse"/>.
    /// </summary>
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

    /// <summary>True when the coordinates address a real cell of the grid.</summary>
    private static bool InRange(int weekday, int slot) =>
        weekday is >= 0 and < Days && slot is >= 0 and < SlotsPerDay;

    /// <summary>
    /// Copies <paramref name="source"/> into a guaranteed-rectangular
    /// <see cref="Days"/> × <see cref="SlotsPerDay"/> grid, defaulting any
    /// missing cell to allowed. Always returns a private array the caller cannot mutate.
    /// </summary>
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

namespace Curfew.Core;

/// <summary>
/// A weekly allowed-time grid at 15-minute granularity (96 slots per day,
/// Monday = 0 … Sunday = 6). Outside an allowed slot the screen is locked when
/// scheduling is enabled. Default is fully allowed, so enabling the schedule
/// without painting never locks everything.
/// </summary>
public sealed class Schedule
{
    public const int SlotsPerDay = 96; // 24h / 15min
    public const int SlotMinutes = 15;

    // [day][slot] — true = allowed.
    private readonly bool[][] _allowed;

    public Schedule(bool[][] allowed) => _allowed = allowed;

    public static Schedule AllAllowed()
    {
        var grid = new bool[7][];
        for (var d = 0; d < 7; d++)
        {
            grid[d] = new bool[SlotsPerDay];
            Array.Fill(grid[d], true);
        }
        return new Schedule(grid);
    }

    /// <summary>Whether usage is allowed at the given weekday and minute-of-day.</summary>
    public bool IsAllowed(int weekday, int minuteOfDay)
    {
        if (weekday is < 0 or > 6) return true;
        var slot = Math.Clamp(minuteOfDay / SlotMinutes, 0, SlotsPerDay - 1);
        return _allowed[weekday][slot];
    }

    public bool GetSlot(int weekday, int slot) =>
        weekday is >= 0 and <= 6 && slot >= 0 && slot < SlotsPerDay && _allowed[weekday][slot];

    public void SetSlot(int weekday, int slot, bool allowed)
    {
        if (weekday is >= 0 and <= 6 && slot >= 0 && slot < SlotsPerDay)
            _allowed[weekday][slot] = allowed;
    }

    /// <summary>Parse "ddd…;ddd…" — 7 days of 96 '1'/'0' chars. Missing or
    /// malformed input falls back to fully allowed.</summary>
    public static Schedule Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return AllAllowed();

        var days = text.Split(';');
        if (days.Length != 7) return AllAllowed();

        var grid = new bool[7][];
        for (var d = 0; d < 7; d++)
        {
            grid[d] = new bool[SlotsPerDay];
            var row = days[d];
            for (var s = 0; s < SlotsPerDay; s++)
            {
                // Default any missing/short positions to allowed.
                grid[d][s] = s >= row.Length || row[s] != '0';
            }
        }
        return new Schedule(grid);
    }

    public string Serialize()
    {
        var parts = new string[7];
        for (var d = 0; d < 7; d++)
        {
            var chars = new char[SlotsPerDay];
            for (var s = 0; s < SlotsPerDay; s++)
                chars[s] = _allowed[d][s] ? '1' : '0';
            parts[d] = new string(chars);
        }
        return string.Join(';', parts);
    }
}

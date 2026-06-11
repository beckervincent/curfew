using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class EventLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"curfew-events-{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best effort */ }
    }

    [Fact]
    public void Append_then_read_round_trips_newest_first()
    {
        EventLog.Append(_path, CurfewEventKind.Locked, "budget");
        EventLog.Append(_path, CurfewEventKind.FailedUnlock, "wrong pin");
        EventLog.Append(_path, CurfewEventKind.Unlocked, "passcode");

        var recent = EventLog.ReadRecent(_path, 10);

        Assert.Equal(3, recent.Count);
        Assert.Equal(CurfewEventKind.Unlocked, recent[0].Kind);   // newest first
        Assert.Equal(CurfewEventKind.FailedUnlock, recent[1].Kind);
        Assert.Equal("budget", recent[2].Detail);
    }

    [Fact]
    public void ReadRecent_caps_to_requested_count()
    {
        for (var i = 0; i < 20; i++) EventLog.Append(_path, CurfewEventKind.Locked, $"#{i}");
        Assert.Equal(5, EventLog.ReadRecent(_path, 5).Count);
    }

    [Fact]
    public void Append_trims_to_max_entries()
    {
        for (var i = 0; i < EventLog.MaxEntries + 50; i++)
            EventLog.Append(_path, CurfewEventKind.Locked, $"#{i}");

        Assert.Equal(EventLog.MaxEntries, File.ReadAllLines(_path).Length);
        // The oldest entries were dropped; the newest survives.
        Assert.Equal($"#{EventLog.MaxEntries + 49}", EventLog.ReadRecent(_path, 1)[0].Detail);
    }

    [Fact]
    public void Detail_with_tabs_or_newlines_cannot_break_the_format()
    {
        EventLog.Append(_path, CurfewEventKind.ClockTamper, "moved\tby\n10\rmin");
        var ev = Assert.Single(EventLog.ReadRecent(_path, 10));
        Assert.Equal(CurfewEventKind.ClockTamper, ev.Kind);
        Assert.DoesNotContain('\t', ev.Detail);
        Assert.DoesNotContain('\n', ev.Detail);
    }

    [Fact]
    public void Missing_file_reads_empty()
    {
        Assert.Empty(EventLog.ReadRecent(_path, 10));
    }
}

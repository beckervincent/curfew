using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"curfew-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void Open_seeds_defaults()
    {
        using var store = SettingsStore.Open(_dbPath, new DateOnly(2026, 6, 10));
        Assert.Equal("120", store.Get("limit_monday"));
        Assert.Equal(240, store.GetInt("limit_sunday", 0));
        Assert.True(store.GetBool("auto_update_enabled", false));
    }

    [Fact]
    public void Set_overrides_value()
    {
        using var store = SettingsStore.Open(_dbPath, new DateOnly(2026, 6, 10));
        store.Set("limit_monday", "90");
        Assert.Equal(90, store.GetInt("limit_monday", 0));
    }

    [Fact]
    public void No_passcode_by_default()
    {
        using var store = SettingsStore.Open(_dbPath, new DateOnly(2026, 6, 10));
        Assert.False(store.HasPasscode);
        store.Set("passcode", "1234");
        Assert.True(store.HasPasscode);
    }

    [Fact]
    public void Stale_day_rows_are_purged_on_open()
    {
        var day1 = new DateOnly(2026, 6, 9);
        using (var store = SettingsStore.Open(_dbPath, day1))
        {
            store.Set("remaining_time_2026-06-09", "100");
            store.Set("remaining_time_2026-06-01", "999"); // stale
        }

        using var reopened = SettingsStore.Open(_dbPath, day1);
        Assert.Equal("100", reopened.Get("remaining_time_2026-06-09"));
        Assert.Null(reopened.Get("remaining_time_2026-06-01"));
    }

    [Fact]
    public void GetDailyLimit_handles_out_of_range()
    {
        using var store = SettingsStore.Open(_dbPath, new DateOnly(2026, 6, 10));
        Assert.Equal(120, store.GetDailyLimit(99));
        Assert.Equal(180, store.GetDailyLimit(4)); // Friday
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}

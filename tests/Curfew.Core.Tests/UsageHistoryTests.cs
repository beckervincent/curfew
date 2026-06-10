using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>Tests for <see cref="SettingsStore.GetUsageHistory"/>.</summary>
public class UsageHistoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"curfew-usage-{Guid.NewGuid():N}.db");
    private readonly DateOnly _today = new(2026, 6, 10);

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    private SettingsStore Open() => SettingsStore.Open(_dbPath, _today);

    [Fact]
    public void Returns_requested_number_of_days_oldest_first_ending_today()
    {
        using var store = Open();

        var history = store.GetUsageHistory(7);

        Assert.Equal(7, history.Count);
        Assert.Equal(_today.AddDays(-6), history[0].Date);
        Assert.Equal(_today, history[^1].Date);
    }

    [Fact]
    public void Reports_recorded_minutes_and_zero_for_missing_days()
    {
        using var store = Open();
        store.Set(SettingsStore.UsagePrefix + _today.ToString("yyyy-MM-dd"), "5400");          // 90 min
        store.Set(SettingsStore.UsagePrefix + _today.AddDays(-2).ToString("yyyy-MM-dd"), "1800"); // 30 min

        var history = store.GetUsageHistory(7);

        Assert.Equal(90, history[^1].Minutes);
        Assert.Equal(30, history[^3].Minutes);
        Assert.Equal(0, history[^2].Minutes); // yesterday, no record
    }

    [Fact]
    public void Negative_or_garbage_values_clamp_to_zero()
    {
        using var store = Open();
        store.Set(SettingsStore.UsagePrefix + _today.ToString("yyyy-MM-dd"), "-100");
        store.Set(SettingsStore.UsagePrefix + _today.AddDays(-1).ToString("yyyy-MM-dd"), "not-a-number");

        var history = store.GetUsageHistory(2);

        Assert.Equal(0, history[0].Minutes);
        Assert.Equal(0, history[1].Minutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Days_below_one_is_treated_as_one(int days)
    {
        using var store = Open();
        Assert.Single(store.GetUsageHistory(days));
    }

    [Fact]
    public void Usage_rows_survive_reopening_unlike_remaining_rows()
    {
        var usageKey = SettingsStore.UsagePrefix + _today.AddDays(-3).ToString("yyyy-MM-dd");
        using (var store = Open())
        {
            store.Set(usageKey, "600"); // 10 min, 3 days ago
            store.Set("remaining_time_" + _today.AddDays(-3).ToString("yyyy-MM-dd"), "600");
        }

        // Reopening purges stale remaining_time_ rows but must keep usage history.
        using var reopened = Open();
        Assert.Equal(10, reopened.GetUsageHistory(7)[^4].Minutes);
        Assert.Null(reopened.Get("remaining_time_" + _today.AddDays(-3).ToString("yyyy-MM-dd")));
    }
}

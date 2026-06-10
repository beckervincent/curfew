using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>
/// Behavioural tests for <see cref="SettingsStore"/>: the SQLite-backed
/// key/value store shared by the App, Overlay and Service. The store is
/// deliberately forgiving (it self-heals from corruption and never overwrites a
/// parent's saved customisations), so these tests pin down both the happy path
/// and the defensive edge cases that keep the parental controls running.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    /// <summary>A fixed "today" used throughout so daily-row purging is deterministic.</summary>
    private static readonly DateOnly Today = new(2026, 6, 10);

    /// <summary>
    /// A unique temp-file path per test instance. xUnit constructs a fresh test
    /// instance for every test, so each test gets an isolated database that
    /// <see cref="Dispose"/> cleans up afterwards.
    /// </summary>
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"curfew-test-{Guid.NewGuid():N}.db");

    private SettingsStore OpenStore(DateOnly? today = null) =>
        SettingsStore.Open(_dbPath, today ?? Today);

    [Fact]
    public void Open_seeds_defaults_when_database_is_new()
    {
        using var store = OpenStore();

        Assert.Equal("120", store.Get("limit_monday"));
        Assert.Equal(240, store.GetInt("limit_sunday", 0));
        Assert.True(store.GetBool("auto_update_enabled", false));
    }

    [Fact]
    public void Open_seeds_every_weekday_limit_key()
    {
        using var store = OpenStore();

        // The seeded defaults must cover all seven weekday keys; an unseeded key
        // would silently fall back to the generic default and mask a regression.
        foreach (var key in SettingsStore.WeekdayKeys)
            Assert.NotNull(store.Get(key));
    }

    [Theory]
    [InlineData("limit_monday", "120")]
    [InlineData("limit_friday", "180")]
    [InlineData("limit_saturday", "240")]
    [InlineData("limit_sunday", "240")]
    [InlineData("dns_filter_mode", "off")]
    [InlineData("setup_complete", "0")]
    [InlineData("schedule_enabled", "0")]
    public void Open_seeds_expected_default_value(string key, string expected)
    {
        using var store = OpenStore();
        Assert.Equal(expected, store.Get(key));
    }

    [Fact]
    public void Open_with_invalid_path_throws_argument_exception()
    {
        Assert.Throws<ArgumentException>(() => SettingsStore.Open(null!, Today));
        Assert.Throws<ArgumentException>(() => SettingsStore.Open("", Today));
        Assert.Throws<ArgumentException>(() => SettingsStore.Open("   ", Today));
    }

    [Fact]
    public void Get_returns_null_for_unknown_key()
    {
        using var store = OpenStore();
        Assert.Null(store.Get("no_such_key"));
    }

    [Fact]
    public void Get_throws_on_null_key()
    {
        using var store = OpenStore();
        Assert.Throws<ArgumentNullException>(() => store.Get(null!));
    }

    [Fact]
    public void Set_overrides_value()
    {
        using var store = OpenStore();

        store.Set("limit_monday", "90");

        Assert.Equal(90, store.GetInt("limit_monday", 0));
    }

    [Fact]
    public void Set_replaces_existing_value_rather_than_duplicating()
    {
        using var store = OpenStore();

        store.Set("limit_monday", "90");
        store.Set("limit_monday", "30");

        Assert.Equal("30", store.Get("limit_monday"));
    }

    [Fact]
    public void Set_throws_on_null_key_or_value()
    {
        using var store = OpenStore();

        Assert.Throws<ArgumentNullException>(() => store.Set(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => store.Set("limit_monday", null!));
    }

    [Fact]
    public void Set_persists_across_reopen()
    {
        using (var store = OpenStore())
            store.Set("limit_monday", "45");

        using var reopened = OpenStore();
        Assert.Equal(45, reopened.GetInt("limit_monday", 0));
    }

    [Fact]
    public void Reopen_does_not_overwrite_customised_values_with_defaults()
    {
        // A parent's customisation must survive every subsequent open; only
        // absent keys are (re)seeded.
        using (var store = OpenStore())
            store.Set("limit_friday", "5");

        using var reopened = OpenStore();
        Assert.Equal("5", reopened.Get("limit_friday"));
    }

    [Fact]
    public void GetInt_falls_back_when_missing_or_unparseable()
    {
        using var store = OpenStore();

        Assert.Equal(7, store.GetInt("no_such_key", 7));

        store.Set("limit_monday", "not-a-number");
        Assert.Equal(7, store.GetInt("limit_monday", 7));

        store.Set("limit_monday", "-15");
        Assert.Equal(-15, store.GetInt("limit_monday", 7));
    }

    [Fact]
    public void GetBool_treats_only_one_as_true()
    {
        using var store = OpenStore();

        store.Set("flag", "1");
        Assert.True(store.GetBool("flag", false));

        // Anything other than "1" is false, even truthy-looking text.
        foreach (var falsy in new[] { "0", "true", "yes", "" })
        {
            store.Set("flag", falsy);
            Assert.False(store.GetBool("flag", true));
        }
    }

    [Fact]
    public void GetBool_uses_fallback_only_when_key_missing()
    {
        using var store = OpenStore();

        Assert.True(store.GetBool("no_such_key", true));
        Assert.False(store.GetBool("no_such_key", false));
    }

    [Fact]
    public void No_passcode_by_default()
    {
        using var store = OpenStore();

        Assert.False(store.HasPasscode);

        store.Set("passcode", "1234");
        Assert.True(store.HasPasscode);
    }

    [Fact]
    public void Empty_passcode_does_not_count_as_set()
    {
        using var store = OpenStore();

        store.Set("passcode", "");
        Assert.False(store.HasPasscode);
    }

    [Fact]
    public void Stale_day_rows_are_purged_on_open()
    {
        var day1 = new DateOnly(2026, 6, 9);
        using (var store = OpenStore(day1))
        {
            store.Set("remaining_time_2026-06-09", "100");
            store.Set("remaining_time_2026-06-01", "999"); // stale
        }

        using var reopened = OpenStore(day1);
        Assert.Equal("100", reopened.Get("remaining_time_2026-06-09"));
        Assert.Null(reopened.Get("remaining_time_2026-06-01"));
    }

    [Fact]
    public void Stale_rows_are_purged_for_every_daily_prefix()
    {
        var day1 = new DateOnly(2026, 6, 9);
        using (var store = OpenStore(day1))
        {
            // One "today" row and one stale row for each daily-scoped prefix.
            foreach (var prefix in new[]
                     { "remaining_time_", "pause_used_", "pause_log_", "session_active_" })
            {
                store.Set(prefix + "2026-06-09", "keep");
                store.Set(prefix + "2025-01-01", "drop");
            }
        }

        using var reopened = OpenStore(day1);
        foreach (var prefix in new[]
                 { "remaining_time_", "pause_used_", "pause_log_", "session_active_" })
        {
            Assert.Equal("keep", reopened.Get(prefix + "2026-06-09"));
            Assert.Null(reopened.Get(prefix + "2025-01-01"));
        }
    }

    [Fact]
    public void Purge_leaves_non_daily_rows_untouched()
    {
        using (var store = OpenStore(new DateOnly(2026, 6, 9)))
        {
            store.Set("passcode", "1234");
            store.Set("remaining_time_2025-01-01", "stale");
        }

        // Reopen on a later day; only the dated row should be purged.
        using var reopened = OpenStore(new DateOnly(2026, 6, 10));
        Assert.Equal("1234", reopened.Get("passcode"));
        Assert.Null(reopened.Get("remaining_time_2025-01-01"));
    }

    [Fact]
    public void GetDailyLimit_returns_default_for_out_of_range_index()
    {
        using var store = OpenStore();

        Assert.Equal(120, store.GetDailyLimit(-1));
        Assert.Equal(120, store.GetDailyLimit(7));
        Assert.Equal(120, store.GetDailyLimit(99));
    }

    [Theory]
    [InlineData(0, 120)] // Monday
    [InlineData(1, 120)] // Tuesday
    [InlineData(2, 120)] // Wednesday
    [InlineData(3, 120)] // Thursday
    [InlineData(4, 180)] // Friday
    [InlineData(5, 240)] // Saturday
    [InlineData(6, 240)] // Sunday
    public void GetDailyLimit_returns_seeded_default_per_weekday(int weekday, int expected)
    {
        using var store = OpenStore();
        Assert.Equal(expected, store.GetDailyLimit(weekday));
    }

    [Fact]
    public void GetDailyLimit_reflects_a_customised_weekday_value()
    {
        using var store = OpenStore();

        store.Set("limit_friday", "200");

        Assert.Equal(200, store.GetDailyLimit(4)); // Friday
    }

    [Fact]
    public void WeekdayKeys_and_WeekdayNames_are_parallel_and_full_week()
    {
        Assert.Equal(7, SettingsStore.WeekdayKeys.Length);
        Assert.Equal(SettingsStore.WeekdayKeys.Length, SettingsStore.WeekdayNames.Length);
        Assert.Equal("Monday", SettingsStore.WeekdayNames[0]);
        Assert.Equal("Sunday", SettingsStore.WeekdayNames[6]);
    }

    [Fact]
    public void Open_recovers_from_a_corrupt_database_file()
    {
        // Simulate a tampered/truncated file: random bytes that are not a valid
        // SQLite header. Open() must delete and recreate it rather than throw.
        File.WriteAllBytes(_dbPath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x42 });

        using var store = OpenStore();

        // Recreated from defaults, so seeded values are present again.
        Assert.Equal("120", store.Get("limit_monday"));
    }

    public void Dispose()
    {
        // Best-effort cleanup of the database and its WAL/SHM side files.
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(_dbPath + suffix); }
            catch { /* best effort: leftover temp files are harmless */ }
        }
    }
}

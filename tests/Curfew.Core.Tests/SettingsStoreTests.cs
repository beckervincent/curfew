using Curfew.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>Tests for <see cref="SettingsStore"/>: SQLite-backed key/value store shared by App, Overlay, Service. Forgiving (self-heals corruption, never overwrites parent's customisations); pin happy path + defensive edge cases.</summary>
public sealed class SettingsStoreTests : IDisposable
{
    /// <summary>fixed "today" so daily-row purging deterministic.</summary>
    private static readonly DateOnly Today = new(2026, 6, 10);

    /// <summary>unique temp-file path per test instance; xUnit makes fresh instance per test, so each gets isolated database that <see cref="Dispose"/> cleans up.</summary>
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

        // seeded defaults must cover all seven weekday keys; unseeded key falls back to generic default and masks regression
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
        // parent's customisation survives every later open; only absent keys (re)seeded
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

        // anything but "1" is false, even truthy-looking text
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
            // one "today" row + one stale row per daily-scoped prefix
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

        // reopen on later day; only dated row purged
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
        // tampered/truncated file: random bytes, no valid SQLite header; Open() must delete+recreate, not throw
        File.WriteAllBytes(_dbPath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x42 });

        using var store = OpenStore();

        // recreated from defaults, seeded values present again
        Assert.Equal("120", store.Get("limit_monday"));
    }

    [Fact]
    public void Recreating_a_corrupt_store_emits_a_parent_visible_event()
    {
        // corruption-recreate wipes rows; for child-writable state store resets day's counters to fresh budget, so recreation must leave trace parent can see; clean open must NOT
        var logPath = Path.Combine(Path.GetTempPath(), $"curfew-evt-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllBytes(_dbPath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x42 });
            using (SettingsStore.Open(_dbPath, Today, eventLogPath: logPath)) { }

            var events = EventLog.ReadRecent(logPath, 10);
            var recreated = events.Where(e => e.Kind == CurfewEventKind.StoreRecreated).ToList();
            Assert.Single(recreated);
            Assert.Equal(Path.GetFileName(_dbPath), recreated[0].Detail);

            // reopen now-healthy store emits nothing further
            var freshLog = Path.Combine(Path.GetTempPath(), $"curfew-evt-{Guid.NewGuid():N}.log");
            using (SettingsStore.Open(_dbPath, Today, eventLogPath: freshLog)) { }
            Assert.DoesNotContain(EventLog.ReadRecent(freshLog, 10), e => e.Kind == CurfewEventKind.StoreRecreated);
            try { File.Delete(freshLog); } catch { /* best effort */ }
        }
        finally
        {
            try { File.Delete(logPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ConfigWriter_intercepts_config_writes_but_never_state_writes()
    {
        using var store = OpenStore();
        var captured = new List<(string Key, string Value)>();

        // accepting writer (App's pipe-to-service bridge) handles config writes: value must NOT also land in local config row
        store.ConfigWriter = (k, v) => { captured.Add((k, v)); return true; };
        store.Set("limit_monday", "99");
        Assert.Contains(("limit_monday", "99"), captured);
        Assert.Equal("120", store.Get("limit_monday")); // unchanged locally, routed away

        // state key bypasses writer, written directly
        captured.Clear();
        store.Set("remaining_time_2026-06-10", "42");
        Assert.Empty(captured);
        Assert.Equal("42", store.Get("remaining_time_2026-06-10"));

        // declining writer falls through to direct local config write
        store.ConfigWriter = (_, _) => false;
        store.Set("limit_monday", "77");
        Assert.Equal("77", store.Get("limit_monday"));
    }

    [Fact]
    public void Open_does_not_recreate_the_store_on_a_transient_lock()
    {
        // security-critical mirror of corruption test: OpenResilient delete+reseed ONLY on SQLITE_CORRUPT/SQLITE_NOTADB; transient BUSY/LOCKED (another process mid-write) must PROPAGATE untouched — wiping healthy config.db over momentary lock destroys parent's passcode + every policy; widened IsCorruption (or wrong exception caught) silently turns lock into full reseed
        using (var seed = OpenStore())
        {
            seed.Set("passcode", "1234");      // value a bad recreate would wipe
            seed.Set("limit_monday", "7");     // customised policy, ditto
        }

        // hold exclusive write lock from another connection so seeding txn inside Open() hits SQLITE_BUSY (code 5) not corruption verdict; BEGIN IMMEDIATE takes write lock up front + never commit, so lock outlives Open() below
        using var holder = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        holder.Open();
        using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            begin.ExecuteNonQuery();
        }

        // non-corruption SqliteException must surface, not silently recreated store (DefaultTimeout makes Open() wait a few seconds for lock first)
        var ex = Assert.Throws<SqliteException>(() => OpenStore());
        Assert.NotEqual(11, ex.SqliteErrorCode); // not SQLITE_CORRUPT
        Assert.NotEqual(26, ex.SqliteErrorCode); // not SQLITE_NOTADB

        // release lock + confirm file preserved verbatim: passcode + customised limit still there, proving no delete+reseed ran
        using (var rollback = holder.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK";
            rollback.ExecuteNonQuery();
        }

        using var reopened = OpenStore();
        Assert.Equal("1234", reopened.Get("passcode"));
        Assert.Equal("7", reopened.Get("limit_monday"));
    }

    [Fact]
    public void HasUsageHistory_detects_scoped_usage_keys()
    {
        using var store = OpenStore();
        const string sid = "S-1-5-21-1234567890-1234567890-1234567890-1001";

        Assert.False(store.HasUsageHistory(sid));

        // add scoped usage key (used_time_<sid>_<date>)
        store.Set($"used_time_{sid}_2026-06-10", "3600");

        Assert.True(store.HasUsageHistory(sid));
    }

    [Fact]
    public void HasUsageHistory_ignores_legacy_unscoped_rows()
    {
        using var store = OpenStore();
        const string sid = "S-1-5-21-1234567890-1234567890-1234567890-1001";

        // legacy unscoped used_time_<date> row carries no SID, must NOT grandfather this user — else genuinely new user on device with legacy aggregate row skips new-user setup gate
        store.Set("used_time_2026-06-09", "1800");

        Assert.False(store.HasUsageHistory(sid));
    }

    [Fact]
    public void HasUsageHistory_returns_false_for_null_or_empty_sid()
    {
        using var store = OpenStore();

        Assert.False(store.HasUsageHistory(null));
        Assert.False(store.HasUsageHistory(""));
        Assert.False(store.HasUsageHistory("   "));
    }

    [Fact]
    public void HasUsageHistory_returns_false_when_no_usage_exists()
    {
        using var store = OpenStore();
        const string sid = "S-1-5-21-1234567890-1234567890-1234567890-1001";

        // other keys but no usage history
        store.Set("passcode", "1234");
        store.Set("limit_monday", "120");

        Assert.False(store.HasUsageHistory(sid));
    }

    [Fact]
    public void HasUsageHistory_only_matches_the_specific_sid()
    {
        using var store = OpenStore();
        const string sid1 = "S-1-5-21-1111111111-1111111111-1111111111-1001";
        const string sid2 = "S-1-5-21-2222222222-2222222222-2222222222-1002";

        // scoped usage for sid1 only (no legacy keys)
        store.Set($"used_time_{sid1}_2026-06-10", "3600");

        Assert.True(store.HasUsageHistory(sid1));
        // sid2 NOT grandfathered - no legacy keys, no scoped keys for sid2
        Assert.False(store.HasUsageHistory(sid2));
    }

    public void Dispose()
    {
        // best-effort cleanup of database + WAL/SHM side files
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(_dbPath + suffix); }
            catch { /* best effort: leftover temp files are harmless */ }
        }
    }
}

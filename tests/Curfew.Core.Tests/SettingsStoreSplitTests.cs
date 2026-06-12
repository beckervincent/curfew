using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class SettingsStoreSplitTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 6, 11);
    private readonly string _config = TempPath();
    private readonly string _state = TempPath();
    private readonly string _legacy = TempPath();

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"curfew-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var p in new[] { _config, _state, _legacy })
            foreach (var s in new[] { "", "-wal", "-shm", "-journal" })
                try { File.Delete(p + s); } catch { /* best effort */ }
    }

    [Fact]
    public void OpenSplit_routes_config_to_config_file_and_state_to_state_file()
    {
        using (var s = SettingsStore.OpenSplit(_config, _state, legacyPath: null, Today))
        {
            s.Set("passcode", "hash");                       // config
            s.Set("limit_enabled", "1");                     // config
            s.Set("remaining_time_2026-06-11", "100");       // state
            s.Set("lock_active", "1");                       // state
        }

        // The config file holds the policy keys, not the counters.
        using (var c = SettingsStore.Open(_config, Today))
        {
            Assert.Equal("hash", c.Get("passcode"));
            Assert.Null(c.Get("remaining_time_2026-06-11"));
        }

        // The state file holds the counters, not the policy.
        using (var st = SettingsStore.Open(_state, Today))
        {
            Assert.Equal("100", st.Get("remaining_time_2026-06-11"));
            Assert.Equal("1", st.Get("lock_active"));
            Assert.Null(st.Get("passcode"));
        }
    }

    [Fact]
    public void OpenSplit_migrates_a_legacy_single_file_into_both_stores()
    {
        using (var legacy = SettingsStore.Open(_legacy, Today))
        {
            legacy.Set("passcode", "H");
            legacy.Set("schedule", "0101");
            legacy.Set("used_time_2026-06-11", "60");        // state
        }

        // config/state do not exist yet → migration runs.
        using var s = SettingsStore.OpenSplit(_config, _state, _legacy, Today);

        Assert.Equal("H", s.Get("passcode"));                // migrated to config
        Assert.Equal("0101", s.Get("schedule"));             // migrated to config
        Assert.Equal("60", s.Get("used_time_2026-06-11"));   // migrated to state
    }

    [Fact]
    public void UserSid_scopes_per_user_config_with_global_fallback()
    {
        using var s = SettingsStore.OpenSplit(_config, _state, legacyPath: null, Today);

        s.Set("limit_enabled", "1");          // global (no SID set)

        s.UserSid = "S-1-5-21-1";
        Assert.Equal("1", s.Get("limit_enabled"));   // falls back to global
        s.Set("limit_enabled", "0");                 // overrides for this user
        Assert.Equal("0", s.Get("limit_enabled"));

        s.UserSid = "S-1-5-21-2";
        Assert.Equal("1", s.Get("limit_enabled"));   // other user → global

        s.UserSid = null;
        Assert.Equal("1", s.Get("limit_enabled"));   // global untouched

        // Device-wide keys are never scoped.
        s.UserSid = "S-1-5-21-1";
        s.Set("passcode", "H");
        s.UserSid = "S-1-5-21-2";
        Assert.Equal("H", s.Get("passcode"));
    }

    [Fact]
    public void OpenSplit_does_not_migrate_when_config_already_exists()
    {
        // Seed config first so migration is skipped on the next open.
        using (var s = SettingsStore.OpenSplit(_config, _state, legacyPath: null, Today))
            s.Set("passcode", "already");

        using (var legacy = SettingsStore.Open(_legacy, Today))
            legacy.Set("passcode", "should-not-win");

        using var reopened = SettingsStore.OpenSplit(_config, _state, _legacy, Today);
        Assert.Equal("already", reopened.Get("passcode"));   // legacy ignored
    }

    [Fact]
    public void OpenSplit_still_migrates_after_a_non_privileged_bootstrap()
    {
        using (var legacy = SettingsStore.Open(_legacy, Today))
            legacy.Set("passcode", "parents-real-passcode");

        // A non-privileged process (app/overlay) bootstrapped config.db with
        // defaults before the service's first split open — historically that made
        // the service skip the migration forever because the file already existed.
        using (SettingsStore.OpenSplit(_config, _state, legacyPath: null, Today, configWritable: false)) { }
        Assert.True(File.Exists(_config));

        using var service = SettingsStore.OpenSplit(_config, _state, _legacy, Today, configWritable: true);
        Assert.Equal("parents-real-passcode", service.Get("passcode"));

        // The marker is consumed: a later service open must NOT re-migrate the
        // stale legacy file over the parent's current settings.
        service.Set("passcode", "rotated");
        service.Dispose();
        using var reopened = SettingsStore.OpenSplit(_config, _state, _legacy, Today, configWritable: true);
        Assert.Equal("rotated", reopened.Get("passcode"));
    }

    [Fact]
    public void GetUsageHistory_reads_per_user_rows_and_sums_without_a_sid()
    {
        using var s = SettingsStore.OpenSplit(_config, _state, legacyPath: null, Today);

        // The overlay writes usage as used_time_<sid>_<date> (seconds).
        s.Set("used_time_S-1-5-21-1_2026-06-11", "600");
        s.Set("used_time_S-1-5-21-2_2026-06-11", "300");
        s.Set("used_time_2026-06-11", "60"); // legacy pre-per-user row

        s.UserSid = "S-1-5-21-1";
        var one = s.GetUsageHistory(1);
        Assert.Equal(10, one[^1].Minutes);

        s.UserSid = null;
        var all = s.GetUsageHistory(1);
        Assert.Equal(16, all[^1].Minutes); // 600 + 300 + 60 seconds = 16 minutes
    }
}

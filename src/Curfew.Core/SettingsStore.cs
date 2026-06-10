using Microsoft.Data.Sqlite;

namespace Curfew.Core;

/// <summary>
/// SQLite-backed key/value settings plus per-day state. A corrupt database is
/// deleted and recreated rather than throwing.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public static readonly string[] WeekdayKeys =
    {
        "limit_monday", "limit_tuesday", "limit_wednesday", "limit_thursday",
        "limit_friday", "limit_saturday", "limit_sunday",
    };

    public static readonly string[] WeekdayNames =
    {
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
    };

    private static readonly (string Key, string Value)[] Defaults =
    {
        ("limit_monday", "120"),
        ("limit_tuesday", "120"),
        ("limit_wednesday", "120"),
        ("limit_thursday", "120"),
        ("limit_friday", "180"),
        ("limit_saturday", "240"),
        ("limit_sunday", "240"),
        ("warning1_minutes", "10"),
        ("warning1_message", "10 minutes remaining!"),
        ("warning2_minutes", "5"),
        ("warning2_message", "5 minutes remaining!"),
        ("blocking_message", "Your screen time limit has been reached."),
        ("pause_enabled", "1"),
        ("pause_daily_budget", "45"),
        ("pause_max_duration", "20"),
        ("pause_cooldown", "15"),
        ("pause_min_active_time", "10"),
        ("lock_screen_timeout", "600"),
        ("idle_enabled", "1"),
        ("idle_timeout_minutes", "5"),
        ("auto_update_enabled", "1"),
        // Content filtering: "off" | "malware" | "family". Chosen during setup.
        ("dns_filter_mode", "off"),
        // Block third-party DoH at the firewall so browsers can't bypass the filter.
        ("block_doh_bypass", "1"),
        // Time Manipulation Guarding: correct the clock from NTP before resetting time.
        ("time_guard_enabled", "1"),
        // Set once the first-run setup wizard has completed.
        ("setup_complete", "0"),
        // Daily hours budget on/off (the per-day limits). Parent's choice.
        ("limit_enabled", "1"),
        // Weekly allowed-time schedule on/off. Default off; absent schedule = all allowed.
        ("schedule_enabled", "0"),
    };

    private SettingsStore(SqliteConnection connection) => _connection = connection;

    /// <summary>Opens the database at the given path, seeding defaults and
    /// purging stale per-day rows. Recreates the file if it is corrupt.</summary>
    public static SettingsStore Open(string databasePath, DateOnly today)
    {
        try
        {
            return Initialize(databasePath, today);
        }
        catch (SqliteException)
        {
            try { File.Delete(databasePath); } catch { /* best effort */ }
            return Initialize(databasePath, today);
        }
    }

    private static SettingsStore Initialize(string databasePath, DateOnly today)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();

        Execute(connection,
            "CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)");

        foreach (var (key, value) in Defaults)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO settings (key, value) VALUES ($k, $v)";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }

        // Drop per-day rows from previous days so the table cannot grow forever.
        var todayKey = today.ToString("yyyy-MM-dd");
        foreach (var prefix in new[] { "remaining_time_", "pause_used_", "pause_log_", "session_active_" })
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "DELETE FROM settings WHERE key LIKE $p || '%' AND key != $p || $today";
            cmd.Parameters.AddWithValue("$p", prefix);
            cmd.Parameters.AddWithValue("$today", todayKey);
            cmd.ExecuteNonQuery();
        }

        return new SettingsStore(connection);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public string? Get(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), out var v) ? v : fallback;

    public bool GetBool(string key, bool fallback)
    {
        var raw = Get(key);
        return raw is null ? fallback : raw == "1";
    }

    /// <summary>Daily limit in minutes for a weekday (0 = Monday … 6 = Sunday).</summary>
    public int GetDailyLimit(int weekday) =>
        weekday is >= 0 and < 7 ? GetInt(WeekdayKeys[weekday], 120) : 120;

    public bool HasPasscode => !string.IsNullOrEmpty(Get("passcode"));

    public void Dispose() => _connection.Dispose();
}

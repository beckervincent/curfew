using Microsoft.Data.Sqlite;

namespace Curfew.Core;

/// <summary>
/// SQLite-backed key/value settings store plus per-day state (such as the
/// remaining-time and pause counters that are keyed by date).
/// </summary>
/// <remarks>
/// <para>
/// The store is intentionally forgiving: it is opened by the App (WinUI 3),
/// the Overlay (Win32) and the Service, sometimes concurrently, on machines
/// where the database file may have been tampered with or truncated. If the
/// file cannot be parsed as a valid database it is deleted and recreated from
/// <see cref="Defaults"/> rather than throwing, so a corrupt file can never
/// brick the parental controls.
/// </para>
/// <para>
/// All values are stored as text; the typed accessors
/// (<see cref="GetInt"/>, <see cref="GetBool"/>) parse on read and fall back
/// to a caller-supplied default when a key is missing or malformed. This means
/// callers never have to defend against bad data in the database.
/// </para>
/// </remarks>
public sealed class SettingsStore : IDisposable
{
    // Two backing connections. In single-file mode (Open) both reference the same
    // connection. In split mode (OpenSplit) _config is the write-protected
    // config.db and _state is the child-writable state.db; each key is routed to
    // one of them by SettingsPartition.StoreFor.
    private readonly SqliteConnection _config;
    private readonly SqliteConnection _state;

    /// <summary>
    /// Settings keys for the per-weekday time limit, ordered Monday-first
    /// (index 0 = Monday … index 6 = Sunday) to match <see cref="DayOfWeek"/>
    /// shifted so the week starts on Monday.
    /// </summary>
    public static readonly string[] WeekdayKeys =
    {
        "limit_monday", "limit_tuesday", "limit_wednesday", "limit_thursday",
        "limit_friday", "limit_saturday", "limit_sunday",
    };

    /// <summary>
    /// Human-readable weekday names parallel to <see cref="WeekdayKeys"/>
    /// (index 0 = Monday … index 6 = Sunday), suitable for display in the UI.
    /// </summary>
    public static readonly string[] WeekdayNames =
    {
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
    };

    /// <summary>Default daily limit, in minutes, used when no weekday-specific value is configured.</summary>
    private const int DefaultDailyLimitMinutes = 120;

    /// <summary>
    /// Prefixes for rows that are scoped to a single calendar day. On
    /// <see cref="Open"/>, every row with one of these prefixes that does not
    /// belong to "today" is deleted so the table cannot grow without bound.
    /// </summary>
    private static readonly string[] DailyRowPrefixes =
    {
        "remaining_time_", "pause_used_", "pause_log_", "session_active_",
    };

    /// <summary>
    /// Seed values inserted on first open (and only for keys that are absent,
    /// so a parent's customisations are never overwritten on later opens).
    /// </summary>
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

    private readonly DateOnly _today;

    private SettingsStore(SqliteConnection config, SqliteConnection state, DateOnly today)
    {
        _config = config;
        _state = state;
        _today = today;
    }

    /// <summary>Picks the connection a key is routed to.</summary>
    private SqliteConnection ConnectionFor(string key) =>
        SettingsPartition.StoreFor(key) == SettingsStoreKind.State ? _state : _config;

    /// <summary>
    /// Optional sink for CONFIG writes. When set and it returns <c>true</c> for a
    /// key, that write is considered handled (e.g. forwarded to the SYSTEM service
    /// over the config pipe) and is NOT written to the local config connection.
    /// Returning <c>false</c> (or leaving this null) falls through to a direct
    /// write. State writes never use this. The app sets this so its config changes
    /// go through the service once config.db is write-protected; the service and
    /// overlay leave it null (the service owns config.db; the overlay only writes
    /// state).
    /// </summary>
    public Func<string, string, bool>? ConfigWriter { get; set; }

    /// <summary>
    /// Opens (or creates) the database at <paramref name="databasePath"/>,
    /// seeding any missing defaults and purging per-day rows that do not belong
    /// to <paramref name="today"/>. If the existing file is corrupt it is
    /// deleted and recreated from defaults.
    /// </summary>
    /// <param name="databasePath">Absolute path to the SQLite database file.</param>
    /// <param name="today">The current local date, used to decide which per-day rows to keep.</param>
    /// <returns>An open <see cref="SettingsStore"/>; never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="databasePath"/> is null, empty or whitespace.</exception>
    public static SettingsStore Open(string databasePath, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path must be provided.", nameof(databasePath));

        // Single-file mode: one connection backs both config and state. Defaults are
        // seeded and stale per-day rows purged on the same file.
        var connection = OpenResilient(databasePath, c => Prepare(c, seedDefaults: true, today, purge: true));
        return new SettingsStore(connection, connection, today);
    }

    /// <summary>
    /// Opens the split stores: <paramref name="configPath"/> for the write-protected
    /// policy/secrets and <paramref name="statePath"/> for the child-writable per-day
    /// counters. On the first split open, if <paramref name="legacyPath"/> still
    /// holds a single-file database its keys are migrated into the correct store.
    /// </summary>
    /// <param name="configPath">Path to config.db (defaults seeded here).</param>
    /// <param name="statePath">Path to state.db (per-day rows purged here).</param>
    /// <param name="legacyPath">Optional pre-split database to migrate from, or null.</param>
    /// <param name="today">Current local date, for the per-day purge.</param>
    public static SettingsStore OpenSplit(string configPath, string statePath, string? legacyPath, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new ArgumentException("Config path must be provided.", nameof(configPath));
        if (string.IsNullOrWhiteSpace(statePath))
            throw new ArgumentException("State path must be provided.", nameof(statePath));

        var migrate = !string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath) && !File.Exists(configPath);

        var config = OpenResilient(configPath, c => Prepare(c, seedDefaults: true, today, purge: false));
        var state = OpenResilient(statePath, c => Prepare(c, seedDefaults: false, today, purge: true));

        if (migrate)
        {
            // Best-effort: a failed migration just leaves the freshly-seeded defaults.
            try { MigrateFromLegacy(legacyPath!, config, state); }
            catch (SqliteException) { /* corrupt legacy file — ignore */ }
        }

        return new SettingsStore(config, state, today);
    }

    /// <summary>Opens a connection and runs <paramref name="setup"/>, recreating the file once if it is corrupt.</summary>
    private static SqliteConnection OpenResilient(string path, Action<SqliteConnection> setup)
    {
        try
        {
            return InitConnection(path, setup);
        }
        catch (SqliteException)
        {
            // Corrupt/truncated/not-SQLite: drop it and recreate from defaults so the
            // controls keep working. A second failure propagates (it's the directory).
            TryDeleteDatabaseFiles(path);
            return InitConnection(path, setup);
        }
    }

    private static SqliteConnection InitConnection(string path, Action<SqliteConnection> setup)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            // Wait a few seconds for a lock instead of failing instantly when
            // another process (App/Overlay/Service) is mid-write.
            DefaultTimeout = 5,
        }.ToString());

        try
        {
            connection.Open();
            // WAL lets readers and a writer proceed concurrently (several processes
            // share these files) and forces a header check, surfacing corruption here.
            Execute(connection, "PRAGMA journal_mode = WAL");
            Execute(connection,
                "CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
            setup(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Prepare(SqliteConnection connection, bool seedDefaults, DateOnly today, bool purge)
    {
        using var transaction = connection.BeginTransaction();
        if (seedDefaults) SeedDefaults(connection, transaction);
        if (purge) PurgeStaleDailyRows(connection, transaction, today);
        transaction.Commit();
    }

    /// <summary>Copies every key from a pre-split single-file database into the right store.</summary>
    private static void MigrateFromLegacy(string legacyPath, SqliteConnection config, SqliteConnection state)
    {
        using var legacy = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = legacyPath,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 5,
        }.ToString());
        legacy.Open();

        var rows = new List<(string Key, string Value)>();
        using (var read = legacy.CreateCommand())
        {
            read.CommandText = "SELECT key, value FROM settings";
            using var reader = read.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        // Overwrite the seeded config defaults with the parent's real values.
        foreach (var (key, value) in rows)
        {
            var target = SettingsPartition.StoreFor(key) == SettingsStoreKind.State ? state : config;
            using var cmd = target.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ($k, $v)";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    private static void SeedDefaults(SqliteConnection connection, SqliteTransaction transaction)
    {
        // Insert defaults only for keys that are absent so a parent's saved
        // settings survive every subsequent open.
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT OR IGNORE INTO settings (key, value) VALUES ($k, $v)";
        var keyParam = cmd.Parameters.Add("$k", SqliteType.Text);
        var valueParam = cmd.Parameters.Add("$v", SqliteType.Text);

        foreach (var (key, value) in Defaults)
        {
            keyParam.Value = key;
            valueParam.Value = value;
            cmd.ExecuteNonQuery();
        }
    }

    private static void PurgeStaleDailyRows(
        SqliteConnection connection, SqliteTransaction transaction, DateOnly today)
    {
        // Drop per-day rows from previous days so the table cannot grow forever.
        var todaySuffix = today.ToString("yyyy-MM-dd");
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "DELETE FROM settings WHERE key LIKE $p || '%' AND key != $p || $today";
        var prefixParam = cmd.Parameters.Add("$p", SqliteType.Text);
        cmd.Parameters.AddWithValue("$today", todaySuffix);

        foreach (var prefix in DailyRowPrefixes)
        {
            prefixParam.Value = prefix;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Best-effort removal of the database file and its WAL/SHM side files so a
    /// corrupt database can be fully recreated.
    /// </summary>
    private static void TryDeleteDatabaseFiles(string databasePath)
    {
        // Releasing pooled connections first lets Windows unlock the file.
        try { SqliteConnection.ClearAllPools(); } catch { /* best effort */ }

        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(databasePath + suffix); }
            catch { /* best effort: a leftover side file is harmless */ }
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns the raw stored value for <paramref name="key"/>, or <see langword="null"/> if it is not set.</summary>
    public string? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var cmd = ConnectionFor(key).CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>, replacing any existing value.</summary>
    public void Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        // Route config writes through the config writer (the SYSTEM service) when one
        // is set and it accepts the write; otherwise fall through to a direct write.
        if (SettingsPartition.StoreFor(key) == SettingsStoreKind.Config
            && ConfigWriter is { } writer && writer(key, value))
        {
            return;
        }

        using var cmd = ConnectionFor(key).CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads <paramref name="key"/> as an integer, returning
    /// <paramref name="fallback"/> when the key is missing or not a valid integer.
    /// </summary>
    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), out var v) ? v : fallback;

    /// <summary>
    /// Reads <paramref name="key"/> as a boolean flag, returning
    /// <paramref name="fallback"/> when the key is missing. A stored value of
    /// <c>"1"</c> is <see langword="true"/>; any other stored value is
    /// <see langword="false"/>.
    /// </summary>
    public bool GetBool(string key, bool fallback)
    {
        var raw = Get(key);
        return raw is null ? fallback : raw == "1";
    }

    /// <summary>
    /// Returns the configured daily limit in minutes for the given weekday,
    /// where <paramref name="weekday"/> is 0 = Monday … 6 = Sunday. Out-of-range
    /// indexes and unparseable values fall back to the default daily limit.
    /// </summary>
    public int GetDailyLimit(int weekday) =>
        weekday is >= 0 and < 7
            ? GetInt(WeekdayKeys[weekday], DefaultDailyLimitMinutes)
            : DefaultDailyLimitMinutes;

    /// <summary>Whether a parental passcode has been set.</summary>
    public bool HasPasscode => !string.IsNullOrEmpty(Get("passcode"));

    /// <summary>Settings-key prefix for per-day recorded active screen time (seconds).</summary>
    public const string UsagePrefix = "used_time_";

    /// <summary>One day's recorded screen time.</summary>
    public readonly record struct UsageDay(DateOnly Date, int Minutes);

    /// <summary>
    /// Active screen-time per day for the last <paramref name="days"/> days,
    /// oldest first and ending on the day the store was opened. Days without a
    /// record report zero. These rows are intentionally exempt from the per-day
    /// purge so history accumulates.
    /// </summary>
    public IReadOnlyList<UsageDay> GetUsageHistory(int days)
    {
        if (days < 1) days = 1;

        var history = new List<UsageDay>(days);
        for (var i = days - 1; i >= 0; i--)
        {
            var date = _today.AddDays(-i);
            var seconds = GetInt(UsagePrefix + date.ToString("yyyy-MM-dd"), 0);
            history.Add(new UsageDay(date, Math.Max(0, seconds) / 60));
        }
        return history;
    }

    /// <summary>Closes the underlying database connection(s).</summary>
    public void Dispose()
    {
        _config.Dispose();
        if (!ReferenceEquals(_state, _config)) _state.Dispose();
    }
}

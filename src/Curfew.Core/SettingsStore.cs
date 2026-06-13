using System.Globalization;
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
    /// When set to a Windows user SID, per-user config keys (see
    /// <see cref="SettingsPartition.IsPerUser"/>) are transparently scoped to that
    /// user: a read prefers the user's value and falls back to the unscoped/global
    /// value, and a write stores the user's value. Device-wide keys and state keys
    /// are never scoped. Null means "no scoping" (the legacy/global behaviour).
    /// </summary>
    public string? UserSid { get; set; }

    /// <summary>Resolves the effective stored key for a base key, applying per-user scoping.</summary>
    private string EffectiveKey(string key) =>
        UserSid is { Length: > 0 } sid && SettingsPartition.IsPerUser(key)
            ? SettingsPartition.Scope(key, sid)
            : key;

    /// <summary>
    /// Opens (or creates) the database at <paramref name="databasePath"/>,
    /// seeding any missing defaults and purging per-day rows that do not belong
    /// to <paramref name="today"/>. If the existing file is corrupt it is
    /// deleted and recreated from defaults.
    /// </summary>
    /// <param name="databasePath">Absolute path to the SQLite database file.</param>
    /// <param name="today">The current local date, used to decide which per-day rows to keep.</param>
    /// <param name="eventLogPath">
    /// Where a corruption-recreate's <see cref="CurfewEventKind.StoreRecreated"/>
    /// event is appended; defaults to the production log. Tests pass a temp file to
    /// assert the emission.
    /// </param>
    /// <returns>An open <see cref="SettingsStore"/>; never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="databasePath"/> is null, empty or whitespace.</exception>
    public static SettingsStore Open(string databasePath, DateOnly today, string? eventLogPath = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path must be provided.", nameof(databasePath));

        // Single-file mode: one connection backs both config and state. Defaults are
        // seeded and stale per-day rows purged on the same file.
        var connection = OpenResilient(
            databasePath, c => Prepare(c, seedDefaults: true, today, purge: true), eventLogPath: eventLogPath);
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
    /// <param name="configWritable">
    /// True for the SYSTEM service (creates/seeds/migrates config.db). False for the
    /// app/overlay, which open config.db read-only (it is ACL'd read-only for them);
    /// they never migrate — the service does that on boot.
    /// </param>
    /// <param name="eventLogPath">
    /// Where a corruption-recreate's <see cref="CurfewEventKind.StoreRecreated"/>
    /// event is appended; defaults to the production log. Tests pass a temp file to
    /// assert that recreating state.db (a child-triggerable counter reset) is traced.
    /// </param>
    public static SettingsStore OpenSplit(
        string configPath, string statePath, string? legacyPath, DateOnly today,
        bool configWritable = true, string? eventLogPath = null)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new ArgumentException("Config path must be provided.", nameof(configPath));
        if (string.IsNullOrWhiteSpace(statePath))
            throw new ArgumentException("State path must be provided.", nameof(statePath));

        var configExisted = File.Exists(configPath);

        var config = configWritable
            ? OpenResilient(
                configPath, c => Prepare(c, seedDefaults: true, today, purge: false),
                ConfigJournalMode, eventLogPath)
            : OpenConfigReadOnly(configPath, today, eventLogPath);

        SqliteConnection state;
        try
        {
            state = OpenResilient(
                statePath, c => Prepare(c, seedDefaults: false, today, purge: true), eventLogPath: eventLogPath);
        }
        catch
        {
            config.Dispose();
            throw;
        }

        // Migrate when the legacy single-file database still exists and config.db
        // either did not exist yet or was merely bootstrapped (defaults only) by a
        // non-privileged process before the service got its first chance — the
        // bootstrap marker distinguishes that from a config the parent has owned
        // for a while, which must never be overwritten with stale legacy values.
        var migrate = configWritable && !string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath)
            && (!configExisted || GetDirect(config, BootstrapMarkerKey) == "1");

        if (migrate)
        {
            // Best-effort: a failed migration just leaves the freshly-seeded defaults.
            try { MigrateFromLegacy(legacyPath!, config, state); }
            catch (SqliteException) { /* corrupt legacy file — ignore */ }
        }

        // The service owns config.db from here on; the bootstrap marker has served
        // its purpose either way (migrated, or there was no legacy data to migrate).
        if (configWritable)
        {
            try { Execute(config, $"DELETE FROM settings WHERE key = '{BootstrapMarkerKey}'"); }
            catch (SqliteException) { /* best effort */ }
        }

        return new SettingsStore(config, state, today);
    }

    /// <summary>
    /// Marker row set when a non-privileged process bootstraps config.db (see
    /// <see cref="OpenConfigReadOnly"/>), so the service can still tell that the
    /// legacy migration has not happened yet even though the file exists.
    /// </summary>
    private const string BootstrapMarkerKey = "config_bootstrap";

    /// <summary>
    /// Journal mode for config.db. WAL would put committed config data into
    /// child-writable <c>-wal</c>/<c>-shm</c> sidecars (the deny-ACE covers only
    /// the main file) and WAL readers need write access to <c>-shm</c>; PERSIST
    /// keeps a single permanent <c>-journal</c> file the service can ACL alongside
    /// the database, and read-only opens need no sidecar writes at all.
    /// </summary>
    private const string ConfigJournalMode = "PERSIST";

    /// <summary>Reads one raw row off a specific connection, bypassing routing/scoping.</summary>
    private static string? GetDirect(SqliteConnection connection, string key)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>Opens a connection and runs <paramref name="setup"/>, recreating the file once if it is corrupt.</summary>
    /// <param name="eventLogPath">
    /// Destination for the <see cref="CurfewEventKind.StoreRecreated"/> event on a
    /// corruption-recreate; defaults to the production log. Threaded through so tests
    /// can redirect it to a temp file and assert the emission.
    /// </param>
    private static SqliteConnection OpenResilient(
        string path, Action<SqliteConnection> setup, string journalMode = "WAL", string? eventLogPath = null)
    {
        try
        {
            return InitConnection(path, setup, journalMode: journalMode);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            // Corrupt/truncated/not-SQLite: drop it and recreate from defaults so the
            // controls keep working. A second failure propagates (it's the directory).
            // Anything that is NOT corruption (BUSY/LOCKED contention, an I/O error,
            // a permission problem) propagates immediately instead — deleting a
            // healthy database over a transient lock would destroy the parent's
            // settings, including the passcode.
            TryDeleteDatabaseFiles(path);
            RecordStoreRecreated(path, eventLogPath);
            return InitConnection(path, setup, journalMode: journalMode);
        }
    }

    /// <summary>
    /// Whether a SQLite failure means the file itself is not a usable database
    /// (as opposed to a transient lock, an I/O problem, or a permission error).
    /// </summary>
    private static bool IsCorruption(SqliteException ex) =>
        ex.SqliteErrorCode is 11 or 26; // SQLITE_CORRUPT, SQLITE_NOTADB

    /// <summary>
    /// Leaves a parent-visible event when a store had to be deleted and recreated:
    /// for state.db that wipes the day's counters back to a fresh budget, which a
    /// child could provoke deliberately by corrupting the child-writable file.
    /// </summary>
    /// <param name="path">The database file that was recreated (its name is the event detail).</param>
    /// <param name="eventLogPath">
    /// Where to append the event, or <see langword="null"/> to use the production log
    /// (<see cref="CurfewPaths.EventLogFile"/>). Tests redirect it to a temp file to
    /// assert the emission (the only parent-facing trace of a child-triggerable state
    /// reset). The default-path lookup is resolved INSIDE the try below: computing
    /// <see cref="CurfewPaths.EventLogFile"/> creates <c>%ProgramData%\Curfew</c> and
    /// can itself throw (a reparse-point junction makes <see cref="CurfewPaths.DataDirectory"/>
    /// fail closed), and recreating the store must still proceed regardless.
    /// </param>
    private static void RecordStoreRecreated(string path, string? eventLogPath)
    {
        try
        {
            EventLog.Append(
                eventLogPath ?? CurfewPaths.EventLogFile, CurfewEventKind.StoreRecreated, Path.GetFileName(path));
        }
        catch
        {
            // Diagnostics only; recreating the store must proceed regardless.
        }
    }

    private static SqliteConnection InitConnection(
        string path, Action<SqliteConnection> setup, bool readOnly = false, string journalMode = "WAL")
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            // Wait a few seconds for a lock instead of failing instantly when
            // another process (App/Overlay/Service) is mid-write.
            DefaultTimeout = 5,
        }.ToString());

        try
        {
            connection.Open();
            if (!readOnly)
            {
                // WAL lets readers and a writer proceed concurrently (several processes
                // share these files) and forces a header check, surfacing corruption.
                // config.db uses PERSIST instead (see ConfigJournalMode). Changing the
                // mode needs an exclusive lock, so a refusal while another process
                // holds the file is tolerated (the next open tries again) — but a
                // corruption verdict must still propagate so OpenResilient recreates.
                try { Execute(connection, $"PRAGMA journal_mode = {journalMode}"); }
                catch (SqliteException ex) when (!IsCorruption(ex)) { /* keep the current mode */ }
                Execute(connection,
                    "CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
            }
            setup(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens config.db read-only for a non-privileged process (app/overlay). Falls
    /// back to a writable open + seed if the file does not exist yet — the case
    /// before the SYSTEM service has created it, or before the ACL is applied. This
    /// keeps unlock working (a direct read of the passcode hash) even though the
    /// app can no longer WRITE config once the ACL is on.
    /// </summary>
    private static SqliteConnection OpenConfigReadOnly(string path, DateOnly today, string? eventLogPath = null)
    {
        if (File.Exists(path))
        {
            try { return InitConnection(path, static _ => { }, readOnly: true); }
            catch (SqliteException) { /* fall through and try to bootstrap it */ }
        }

        // Mark the bootstrap so the service can still run the legacy migration on
        // its next start: a defaults-only file created here must not be mistaken
        // for a config the parent already owns (see OpenSplit's migrate decision).
        return OpenResilient(path, c =>
        {
            Prepare(c, seedDefaults: true, today, purge: false);
            Execute(c, $"INSERT OR REPLACE INTO settings (key, value) VALUES ('{BootstrapMarkerKey}', '1')");
        }, ConfigJournalMode, eventLogPath);
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

        // Overwrite the seeded config defaults with the parent's real values. One
        // transaction per target store so an interruption (power loss, a thrown
        // exception mid-copy) leaves no half-migrated database behind — either all
        // of a store's rows land or none do, and the next boot can retry cleanly.
        using var configTx = config.BeginTransaction();
        using var stateTx = state.BeginTransaction();
        foreach (var (key, value) in rows)
        {
            var isState = SettingsPartition.StoreFor(key) == SettingsStoreKind.State;
            using var cmd = (isState ? state : config).CreateCommand();
            cmd.Transaction = isState ? stateTx : configTx;
            cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ($k, $v)";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
        configTx.Commit();
        stateTx.Commit();
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
        // Invariant culture to match the writers exactly: a user-selected region
        // with a non-Gregorian calendar would otherwise format a different "today"
        // and purge the live rows.
        var todaySuffix = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        // Keep only rows whose key ends in today's date. Matching the date as a
        // suffix (rather than prefix+date exactly) keeps per-user keys that embed a
        // SID between the prefix and the date, e.g. remaining_time_<sid>_<date>.
        cmd.CommandText =
            "DELETE FROM settings WHERE key LIKE $p || '%' AND key NOT LIKE '%' || $today";
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

        var effective = EffectiveKey(key);
        var value = GetRaw(effective);
        // A per-user read falls back to the unscoped/global value when the user has
        // no override, so existing (global) settings keep applying to every user.
        if (value is null && effective != key) value = GetRaw(key);
        return value;
    }

    private string? GetRaw(string key)
    {
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

        var effective = EffectiveKey(key);

        // Route config writes through the config writer (the SYSTEM service) when one
        // is set and it accepts the write; otherwise fall through to a direct write.
        if (SettingsPartition.StoreFor(effective) == SettingsStoreKind.Config
            && ConfigWriter is { } writer && writer(effective, value))
        {
            return;
        }

        using var cmd = ConnectionFor(effective).CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", effective);
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
    /// <remarks>
    /// The overlay writes usage as <c>used_time_&lt;sid&gt;_&lt;date&gt;</c>. With
    /// <see cref="UserSid"/> set this reads that user's rows; otherwise it sums all
    /// users for the day (which also covers legacy <c>used_time_&lt;date&gt;</c>
    /// rows written before per-user counters existed).
    /// </remarks>
    public IReadOnlyList<UsageDay> GetUsageHistory(int days)
    {
        if (days < 1) days = 1;

        var history = new List<UsageDay>(days);
        for (var i = days - 1; i >= 0; i--)
        {
            var date = _today.AddDays(-i);
            var suffix = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var seconds = UserSid is { Length: > 0 } sid
                ? GetInt($"{UsagePrefix}{sid}_{suffix}", 0)
                : SumUsageForDate(suffix);
            history.Add(new UsageDay(date, Math.Max(0, seconds) / 60));
        }
        return history;
    }

    /// <summary>Sums one day's usage rows across every user (and the legacy unscoped row).</summary>
    private int SumUsageForDate(string dateSuffix)
    {
        var total = 0;
        try
        {
            using var cmd = _state.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key LIKE $p || '%' AND key LIKE '%' || $d";
            cmd.Parameters.AddWithValue("$p", UsagePrefix);
            cmd.Parameters.AddWithValue("$d", dateSuffix);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (int.TryParse(reader.GetString(0), out var seconds) && seconds > 0)
                    total += seconds;
            }
        }
        catch (SqliteException)
        {
            // A read failure just reports zero for the day; the chart is advisory.
        }
        return total;
    }

    /// <summary>Closes the underlying database connection(s).</summary>
    public void Dispose()
    {
        _config.Dispose();
        if (!ReferenceEquals(_state, _config)) _state.Dispose();
    }
}

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Curfew.Core;

/// <summary>SQLite key/value settings store + per-day state (date-keyed remaining-time and pause counters).</summary>
/// <remarks>
/// <para>
/// Forgiving: opened by App (WinUI 3), Overlay (Win32), Service, sometimes concurrently, on machines where the db file may be tampered or truncated. Unparseable file gets deleted and recreated from <see cref="Defaults"/> instead of throwing, so corruption never bricks parental controls.
/// </para>
/// <para>
/// Values stored as text; typed accessors (<see cref="GetInt"/>, <see cref="GetBool"/>) parse on read, fall back to caller default when key missing or malformed. Callers never defend against bad data.
/// </para>
/// </remarks>
public sealed class SettingsStore : IDisposable
{
    // two backing connections. single-file mode (Open): both same connection.
    // split mode (OpenSplit): _config = write-protected config.db, _state =
    // child-writable state.db; SettingsPartition.StoreFor routes each key.
    private readonly SqliteConnection _config;
    private readonly SqliteConnection _state;

    /// <summary>Per-weekday time-limit keys, Monday-first (index 0 = Monday … 6 = Sunday), matching <see cref="DayOfWeek"/> shifted to Monday start.</summary>
    public static readonly string[] WeekdayKeys =
    {
        "limit_monday", "limit_tuesday", "limit_wednesday", "limit_thursday",
        "limit_friday", "limit_saturday", "limit_sunday",
    };

    /// <summary>Weekday names parallel to <see cref="WeekdayKeys"/> (index 0 = Monday … 6 = Sunday), for UI display.</summary>
    public static readonly string[] WeekdayNames =
    {
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
    };

    /// <summary>Default daily limit, minutes, when no weekday value set.</summary>
    private const int DefaultDailyLimitMinutes = 120;

    /// <summary>Prefixes for single-day rows. On <see cref="Open"/>, rows not belonging to "today" get deleted so the table can't grow unbounded.</summary>
    private static readonly string[] DailyRowPrefixes =
    {
        "remaining_time_", "pause_used_", "pause_log_", "session_active_",
    };

    /// <summary>Seed values on first open (only for absent keys, so parent customisations never overwritten on later opens).</summary>
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
        // content filtering: "off" | "malware" | "family". chosen at setup
        ("dns_filter_mode", "off"),
        // block third-party DoH at firewall so browsers can't bypass filter
        ("block_doh_bypass", "1"),
        // Time Manipulation Guarding: correct clock from NTP before reset
        ("time_guard_enabled", "1"),
        // set once first-run setup wizard done
        ("setup_complete", "0"),
        // daily hours budget on/off (per-day limits). parent's choice
        ("limit_enabled", "1"),
        // weekly allowed-time schedule on/off. default off; absent = all allowed
        ("schedule_enabled", "0"),
    };

    private readonly DateOnly _today;

    private SettingsStore(SqliteConnection config, SqliteConnection state, DateOnly today)
    {
        _config = config;
        _state = state;
        _today = today;
    }

    /// <summary>Connection a key routes to.</summary>
    private SqliteConnection ConnectionFor(string key) =>
        SettingsPartition.StoreFor(key) == SettingsStoreKind.State ? _state : _config;

    /// <summary>Optional sink for CONFIG writes. Returns <c>true</c> = write handled (e.g. forwarded to SYSTEM service over config pipe), NOT written locally. <c>false</c>/null = direct write. State writes never use this. App sets it so config changes go through service once config.db write-protected; service + overlay leave null.</summary>
    public Func<string, string, bool>? ConfigWriter { get; set; }

    /// <summary>Windows user SID: per-user config keys (see <see cref="SettingsPartition.IsPerUser"/>) scoped to that user — read prefers user value, falls back to unscoped/global; write stores user value. Device-wide + state keys never scoped. Null = no scoping (legacy/global).</summary>
    public string? UserSid { get; set; }

    /// <summary>Effective stored key for a base key, applying per-user scoping.</summary>
    private string EffectiveKey(string key) =>
        UserSid is { Length: > 0 } sid && SettingsPartition.IsPerUser(key)
            ? SettingsPartition.Scope(key, sid)
            : key;

    /// <summary>Open (or create) db at <paramref name="databasePath"/>, seed missing defaults, purge per-day rows not in <paramref name="today"/>. Corrupt file gets deleted and recreated from defaults.</summary>
    /// <param name="databasePath">Absolute path to SQLite db file.</param>
    /// <param name="today">Current local date, decides which per-day rows to keep.</param>
    /// <param name="eventLogPath">Where corruption-recreate's <see cref="CurfewEventKind.StoreRecreated"/> event is appended; defaults to production log. Tests pass temp file to assert emission.</param>
    /// <returns>Open <see cref="SettingsStore"/>; never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="databasePath"/> is null, empty or whitespace.</exception>
    public static SettingsStore Open(string databasePath, DateOnly today, string? eventLogPath = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path must be provided.", nameof(databasePath));

        // single-file mode: one connection backs config + state. defaults seeded,
        // stale per-day rows purged on same file.
        var connection = OpenResilient(
            databasePath, c => Prepare(c, seedDefaults: true, today, purge: true), eventLogPath: eventLogPath);
        return new SettingsStore(connection, connection, today);
    }

    /// <summary>Open split stores: <paramref name="configPath"/> = write-protected policy/secrets, <paramref name="statePath"/> = child-writable per-day counters. First split open, if <paramref name="legacyPath"/> still holds a single-file db, its keys migrate into the right store.</summary>
    /// <param name="configPath">Path to config.db (defaults seeded here).</param>
    /// <param name="statePath">Path to state.db (per-day rows purged here).</param>
    /// <param name="legacyPath">Optional pre-split db to migrate from, or null.</param>
    /// <param name="today">Current local date, for per-day purge.</param>
    /// <param name="configWritable">True for SYSTEM service (creates/seeds/migrates config.db). False for app/overlay, which open config.db read-only (ACL'd read-only); they never migrate — service does that on boot.</param>
    /// <param name="eventLogPath">Where corruption-recreate's <see cref="CurfewEventKind.StoreRecreated"/> event is appended; defaults to production log. Tests pass temp file to assert recreating state.db (a child-triggerable counter reset) is traced.</param>
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

        // migrate when legacy single-file db still exists and config.db either
        // didn't exist yet or was merely bootstrapped (defaults only) by a
        // non-privileged process before service got its chance — bootstrap marker
        // distinguishes that from a config the parent has owned a while, which must
        // never be overwritten with stale legacy values.
        var migrate = configWritable && !string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath)
            && (!configExisted || GetDirect(config, BootstrapMarkerKey) == "1");

        if (migrate)
        {
            // best-effort: failed migration just leaves freshly-seeded defaults
            try { MigrateFromLegacy(legacyPath!, config, state); }
            catch (SqliteException) { /* corrupt legacy file — ignore */ }
        }

        // service owns config.db from here; bootstrap marker has served its purpose
        // either way (migrated, or no legacy data to migrate).
        if (configWritable)
        {
            try { Execute(config, $"DELETE FROM settings WHERE key = '{BootstrapMarkerKey}'"); }
            catch (SqliteException) { /* best effort */ }
        }

        return new SettingsStore(config, state, today);
    }

    /// <summary>Marker row set when a non-privileged process bootstraps config.db (see <see cref="OpenConfigReadOnly"/>), so service can tell legacy migration hasn't happened yet even though file exists.</summary>
    private const string BootstrapMarkerKey = "config_bootstrap";

    /// <summary>Journal mode for config.db. WAL would put committed config into child-writable <c>-wal</c>/<c>-shm</c> sidecars (deny-ACE covers only main file) and WAL readers need write access to <c>-shm</c>; PERSIST keeps one permanent <c>-journal</c> file the service can ACL alongside the db, read-only opens need no sidecar writes.</summary>
    private const string ConfigJournalMode = "PERSIST";

    /// <summary>Read one raw row off a specific connection, bypassing routing/scoping.</summary>
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

    /// <summary>Open a connection, run <paramref name="setup"/>, recreate file once if corrupt.</summary>
    /// <param name="eventLogPath">Destination for <see cref="CurfewEventKind.StoreRecreated"/> event on corruption-recreate; defaults to production log. Threaded through so tests redirect to temp file and assert emission.</param>
    private static SqliteConnection OpenResilient(
        string path, Action<SqliteConnection> setup, string journalMode = "WAL", string? eventLogPath = null)
    {
        try
        {
            return InitConnection(path, setup, journalMode: journalMode);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            // corrupt/truncated/not-SQLite: drop, recreate from defaults so controls
            // keep working. second failure propagates (it's the directory). NOT
            // corruption (BUSY/LOCKED contention, I/O error, permission) propagates
            // immediately — deleting a healthy db over a transient lock would destroy
            // parent's settings, including the passcode.
            TryDeleteDatabaseFiles(path);
            RecordStoreRecreated(path, eventLogPath);
            return InitConnection(path, setup, journalMode: journalMode);
        }
    }

    /// <summary>Whether a SQLite failure means the file itself isn't a usable db (vs transient lock, I/O problem, permission error).</summary>
    private static bool IsCorruption(SqliteException ex) =>
        ex.SqliteErrorCode is 11 or 26; // SQLITE_CORRUPT, SQLITE_NOTADB

    /// <summary>Leave a parent-visible event when a store had to be deleted + recreated: for state.db that wipes the day's counters to a fresh budget, which a child could provoke by corrupting the child-writable file.</summary>
    /// <param name="path">Db file recreated (its name is the event detail).</param>
    /// <param name="eventLogPath">Where to append, or <see langword="null"/> for production log (<see cref="CurfewPaths.EventLogFile"/>). Tests redirect to temp file to assert emission (only parent-facing trace of a child-triggerable state reset). Default-path lookup resolved INSIDE the try below: computing <see cref="CurfewPaths.EventLogFile"/> creates <c>%ProgramData%\Curfew</c> and can throw (reparse-point junction makes <see cref="CurfewPaths.DataDirectory"/> fail closed); recreating must still proceed.</param>
    private static void RecordStoreRecreated(string path, string? eventLogPath)
    {
        try
        {
            EventLog.Append(
                eventLogPath ?? CurfewPaths.EventLogFile, CurfewEventKind.StoreRecreated, Path.GetFileName(path));
        }
        catch
        {
            // diagnostics only; recreating must proceed regardless
        }
    }

    private static SqliteConnection InitConnection(
        string path, Action<SqliteConnection> setup, bool readOnly = false, string journalMode = "WAL")
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            // wait a few seconds for a lock instead of failing instantly when
            // another process (App/Overlay/Service) is mid-write.
            DefaultTimeout = 5,
        }.ToString());

        try
        {
            connection.Open();
            if (!readOnly)
            {
                // WAL lets readers + a writer proceed concurrently (several processes
                // share these files), forces a header check, surfaces corruption.
                // config.db uses PERSIST (see ConfigJournalMode). Changing mode needs
                // an exclusive lock, so a refusal while another process holds the file
                // is tolerated (next open retries) — but a corruption verdict must
                // still propagate so OpenResilient recreates.
                try { Execute(connection, $"PRAGMA journal_mode = {journalMode}"); }
                catch (SqliteException ex) when (!IsCorruption(ex)) { /* keep current mode */ }
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

    /// <summary>Open config.db read-only for a non-privileged process (app/overlay). Falls back to writable open + seed if file doesn't exist yet — before SYSTEM service created it, or before ACL applied. Keeps unlock working (direct read of passcode hash) even though app can no longer WRITE config once ACL is on.</summary>
    private static SqliteConnection OpenConfigReadOnly(string path, DateOnly today, string? eventLogPath = null)
    {
        if (File.Exists(path))
        {
            try { return InitConnection(path, static _ => { }, readOnly: true); }
            catch (SqliteException) { /* fall through, try to bootstrap it */ }
        }

        // mark bootstrap so service can still run legacy migration on next start:
        // a defaults-only file created here must not be mistaken for a config the
        // parent already owns (see OpenSplit's migrate decision).
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

    /// <summary>Copy every key from a pre-split single-file db into the right store.</summary>
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

        // overwrite seeded config defaults with parent's real values. one
        // transaction per target store so an interruption (power loss, thrown
        // exception mid-copy) leaves no half-migrated db — all of a store's rows
        // land or none do, next boot retries cleanly.
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
        // insert defaults only for absent keys so parent's saved settings
        // survive every subsequent open.
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
        // drop previous days' per-day rows so table can't grow forever. invariant
        // culture matches writers exactly: a non-Gregorian calendar region would
        // format a different "today" and purge live rows.
        var todaySuffix = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        // keep only rows whose key ends in today's date. suffix match (not
        // prefix+date exactly) keeps per-user keys with a SID between prefix and
        // date, e.g. remaining_time_<sid>_<date>.
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

    /// <summary>Best-effort removal of the db file + its WAL/SHM side files so a corrupt db can be fully recreated.</summary>
    private static void TryDeleteDatabaseFiles(string databasePath)
    {
        // release pooled connections first to let Windows unlock the file
        try { SqliteConnection.ClearAllPools(); } catch { /* best effort */ }

        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(databasePath + suffix); }
            catch { /* best effort: leftover side file is harmless */ }
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Raw stored value for <paramref name="key"/>, or <see langword="null"/> if unset.</summary>
    public string? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var effective = EffectiveKey(key);
        var value = GetRaw(effective);
        // per-user read falls back to unscoped/global value when user has no
        // override, so existing (global) settings keep applying to every user.
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

    /// <summary>Store <paramref name="value"/> under <paramref name="key"/>, replacing any existing.</summary>
    public void Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var effective = EffectiveKey(key);

        // route config writes through config writer (SYSTEM service) when set and it
        // accepts the write; else fall through to direct write.
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

    /// <summary>Read <paramref name="key"/> as integer; <paramref name="fallback"/> when missing or not a valid integer.</summary>
    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), out var v) ? v : fallback;

    /// <summary>Read <paramref name="key"/> as bool flag; <paramref name="fallback"/> when missing. Stored <c>"1"</c> is <see langword="true"/>; any other stored value is <see langword="false"/>.</summary>
    public bool GetBool(string key, bool fallback)
    {
        var raw = Get(key);
        return raw is null ? fallback : raw == "1";
    }

    /// <summary>Configured daily limit, minutes, for weekday where <paramref name="weekday"/> is 0 = Monday … 6 = Sunday. Out-of-range indexes + unparseable values fall back to default daily limit.</summary>
    public int GetDailyLimit(int weekday) =>
        weekday is >= 0 and < 7
            ? GetInt(WeekdayKeys[weekday], DefaultDailyLimitMinutes)
            : DefaultDailyLimitMinutes;

    /// <summary>Whether a parental passcode is set.</summary>
    public bool HasPasscode => !string.IsNullOrEmpty(Get("passcode"));

    /// <summary>Settings-key prefix for per-day recorded active screen time (seconds).</summary>
    public const string UsagePrefix = "used_time_";

    /// <summary>One day's recorded screen time.</summary>
    public readonly record struct UsageDay(DateOnly Date, int Minutes);

    /// <summary>Active screen-time per day for last <paramref name="days"/> days, oldest first, ending on the day the store opened. Recordless days report zero. Exempt from per-day purge so history accumulates.</summary>
    /// <remarks>
    /// Overlay writes usage as <c>used_time_&lt;sid&gt;_&lt;date&gt;</c>. With <see cref="UserSid"/> set this reads that user's rows; else sums all users for the day (covering legacy <c>used_time_&lt;date&gt;</c> rows from before per-user counters existed).
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

    /// <summary>Sum one day's usage rows across every user (and the legacy unscoped row).</summary>
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
            // read failure reports zero for the day; chart is advisory
        }
        return total;
    }

    /// <summary>Distinct Windows-user SIDs with recorded usage, from <c>used_time_&lt;sid&gt;_&lt;date&gt;</c> keys in state.db. Populates settings per-user picker (each Windows user keeps own budget; no provisioned-user list). Legacy unscoped <c>used_time_&lt;date&gt;</c> rows have no SID, skipped. Order unspecified.</summary>
    public IReadOnlyList<string> UsersWithHistory()
    {
        var sids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = _state.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT key FROM settings WHERE key LIKE $p || '%'";
            cmd.Parameters.AddWithValue("$p", UsagePrefix);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var rest = reader.GetString(0)[UsagePrefix.Length..];
                // key is used_time_<sid>_<yyyy-MM-dd>; SID has no underscores, date
                // uses hyphens, so last underscore splits SID from date.
                var cut = rest.LastIndexOf('_');
                if (cut <= 0) continue; // legacy unscoped used_time_<date>
                var sid = rest[..cut];
                if (sid.Length > 0 && seen.Add(sid)) sids.Add(sid);
            }
        }
        catch (SqliteException)
        {
            // advisory list; empty result means picker shows "All users" only
        }
        return sids;
    }

    /// <summary>Whether <paramref name="sid"/> has any recorded usage (a <c>used_time_&lt;sid&gt;_&lt;date&gt;</c> row). Grandfathers users already on the device before the new-user setup gate existed so an upgrade doesn't lock everyone out — only genuinely new users (no history, not set up) are gated. Read failure reports false (treat as new).</summary>
    public bool HasUsageHistory(string? sid)
    {
        if (string.IsNullOrEmpty(sid)) return false;
        try
        {
            using var cmd = _state.CreateCommand();
            // ONLY this user's scoped rows (used_time_<sid>_<date>). NOT legacy
            // unscoped used_time_<date> aggregate rows: no SID, so matching them
            // would grandfather EVERY user (incl a new one) on any device with a
            // legacy row — defeating the new-user gate. GLOB (not LIKE) so
            // underscores in key/SID are literal, '*' the wildcard.
            cmd.CommandText = "SELECT 1 FROM settings WHERE key GLOB $g LIMIT 1";
            cmd.Parameters.AddWithValue("$g", $"{UsagePrefix}{sid}_*");
            return cmd.ExecuteScalar() is not null;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>Close underlying db connection(s).</summary>
    public void Dispose()
    {
        _config.Dispose();
        if (!ReferenceEquals(_state, _config)) _state.Dispose();
    }
}

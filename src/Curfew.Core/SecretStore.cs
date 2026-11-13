using Microsoft.Data.Sqlite;

namespace Curfew.Core;

/// <summary>Service-private credential store (<c>%ProgramData%\Curfew\secure\secrets.db</c>): parent passcode
/// hash, offline-unlock seed + replay counter, brute-force lockout counters. The directory is ACL'd to SYSTEM +
/// Administrators only, so - unlike config.db - ordinary users can't even read it. Clients never touch it; they
/// ask the service to verify over the config pipe.</summary>
public sealed class SecretStore : IDisposable
{
    private readonly SqliteConnection _db;

    public SecretStore(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
        }.ToString());
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode = PERSIST; CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
        cmd.ExecuteNonQuery();
    }

    public string? Get(string key)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Move every <see cref="SettingsPartition.SecretKeys"/> value out of user-readable config.db into
    /// this store (config value wins only when this store lacks the key), hashing any legacy plaintext passcode,
    /// then delete it from config.db and publish the non-secret <c>passcode_set</c> flag.</summary>
    public void MigrateFrom(SettingsStore config)
    {
        foreach (var key in SettingsPartition.SecretKeys)
        {
            var value = config.Get(key);
            if (value is null) continue;
            if (Get(key) is null && value.Length > 0) Set(key, value);
            config.Delete(key);
        }

        var passcode = Get("passcode");
        if (!string.IsNullOrEmpty(passcode) && !Security.PasscodeHash.IsHashed(passcode))
            Set("passcode", Security.PasscodeHash.Hash(passcode));

        var flag = string.IsNullOrEmpty(Get("passcode")) ? "0" : "1";
        if (config.Get("passcode_set") != flag) config.Set("passcode_set", flag);
    }

    public void Dispose() => _db.Dispose();
}

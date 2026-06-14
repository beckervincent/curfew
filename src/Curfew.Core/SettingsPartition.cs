namespace Curfew.Core;

/// <summary>Which physical store a settings key lives in.</summary>
public enum SettingsStoreKind
{
    /// <summary>Write-protected policy + secrets (Users read, only service writes).</summary>
    Config,

    /// <summary>Child-writable per-day counters + runtime lock coordination.</summary>
    State,
}

/// <summary>Source of truth for splitting keys between write-protected <c>config.db</c> and child-writable <c>state.db</c>, and per-SID vs device-wide.</summary>
/// <remarks>
/// Pure, dependency-free; shared by store, overlay, app. Foundation of C1 write-isolation: child can't rewrite <see cref="SettingsStoreKind.Config"/>, but per-day counters in <see cref="SettingsStoreKind.State"/> stay child-writable so overlay countdown runs without elevation.
/// </remarks>
public static class SettingsPartition
{
    /// <summary>Prefixes in child-writable state store: per-day budget/usage counters, pause accounting, runtime lock-coordination keys. Else config.</summary>
    private static readonly string[] StatePrefixes =
    {
        "remaining_time_", "used_time_", "pause_used_", "pause_log_", "session_active_",
        // child-side runtime coordination (not policy): lock handshake (exact keys
        // below, NOT broad "lock_" prefix — that catches policy keys like
        // lock_screen_timeout, giving child write access), tray command, offline-code
        // replay counter.
        "lock_active", "lock_reason", "lock_deadline_unix", "lock_action",
        "lock_action_at", "lock_sid", "lock_code", "lock_setup_limit",
        "tray_", "unlock_last_counter",
    };

    /// <summary>Device-wide config keys (not per-user): passcode, activation code, provisioned-user list, app allow-list, schema version, failed-attempt counters, update prefs. Else per-user.</summary>
    private static readonly HashSet<string> GlobalConfigKeys = new(StringComparer.Ordinal)
    {
        "passcode", "provisioned_users", "app_allowlist",
        "schema_version", "auto_update_enabled", "update_channel",
        "failed_attempts", "failed_attempt_at",
    };

    /// <summary>Store a (fully-formed) key belongs to.</summary>
    public static SettingsStoreKind StoreFor(string key)
    {
        foreach (var prefix in StatePrefixes)
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return SettingsStoreKind.State;
        return SettingsStoreKind.Config;
    }

    /// <summary>Whether a config base key is per-user. Device-wide + any state key are not.</summary>
    public static bool IsPerUser(string baseKey) =>
        StoreFor(baseKey) == SettingsStoreKind.Config && !GlobalConfigKeys.Contains(baseKey);

    /// <summary>Scope a per-user config base key to <paramref name="sid"/>; device-wide + state keys pass through. Blank SID returns base key as-is.</summary>
    public static string Scope(string baseKey, string? sid) =>
        IsPerUser(baseKey) && !string.IsNullOrEmpty(sid) ? $"u:{sid}:{baseKey}" : baseKey;
}

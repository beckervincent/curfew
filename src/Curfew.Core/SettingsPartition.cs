namespace Curfew.Core;

/// <summary>Which physical store a settings key belongs to.</summary>
public enum SettingsStoreKind
{
    /// <summary>Write-protected policy + secrets (Users may read, only the service writes).</summary>
    Config,

    /// <summary>Child-writable per-day counters and runtime lock coordination.</summary>
    State,
}

/// <summary>
/// The single source of truth for how settings keys are split between the
/// write-protected <c>config.db</c> and the child-writable <c>state.db</c>, and
/// which keys are scoped per Windows user (SID) versus device-wide.
/// </summary>
/// <remarks>
/// Pure and dependency-free so it can be unit-tested and shared by the store, the
/// overlay and the app. The split is the foundation of the C1 write-isolation: a
/// child cannot rewrite anything in <see cref="SettingsStoreKind.Config"/>, but
/// the volatile per-day counters in <see cref="SettingsStoreKind.State"/> stay
/// child-writable so the overlay's countdown keeps working without elevation.
/// </remarks>
public static class SettingsPartition
{
    /// <summary>
    /// Key prefixes that live in the child-writable state store: per-day budget and
    /// usage counters, pause accounting, and the runtime lock-coordination keys the
    /// overlay and lock app exchange. Everything else is config.
    /// </summary>
    private static readonly string[] StatePrefixes =
    {
        "remaining_time_", "used_time_", "pause_used_", "pause_log_", "session_active_",
        // Runtime coordination written by the child-side processes (not policy):
        // the lock handshake (exact keys below, NOT a broad "lock_" prefix — that
        // would also catch policy keys like lock_screen_timeout and hand the child
        // write access to them), the tray command, and the offline-code replay
        // counter.
        "lock_active", "lock_reason", "lock_deadline_unix", "lock_action",
        "lock_action_at", "lock_sid", "lock_code",
        "tray_", "unlock_last_counter",
    };

    /// <summary>
    /// Config keys that are device-wide rather than per-user: the parent passcode,
    /// the device activation code, the provisioned-user list, the app allow-list,
    /// the schema version, the failed-attempt lockout counters, and the update
    /// preferences. Every other config key is per-user.
    /// </summary>
    private static readonly HashSet<string> GlobalConfigKeys = new(StringComparer.Ordinal)
    {
        "passcode", "device_code", "provisioned_users", "app_allowlist",
        "schema_version", "auto_update_enabled", "update_channel",
        "failed_attempts", "failed_attempt_at",
    };

    /// <summary>Returns the store a (fully-formed) key belongs to.</summary>
    public static SettingsStoreKind StoreFor(string key)
    {
        foreach (var prefix in StatePrefixes)
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return SettingsStoreKind.State;
        return SettingsStoreKind.Config;
    }

    /// <summary>
    /// Whether a config base key is scoped to a single user. Device-wide keys and
    /// any state key are not per-user.
    /// </summary>
    public static bool IsPerUser(string baseKey) =>
        StoreFor(baseKey) == SettingsStoreKind.Config && !GlobalConfigKeys.Contains(baseKey);

    /// <summary>
    /// Scopes a per-user config base key to <paramref name="sid"/>; device-wide
    /// keys (and state keys, which embed their own user/date) pass through
    /// unchanged. With a blank SID the base key is returned as-is.
    /// </summary>
    public static string Scope(string baseKey, string? sid) =>
        IsPerUser(baseKey) && !string.IsNullOrEmpty(sid) ? $"u:{sid}:{baseKey}" : baseKey;
}

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
        "remaining_time_", "used_time_", "pause_used_", "pause_log_", "pause_last_end_", "session_active_",
        // child-side runtime coordination (not policy): lock handshake (exact keys
        // below, NOT broad "lock_" prefix — that catches policy keys like
        // lock_screen_timeout, giving child write access), tray command, offline-code
        // replay counter.
        "lock_active", "lock_reason", "lock_deadline_unix", "lock_action",
        "lock_action_at", "lock_sid", "lock_code", "lock_setup_limit", "lock_break_minutes",
        "tray_", "unlock_last_counter",
    };

    /// <summary>Device-wide config keys (not per-user): passcode, provisioned-user list, app allow-list,
    /// schema version, failed-attempt counters, update prefs, the machine-wide enforcement settings, and
    /// the offline unlock code. Else per-user.
    /// <para>dns_filter_mode / block_doh_bypass / time_guard_enabled MUST be global: the SYSTEM service
    /// applies them once for the whole machine (Cloudflare DNS on every adapter, DoH firewall rules,
    /// system-clock guard) and reads them with no UserSid.</para>
    /// <para>unlock_secret / unlock_bonus_minutes are ONE device unlock code (the QR is labelled
    /// "Curfew:Device" and the parent enrols a single authenticator entry). Were they per-user the secret
    /// would differ between where it is seeded (first-run setup, no UserSid -> global), where it is shown
    /// (Settings, scoped to the picked user), and where it is verified (lock/overlay, scoped to the session
    /// user) — so the enrolled code matched nothing and redemption always failed. Global keeps all four
    /// sites on the same secret. The replay counter (unlock_last_counter) is already device-wide state.</para></summary>
    private static readonly HashSet<string> GlobalConfigKeys = new(StringComparer.Ordinal)
    {
        "passcode", "provisioned_users", "app_allowlist",
        "schema_version", "auto_update_enabled", "update_channel",
        "failed_attempts", "failed_attempt_at",
        "dns_filter_mode", "block_doh_bypass", "time_guard_enabled",
        "unlock_secret", "unlock_bonus_minutes",
        // custom hosts-file blocklist + enforced SafeSearch: applied machine-wide by the SYSTEM service
        "blocked_domains", "safesearch_enabled", "blocked_categories",
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

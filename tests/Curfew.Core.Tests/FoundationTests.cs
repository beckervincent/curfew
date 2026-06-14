using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class SettingsPartitionTests
{
    [Theory]
    [InlineData("remaining_time_2026-06-11", SettingsStoreKind.State)]
    [InlineData("used_time_2026-06-11", SettingsStoreKind.State)]
    [InlineData("pause_used_2026-06-11", SettingsStoreKind.State)]
    [InlineData("lock_active", SettingsStoreKind.State)]
    [InlineData("lock_action", SettingsStoreKind.State)]
    [InlineData("lock_action_at", SettingsStoreKind.State)]
    [InlineData("lock_reason", SettingsStoreKind.State)]
    [InlineData("lock_deadline_unix", SettingsStoreKind.State)]
    [InlineData("lock_sid", SettingsStoreKind.State)]
    [InlineData("lock_code", SettingsStoreKind.State)]
    [InlineData("lock_setup_limit", SettingsStoreKind.State)]
    [InlineData("tray_command", SettingsStoreKind.State)]
    [InlineData("unlock_last_counter", SettingsStoreKind.State)]
    [InlineData("passcode", SettingsStoreKind.Config)]
    [InlineData("provisioned_users", SettingsStoreKind.Config)]
    [InlineData("schedule", SettingsStoreKind.Config)]
    [InlineData("limit_enabled", SettingsStoreKind.Config)]
    // Policy, NOT runtime handshake: a broad "lock_" prefix once routed this into
    // the child-writable state store, letting the child set their own logoff delay.
    [InlineData("lock_screen_timeout", SettingsStoreKind.Config)]
    public void StoreFor_routes_counters_to_state_and_policy_to_config(string key, SettingsStoreKind expected) =>
        Assert.Equal(expected, SettingsPartition.StoreFor(key));

    [Theory]
    [InlineData("limit_enabled", true)]
    [InlineData("schedule", true)]
    [InlineData("unlock_secret", true)]
    [InlineData("passcode", false)]        // device-wide
    [InlineData("provisioned_users", false)] // device-wide set-up list
    [InlineData("app_allowlist", false)]
    [InlineData("auto_update_enabled", false)]
    [InlineData("lock_active", false)]     // state, not per-user config
    public void IsPerUser_only_for_per_child_policy(string key, bool expected) =>
        Assert.Equal(expected, SettingsPartition.IsPerUser(key));

    [Fact]
    public void Scope_prefixes_per_user_keys_and_passes_global_through()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        Assert.Equal($"u:{sid}:limit_enabled", SettingsPartition.Scope("limit_enabled", sid));
        Assert.Equal("passcode", SettingsPartition.Scope("passcode", sid));       // global
        Assert.Equal("limit_enabled", SettingsPartition.Scope("limit_enabled", "")); // no sid
    }
}

public class UserProvisioningTests
{
    [Fact]
    public void Add_is_idempotent_and_membership_is_case_insensitive()
    {
        var list = UserProvisioning.Add(null, "S-1-5-21-1");
        list = UserProvisioning.Add(list, "S-1-5-21-2");
        list = UserProvisioning.Add(list, "S-1-5-21-1"); // duplicate

        Assert.Equal(2, UserProvisioning.Parse(list).Count);
        Assert.True(UserProvisioning.IsProvisioned(list, "s-1-5-21-2"));
        Assert.False(UserProvisioning.IsProvisioned(list, "S-1-5-21-9"));
        Assert.False(UserProvisioning.IsProvisioned(list, null));
    }
}

public class LockoutPolicyTests
{
    [Fact]
    public void No_backoff_within_free_attempts()
    {
        for (var i = 0; i <= LockoutPolicy.FreeAttempts; i++)
            Assert.Equal(0, LockoutPolicy.BackoffSeconds(i));
    }

    [Fact]
    public void Backoff_grows_and_caps()
    {
        Assert.Equal(LockoutPolicy.BaseBackoffSeconds, LockoutPolicy.BackoffSeconds(LockoutPolicy.FreeAttempts + 1));
        Assert.True(LockoutPolicy.BackoffSeconds(LockoutPolicy.FreeAttempts + 2) > LockoutPolicy.BaseBackoffSeconds);
        Assert.Equal(LockoutPolicy.MaxBackoffSeconds, LockoutPolicy.BackoffSeconds(100));
    }

    [Fact]
    public void IsLockedOut_blocks_until_backoff_elapses()
    {
        var state = new LockoutState(LockoutPolicy.FreeAttempts + 1, 1000);
        Assert.True(LockoutPolicy.IsLockedOut(state, 1001, out var wait));
        Assert.True(wait > 0);
        Assert.False(LockoutPolicy.IsLockedOut(state, 1000 + LockoutPolicy.BaseBackoffSeconds, out _));
    }

    [Fact]
    public void Backwards_clock_keeps_blocking()
    {
        var state = new LockoutState(LockoutPolicy.FreeAttempts + 3, 10_000);
        // Clock rolled back to before the attempt — must still be locked (fail closed).
        Assert.True(LockoutPolicy.IsLockedOut(state, 9_000, out _));
    }
}

public class AppAllowlistTests
{
    [Fact]
    public void Parse_normalizes_names_and_strips_exe_and_path()
    {
        var set = AppAllowlist.Parse("Code.exe; C:\\Program Files\\Word\\WINWORD.EXE\n  notepad  ");
        Assert.True(AppAllowlist.Allows(set, "code"));
        Assert.True(AppAllowlist.Allows(set, "CODE.EXE"));
        Assert.True(AppAllowlist.Allows(set, @"D:\apps\winword.exe"));
        Assert.True(AppAllowlist.Allows(set, "notepad.exe"));
        Assert.False(AppAllowlist.Allows(set, "chrome.exe"));
    }

    [Fact]
    public void Empty_list_allows_nothing()
    {
        var set = AppAllowlist.Parse("");
        Assert.False(AppAllowlist.Allows(set, "code.exe"));
    }

    [Fact]
    public void Serialize_round_trips_distinct_normalized_names()
    {
        var text = AppAllowlist.Serialize(new[] { "Code.exe", "code", "Word.exe" });
        var set = AppAllowlist.Parse(text);
        Assert.Equal(2, set.Count);
        Assert.True(AppAllowlist.Allows(set, "code"));
        Assert.True(AppAllowlist.Allows(set, "word"));
    }

    [Fact]
    public void AllowsTrusted_requires_the_image_to_live_under_a_trusted_root()
    {
        var set = AppAllowlist.Parse("code.exe");
        var sep = System.IO.Path.DirectorySeparatorChar;
        var roots = new[] { $"{sep}trusted{sep}Program Files", $"{sep}trusted{sep}Windows" };

        // Allow-listed name in a trusted root: exempt.
        Assert.True(AppAllowlist.AllowsTrusted(set, $"{sep}trusted{sep}Program Files{sep}VSCode{sep}code.exe", roots));

        // Same name copied to a child-writable location: NOT exempt — otherwise
        // renaming any exe to an allow-listed name stops the budget clock forever.
        Assert.False(AppAllowlist.AllowsTrusted(set, $"{sep}users{sep}kid{sep}code.exe", roots));

        // Sibling directory whose name merely *starts with* the trusted root's text:
        // NOT exempt. AllowsTrusted appends a separator to each root before StartsWith
        // precisely so "Program FilesEvil" (a folder the child could create) is not
        // treated as inside "Program Files". Drop that separator-append and this path
        // wrongly becomes exempt, stopping the budget clock forever.
        Assert.False(AppAllowlist.AllowsTrusted(set, $"{sep}trusted{sep}Program FilesEvil{sep}code.exe", roots));

        // Non-listed name in a trusted root: not exempt either.
        Assert.False(AppAllowlist.AllowsTrusted(set, $"{sep}trusted{sep}Program Files{sep}chrome.exe", roots));

        // Unknown path (elevated process, exited process): fail closed.
        Assert.False(AppAllowlist.AllowsTrusted(set, null, roots));
        Assert.False(AppAllowlist.AllowsTrusted(set, "", roots));
    }
}

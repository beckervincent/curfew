namespace Curfew.Core;

/// <summary>
/// Parse/format helpers for the set of set-up Windows users (the SIDs the parent
/// has set up on this device via the new-user setup lock). Stored as a
/// semicolon-separated list in the device-wide config key <c>provisioned_users</c>.
/// A user not in this set is blocked on login until the parent sets their limit.
/// Pure and unit-tested.
/// </summary>
public static class UserProvisioning
{
    /// <summary>The set-up SIDs from the stored list (order preserved, no blanks).</summary>
    public static IReadOnlyList<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return Array.Empty<string>();
        return stored
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Whether <paramref name="sid"/> has been set up.</summary>
    public static bool IsProvisioned(string? stored, string? sid) =>
        !string.IsNullOrEmpty(sid) && Parse(stored).Contains(sid, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the list with <paramref name="sid"/> added (idempotent).</summary>
    public static string Add(string? stored, string sid)
    {
        if (string.IsNullOrEmpty(sid)) return stored ?? string.Empty;
        var users = Parse(stored).ToList();
        if (!users.Contains(sid, StringComparer.OrdinalIgnoreCase)) users.Add(sid);
        return string.Join(';', users);
    }
}

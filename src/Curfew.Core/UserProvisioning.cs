namespace Curfew.Core;

/// <summary>
/// Parse/format helpers for the set of provisioned Windows users (the SIDs the
/// parent has activated on this device). Stored as a semicolon-separated list in
/// the device-wide config key <c>provisioned_users</c>. Pure and unit-tested.
/// </summary>
public static class UserProvisioning
{
    /// <summary>The provisioned SIDs from the stored list (order preserved, no blanks).</summary>
    public static IReadOnlyList<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return Array.Empty<string>();
        return stored
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Whether <paramref name="sid"/> is in the provisioned set.</summary>
    public static bool IsProvisioned(string? stored, string? sid) =>
        !string.IsNullOrEmpty(sid) && Parse(stored).Contains(sid, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the list with <paramref name="sid"/> added (idempotent).</summary>
    public static string Add(string? stored, string sid)
    {
        var users = Parse(stored).ToList();
        if (!users.Contains(sid, StringComparer.OrdinalIgnoreCase)) users.Add(sid);
        return string.Join(';', users);
    }
}

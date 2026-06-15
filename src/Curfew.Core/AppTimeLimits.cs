namespace Curfew.Core;

/// <summary>
/// Per-app daily time limits: each named app gets its own minutes-per-day budget,
/// independent of the global daily budget. When an app's tracked foreground time for
/// the day reaches its limit the overlay blocks it (same terminate-foreground path as
/// the app blocklist). Pure parse/match so the overlay's per-second tick can cheaply
/// ask "is this foreground app over its limit?".
/// </summary>
/// <remarks>
/// Stored as newline-/comma-/semicolon-separated <c>name=minutes</c> entries
/// (<c>minecraft=60, chrome.exe = 30</c>). Names normalize exactly like
/// <see cref="AppAllowlist"/> (case-insensitive, optional <c>.exe</c>, path stripped) so
/// the same process matches. Minutes clamp to 0..1440; an entry without a valid
/// positive name or a parseable minute count is dropped rather than failing the whole list.
/// A limit of 0 means "no time at all" (blocked immediately) — still a valid policy.
/// </remarks>
public static class AppTimeLimits
{
    private const int MaxMinutes = 24 * 60;

    /// <summary>Parse the stored list into a normalized <c>name -> minutes</c> map. Last entry wins on duplicates.</summary>
    public static IReadOnlyDictionary<string, int> Parse(string? stored)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(stored)) return map;

        foreach (var raw in stored.Split(new[] { '\n', '\r', ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue; // need a name and an '='

            var name = AppAllowlist.Normalize(raw[..eq]);
            if (name.Length == 0) continue;

            if (!int.TryParse(raw[(eq + 1)..].Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var minutes))
                continue;

            map[name] = Math.Clamp(minutes, 0, MaxMinutes);
        }
        return map;
    }

    /// <summary>Serialize a <c>name -> minutes</c> map back to the stored <c>name=minutes</c> newline form.</summary>
    public static string Serialize(IReadOnlyDictionary<string, int> limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return string.Join('\n', limits
            .Select(kv => (Name: AppAllowlist.Normalize(kv.Key), kv.Value))
            .Where(e => e.Name.Length > 0)
            .Select(e => $"{e.Name}={Math.Clamp(e.Value, 0, MaxMinutes)}"));
    }

    /// <summary>Daily limit in minutes for <paramref name="processName"/> (image name or full path), or
    /// <c>-1</c> when the app has no limit set. A configured limit of 0 returns 0 (blocked outright).</summary>
    public static int LimitMinutesFor(IReadOnlyDictionary<string, int> limits, string? processName)
    {
        if (limits.Count == 0 || string.IsNullOrWhiteSpace(processName)) return -1;
        return limits.TryGetValue(AppAllowlist.Normalize(processName), out var minutes) ? minutes : -1;
    }
}

namespace Curfew.Core;

/// <summary>
/// Compact serialization of a per-app usage counter map (<c>name -> seconds</c>) for a
/// single day, stored as one child-writable <c>state.db</c> row. Keeping the whole day's
/// per-app totals in one value (rather than a key per app) bounds the number of state
/// keys the overlay writes and keeps the midnight purge simple.
/// </summary>
/// <remarks>
/// Stored as comma-separated <c>name=seconds</c>. Names are already normalized
/// (<see cref="AppAllowlist.Normalize"/>) before they reach here; parse is tolerant of a
/// malformed or partially-written row (drops bad entries, never throws) so a torn write
/// can't crash the overlay — at worst a single app's count resets.
/// </remarks>
public static class AppUsageMap
{
    /// <summary>Parse a stored row into a mutable <c>name -> seconds</c> map. Bad entries are skipped.</summary>
    public static Dictionary<string, int> Parse(string? stored)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(stored)) return map;

        foreach (var raw in stored.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue;

            var name = raw[..eq].Trim();
            if (name.Length == 0) continue;
            if (!int.TryParse(raw[(eq + 1)..].Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                continue;

            map[name] = Math.Max(0, seconds);
        }
        return map;
    }

    /// <summary>Serialize a <c>name -> seconds</c> map to the compact stored form, in a stable (sorted) order.</summary>
    public static string Serialize(IReadOnlyDictionary<string, int> usage) =>
        string.Join(',', usage
            .Where(kv => kv.Key.Length > 0 && kv.Value > 0)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));
}

namespace Curfew.Core;

/// <summary>
/// Aggregation helpers over the per-day per-app usage rows (each a serialized
/// <see cref="AppUsageMap"/>) so the parent can see where screen time went. Pure — the
/// store reads the day rows, these merge and rank them.
/// </summary>
public static class AppUsageStats
{
    /// <summary>Merge several day rows into one <c>name -> seconds</c> total. Null/blank rows are ignored.</summary>
    public static IReadOnlyDictionary<string, int> Merge(IEnumerable<string?> dayRows)
    {
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in dayRows)
        {
            foreach (var (name, seconds) in AppUsageMap.Parse(row))
            {
                totals.TryGetValue(name, out var running);
                totals[name] = running + seconds;
            }
        }
        return totals;
    }

    /// <summary>One app's share of recorded time.</summary>
    public readonly record struct AppTime(string Name, int Seconds);

    /// <summary>Top <paramref name="count"/> apps by time, descending (ties broken by name for stable output).
    /// Zero/negative counts are dropped. <paramref name="count"/> &lt;= 0 returns empty.</summary>
    public static IReadOnlyList<AppTime> Top(IReadOnlyDictionary<string, int> totals, int count)
    {
        if (count <= 0) return Array.Empty<AppTime>();
        return totals
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(count)
            .Select(kv => new AppTime(kv.Key, kv.Value))
            .ToList();
    }
}

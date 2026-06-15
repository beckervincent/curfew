namespace Curfew.Core;

/// <summary>allow-list of apps whose foreground time doesn't count against daily budget (homework apps, IDE). pure parse/match so overlay's per-second tick cheaply asks "does foreground app pause clock?"</summary>
/// <remarks>stored newline- or semicolon-separated process image names (<c>code.exe</c>, <c>winword</c>). case-insensitive, tolerant of present/absent <c>.exe</c> and a full path</remarks>
public static class AppAllowlist
{
    /// <summary>parse stored list into normalized set of image names (no <c>.exe</c>)</summary>
    public static IReadOnlySet<string> Parse(string? stored)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(stored)) return set;

        foreach (var raw in stored.Split(new[] { '\n', '\r', ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Normalize(raw);
            if (name.Length > 0) set.Add(name);
        }
        return set;
    }

    /// <summary>serialize names back to stored newline-separated form</summary>
    public static string Serialize(IEnumerable<string> names) =>
        string.Join('\n', names.Select(Normalize).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>whether <paramref name="processName"/> (image name or full path) is allow-listed — foreground time should NOT consume budget</summary>
    public static bool Allows(IReadOnlySet<string> allow, string? processName)
    {
        if (allow.Count == 0 || string.IsNullOrWhiteSpace(processName)) return false;
        return allow.Contains(Normalize(processName));
    }

    /// <summary>whether <paramref name="imagePath"/> is allow-listed AND under one of <paramref name="trustedRoots"/> (Program Files / Windows). name-only match would let child copy any exe to a writable folder under an allow-listed name and stop budget forever; trusted roots need admin to write, which child lacks. null/unknown path NOT exempt (fail closed)</summary>
    public static bool AllowsTrusted(
        IReadOnlySet<string> allow, string? imagePath, IReadOnlyList<string> trustedRoots)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return false;
        if (!Allows(allow, imagePath)) return false;

        foreach (var root in trustedRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (imagePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>lower-case, strip directory and trailing <c>.exe</c>, trim. shared so other app-name
    /// features (per-app time limits, usage stats) normalize identically and match the same process</summary>
    public static string Normalize(string value)
    {
        var name = value.Trim();
        var slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        // lower-case so matching never depends on the dictionary/set comparer alone:
        // serialized keys (per-app usage, limits) compare consistently regardless of source casing
        return name.Trim().ToLowerInvariant();
    }
}

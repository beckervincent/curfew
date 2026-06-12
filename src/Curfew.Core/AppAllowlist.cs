namespace Curfew.Core;

/// <summary>
/// The allow-list of applications whose foreground time does not count against the
/// daily budget (homework apps, an IDE, …). Pure parsing/matching so the overlay's
/// per-second tick can ask "does the foreground app pause the clock?" cheaply, and
/// the rule is unit-tested.
/// </summary>
/// <remarks>
/// Stored as a newline- or semicolon-separated list of process image names
/// (<c>code.exe</c>, <c>winword</c>, …). Matching is case-insensitive and tolerant
/// of a present/absent <c>.exe</c> suffix and of a full path being passed in.
/// </remarks>
public static class AppAllowlist
{
    /// <summary>Parses the stored list into a normalized set of image names (no <c>.exe</c>).</summary>
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

    /// <summary>Serializes a set of names back to the stored newline-separated form.</summary>
    public static string Serialize(IEnumerable<string> names) =>
        string.Join('\n', names.Select(Normalize).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Whether <paramref name="processName"/> (an image name or full path) is on
    /// the allow-list — i.e. its foreground time should NOT consume the budget.
    /// </summary>
    public static bool Allows(IReadOnlySet<string> allow, string? processName)
    {
        if (allow.Count == 0 || string.IsNullOrWhiteSpace(processName)) return false;
        return allow.Contains(Normalize(processName));
    }

    /// <summary>
    /// Whether the process at <paramref name="imagePath"/> is allow-listed AND runs
    /// from one of <paramref name="trustedRoots"/> (e.g. Program Files / Windows).
    /// Matching on the image name alone would let the child copy any executable to
    /// a writable folder under an allow-listed name and stop the budget forever;
    /// the trusted roots require admin rights to write to, which the child lacks.
    /// A null/unknown path is NOT exempt (fail closed).
    /// </summary>
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

    /// <summary>Lower-cases, strips any directory and a trailing <c>.exe</c>, trims.</summary>
    private static string Normalize(string value)
    {
        var name = value.Trim();
        var slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Trim();
    }
}

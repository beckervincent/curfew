namespace Curfew.Core;

/// <summary>
/// Parent-specified domain blocklist enforced through the system hosts file, so a
/// child cannot reach named sites/games regardless of the DNS content filter. Pure
/// parsing + hosts-section rendering live here (testable); the SYSTEM service does
/// the privileged hosts-file write. The hosts file takes precedence over DNS, so
/// this complements the Cloudflare category filter rather than competing with it.
/// </summary>
/// <remarks>
/// All Curfew entries live between two marker lines so the section can be replaced
/// or removed without touching anything else in the file. Idempotent: rendering the
/// same domains twice yields the same section, and merging is safe to repeat.
/// </remarks>
public static class HostsBlocklist
{
    /// <summary>Opening marker of the Curfew-managed section.</summary>
    public const string BeginMarker = "# BEGIN Curfew blocklist - do not edit";

    /// <summary>Closing marker of the Curfew-managed section.</summary>
    public const string EndMarker = "# END Curfew blocklist";

    /// <summary>Sink address for blocked names. 0.0.0.0 is unroutable and never hits a local service (unlike 127.0.0.1).</summary>
    private const string Sink = "0.0.0.0";

    private static readonly char[] Separators = { '\n', '\r', ',', ';', ' ', '\t' };

    /// <summary>
    /// Parse a free-form blocklist (newline/comma/space separated) into normalized,
    /// de-duplicated bare domains. Strips a scheme, any path/query, and a leading
    /// "www."; lowercases; drops anything without a dot or with illegal characters.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var domain = Normalize(raw);
            if (domain is not null && seen.Add(domain)) result.Add(domain);
        }
        return result;
    }

    /// <summary>Normalize one entry to a bare host, or null if it is not a usable domain.</summary>
    private static string? Normalize(string entry)
    {
        var s = entry.Trim().ToLowerInvariant();

        // drop a scheme (http://, https://, anything://)
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];

        // drop path/query/fragment and any port
        foreach (var cut in new[] { '/', '?', '#', ':' })
        {
            var i = s.IndexOf(cut);
            if (i >= 0) s = s[..i];
        }

        if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];
        s = s.Trim('.');

        if (s.Length is 0 or > 253 || !s.Contains('.')) return null;
        // labels: letters/digits/hyphen only, and at least one dot-separated TLD-ish part
        foreach (var label in s.Split('.'))
        {
            if (label.Length is 0 or > 63) return null;
            foreach (var c in label)
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-')) return null;
        }
        return s;
    }

    /// <summary>
    /// Render the Curfew hosts section for <paramref name="domains"/> (apex + www for
    /// each), or an empty string when there is nothing to block (so no marker block
    /// is written). Lines are <c>\n</c>-joined for deterministic output.
    /// </summary>
    public static string RenderSection(IReadOnlyList<string> domains) => RenderSection(domains, Array.Empty<string>());

    /// <summary>
    /// As <see cref="RenderSection(IReadOnlyList{string})"/> but also appends arbitrary verbatim
    /// <paramref name="extraLines"/> (e.g. SafeSearch "ip host" entries) inside the same Curfew section.
    /// Empty when there is nothing to block and no extra lines.
    /// </summary>
    public static string RenderSection(IReadOnlyList<string> domains, IReadOnlyList<string> extraLines)
    {
        if (domains.Count == 0 && extraLines.Count == 0) return string.Empty;

        var lines = new List<string> { BeginMarker };
        foreach (var d in domains)
        {
            lines.Add($"{Sink} {d}");
            lines.Add($"{Sink} www.{d}");
        }
        lines.AddRange(extraLines);
        lines.Add(EndMarker);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Return <paramref name="existingHosts"/> with the Curfew section removed (every
    /// line from a BEGIN marker through the next END marker, inclusive). Other content
    /// is preserved. Tolerates CRLF or LF and a missing/!unterminated section.
    /// </summary>
    public static string StripSection(string? existingHosts)
    {
        if (string.IsNullOrEmpty(existingHosts)) return existingHosts ?? string.Empty;

        var lines = existingHosts.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        var inside = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!inside && trimmed == BeginMarker) { inside = true; continue; }
            if (inside)
            {
                if (trimmed == EndMarker) inside = false;
                continue;
            }
            kept.Add(line);
        }

        // drop trailing blank lines we may have left, keep a single trailing newline
        return string.Join('\n', kept).TrimEnd('\n');
    }

    /// <summary>
    /// Merge the blocklist into <paramref name="existingHosts"/>: strip any prior
    /// Curfew section, then append a fresh one for <paramref name="domains"/> (nothing
    /// appended when the list is empty). Idempotent. Output uses <c>\n</c> newlines.
    /// </summary>
    public static string Merge(string? existingHosts, IReadOnlyList<string> domains) =>
        Merge(existingHosts, domains, Array.Empty<string>());

    /// <summary>As <see cref="Merge(string, IReadOnlyList{string})"/> but also writes verbatim
    /// <paramref name="extraLines"/> (e.g. SafeSearch entries) into the Curfew section.</summary>
    public static string Merge(string? existingHosts, IReadOnlyList<string> domains, IReadOnlyList<string> extraLines)
    {
        var basePart = StripSection(existingHosts);
        var section = RenderSection(domains, extraLines);
        if (section.Length == 0) return basePart;
        return basePart.Length == 0 ? section : basePart + "\n\n" + section;
    }
}

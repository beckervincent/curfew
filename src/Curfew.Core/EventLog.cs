using System.Globalization;

namespace Curfew.Core;

/// <summary>Categories of events recorded for the parent's activity view.</summary>
public enum CurfewEventKind
{
    /// <summary>The lock screen was raised.</summary>
    Locked,

    /// <summary>The lock was dismissed with the passcode / a valid code.</summary>
    Unlocked,

    /// <summary>Bonus time was granted.</summary>
    Extended,

    /// <summary>A wrong passcode was entered at the lock.</summary>
    FailedUnlock,

    /// <summary>The weekly schedule was ignored until restart.</summary>
    ScheduleIgnored,

    /// <summary>The system clock was found tampered and corrected.</summary>
    ClockTamper,

    /// <summary>A content-filter / DoH-block step failed to apply.</summary>
    FilterFailure,

    /// <summary>An update was downloaded and scheduled to install.</summary>
    UpdateInstalled,

    /// <summary>A settings store was corrupt and had to be deleted and recreated.</summary>
    StoreRecreated,
}

/// <summary>One recorded event: when it happened, what kind, and a short detail.</summary>
public readonly record struct CurfewEvent(DateTimeOffset Time, CurfewEventKind Kind, string Detail);

/// <summary>
/// A tiny, dependency-free, cross-process append log of parent-facing events
/// (locks, failed unlocks, clock tampering, filter failures, updates). One
/// tab-separated line per event; the file is trimmed to the most recent
/// <see cref="MaxEntries"/> so it can never grow without bound.
/// </summary>
/// <remarks>
/// Every operation is best-effort and never throws: recording an event must not
/// disrupt enforcement, and the parent view must tolerate a missing or partially
/// written file. The path is a parameter so the writers (service, overlay) and the
/// reader (app) all point at <see cref="CurfewPaths.EventLogFile"/>, and tests can
/// use a temp file.
/// </remarks>
public static class EventLog
{
    /// <summary>Maximum events retained; older ones are dropped on append.</summary>
    public const int MaxEntries = 500;

    private static readonly object Gate = new();

    /// <summary>Appends an event. Never throws; failures are silently ignored.</summary>
    public static void Append(string path, CurfewEventKind kind, string detail)
    {
        if (string.IsNullOrEmpty(path)) return;

        var line = string.Concat(
            DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            "\t", kind.ToString(),
            "\t", Sanitize(detail));

        try
        {
            lock (Gate)
            {
                AppendLineShared(path, line);
                TrimIfNeeded(path);
            }
        }
        catch
        {
            // Diagnostics must never disrupt enforcement.
        }
    }

    /// <summary>
    /// Appends one line with <see cref="FileShare.ReadWrite"/> and a short retry.
    /// The in-process <see cref="Gate"/> cannot serialize the three writer
    /// PROCESSES (service, overlay, app); an exclusive append would make
    /// concurrent events fail with a sharing violation and silently vanish.
    /// Append-mode writes are atomic per call, and the retry covers the rare
    /// collision with the trim's rewrite.
    /// </summary>
    private static void AppendLineShared(string path, string line)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.Write(line + "\n");
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>
    /// Returns up to <paramref name="max"/> most-recent events, newest first.
    /// A missing or unreadable file yields an empty list.
    /// </summary>
    public static IReadOnlyList<CurfewEvent> ReadRecent(string path, int max)
    {
        if (string.IsNullOrEmpty(path) || max <= 0) return Array.Empty<CurfewEvent>();

        string[] lines;
        try
        {
            if (!File.Exists(path)) return Array.Empty<CurfewEvent>();
            lines = File.ReadAllLines(path);
        }
        catch
        {
            return Array.Empty<CurfewEvent>();
        }

        var result = new List<CurfewEvent>(Math.Min(max, lines.Length));
        for (var i = lines.Length - 1; i >= 0 && result.Count < max; i--)
        {
            if (TryParse(lines[i], out var ev)) result.Add(ev);
        }

        return result;
    }

    private static bool TryParse(string line, out CurfewEvent ev)
    {
        ev = default;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var parts = line.Split('\t');
        if (parts.Length < 3) return false;

        if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var time))
            return false;
        if (!Enum.TryParse<CurfewEventKind>(parts[1], out var kind)) return false;

        ev = new CurfewEvent(time, kind, parts[2]);
        return true;
    }

    private static void TrimIfNeeded(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length <= MaxEntries) return;

            var kept = lines[^MaxEntries..];
            File.WriteAllLines(path, kept);
        }
        catch
        {
            // If trimming fails the file simply keeps growing slowly; not critical.
        }
    }

    /// <summary>Strips tabs/newlines so a detail can never break the line format.</summary>
    private static string Sanitize(string? detail) =>
        (detail ?? string.Empty)
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
}

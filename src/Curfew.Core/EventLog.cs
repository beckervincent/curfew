using System.Globalization;

namespace Curfew.Core;

/// <summary>event kinds for parent activity view</summary>
public enum CurfewEventKind
{
    /// <summary>lock screen raised</summary>
    Locked,

    /// <summary>lock dismissed with passcode / valid code</summary>
    Unlocked,

    /// <summary>bonus time granted</summary>
    Extended,

    /// <summary>wrong passcode at lock</summary>
    FailedUnlock,

    /// <summary>weekly schedule ignored until restart</summary>
    ScheduleIgnored,

    /// <summary>clock found tampered and corrected</summary>
    ClockTamper,

    /// <summary>content-filter / DoH-block step failed to apply</summary>
    FilterFailure,

    /// <summary>update downloaded and scheduled to install</summary>
    UpdateInstalled,

    /// <summary>settings store corrupt, deleted and recreated</summary>
    StoreRecreated,

    /// <summary>child took a self-service break</summary>
    BreakTaken,

    /// <summary>a blocked app was closed</summary>
    AppBlocked,
}

/// <summary>one event: when, what kind, short detail</summary>
public readonly record struct CurfewEvent(DateTimeOffset Time, CurfewEventKind Kind, string Detail);

/// <summary>tiny dependency-free cross-process append log of parent-facing events; one tab-separated line each, trimmed to <see cref="MaxEntries"/></summary>
/// <remarks>
/// best-effort, never throws. writers (service, overlay) + reader (app) all point at <see cref="CurfewPaths.EventLogFile"/>; tests use temp file.
/// </remarks>
public static class EventLog
{
    /// <summary>max events kept; older dropped on append</summary>
    public const int MaxEntries = 500;

    private static readonly object Gate = new();

    /// <summary>append event. never throws; failures silently ignored</summary>
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
            // diagnostics never disrupt enforcement
        }
    }

    /// <summary>append one line with <see cref="FileShare.ReadWrite"/> + short retry; <see cref="Gate"/> can't serialize 3 writer PROCESSES, exclusive append would lose concurrent events. append writes atomic per call; retry covers rare collision with trim rewrite</summary>
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

    /// <summary>up to <paramref name="max"/> newest events, newest first; missing/unreadable file gives empty list</summary>
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
            // trim fail = file grows slowly; not critical
        }
    }

    /// <summary>strip tabs/newlines so detail can't break line format</summary>
    private static string Sanitize(string? detail) =>
        (detail ?? string.Empty)
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
}

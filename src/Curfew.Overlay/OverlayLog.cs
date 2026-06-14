using System.Text;

namespace Curfew.Overlay;

/// <summary>diagnostic file log under <c>%ProgramData%\Curfew\overlay.log</c>.
/// <para>overlay spawned by service, no console -> only window into start/draw/exit. defensive: writes serialized, exceptions swallowed, size-capped w/ single rolled backup so it can't grow unbounded over months</para>
/// </summary>
internal static class OverlayLog
{
    /// <summary>Serializes writes from every thread in this process.</summary>
    private static readonly object Gate = new();

    /// <summary>Roll the active log to <c>overlay.log.1</c> once it exceeds this size.</summary>
    private const long MaxBytes = 512 * 1024;

    /// <summary>Resolved once: the directory holding the log (created on first use).</summary>
    private static readonly string LogDirectory = ResolveLogDirectory();

    private static readonly string LogPath = Path.Combine(LogDirectory, "overlay.log");
    private static readonly string RolledPath = LogPath + ".1";

    /// <summary>Appends a single timestamped line to the diagnostic log.</summary>
    /// <param name="message">Free-form diagnostic text. Never thrown on.</param>
    public static void Write(string message)
    {
        // diagnostics must never break overlay; nothing throws
        try
        {
            var line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} " +
                $"pid={Environment.ProcessId} " +
                $"tid={Environment.CurrentManagedThreadId} " +
                $"{message}{Environment.NewLine}";

            lock (Gate)
            {
                RollIfTooLarge();
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // disk full / permissions / AV lock -- none surface
        }
    }

    /// <summary>active log past <see cref="MaxBytes"/> -> move aside to <c>overlay.log.1</c> (replace old backup); live file stays small, recent history kept. caller holds <see cref="Gate"/></summary>
    private static void RollIfTooLarge()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            // File.Move overwrite atomic enough for single-backup roll
            File.Delete(RolledPath);
            File.Move(LogPath, RolledPath);
        }
        catch
        {
            // roll fails -> keep appending to existing file
        }
    }

    /// <summary>resolve + create log dir; fall back to temp path if ProgramData unavailable</summary>
    private static string ResolveLogDirectory()
    {
        try
        {
            var programData =
                Environment.GetEnvironmentVariable("ProgramData")
                ?? @"C:\ProgramData";
            var dir = Path.Combine(programData, "Curfew");
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            try
            {
                var fallback = Path.Combine(Path.GetTempPath(), "Curfew");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
            catch
            {
                // last resort: current dir. Write() swallows failures from unwritable path
                return ".";
            }
        }
    }
}

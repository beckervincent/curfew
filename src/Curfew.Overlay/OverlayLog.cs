using System.Text;

namespace Curfew.Overlay;

/// <summary>
/// Diagnostic file log under <c>%ProgramData%\Curfew\overlay.log</c>.
/// <para>
/// The overlay process is usually spawned by the Windows service and has no
/// console, so this is the only window into why it starts, draws, or exits.
/// The log is therefore deliberately defensive: every write is serialized,
/// exceptions are swallowed, and the file is size-capped with a single rolled
/// backup so it can never grow without bound on a machine that runs for months.
/// </para>
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
        // Diagnostics must never break the overlay, so nothing here may throw.
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
            // Disk full, permissions, antivirus lock — none of these may surface.
        }
    }

    /// <summary>
    /// When the active log grows past <see cref="MaxBytes"/>, move it aside to
    /// <c>overlay.log.1</c> (replacing any previous backup) so the live file
    /// stays small and the most recent history is still preserved. Caller holds
    /// <see cref="Gate"/>.
    /// </summary>
    private static void RollIfTooLarge()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            // File.Move with overwrite is atomic enough for a single-backup roll.
            File.Delete(RolledPath);
            File.Move(LogPath, RolledPath);
        }
        catch
        {
            // If rolling fails we simply keep appending to the existing file.
        }
    }

    /// <summary>
    /// Resolves and creates the log directory, falling back to the system
    /// temp path if ProgramData is unavailable so logging still works.
    /// </summary>
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
                // Last resort: the current directory. Write() still swallows
                // any failure that results from an unwritable path here.
                return ".";
            }
        }
    }
}

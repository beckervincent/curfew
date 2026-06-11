namespace Curfew.Core;

/// <summary>
/// Well-known filesystem locations for Curfew.
/// </summary>
/// <remarks>
/// All data lives under <c>%ProgramData%\Curfew</c> so that the SYSTEM-hosted
/// service, the per-user tray app and the overlay all agree on a single,
/// machine-wide location regardless of which Windows session is active. The
/// folder name and layout here are an external contract: the installer
/// (<c>setup.iss</c>) and uninstall scripts reference the same
/// <c>Curfew</c> / <c>update</c> names, so changing them would orphan existing
/// installations.
/// </remarks>
public static class CurfewPaths
{
    /// <summary>
    /// Name of the application folder created under <c>%ProgramData%</c>.
    /// Mirrors the <c>DataFolder</c> define in the installer; keep them in sync.
    /// </summary>
    public const string AppFolderName = "Curfew";

    /// <summary>File name of the SQLite settings/usage database.</summary>
    private const string DatabaseFileName = "data.db";

    /// <summary>File name of the parent-facing activity/tamper event log.</summary>
    private const string EventLogFileName = "events.log";

    /// <summary>Subfolder that holds a downloaded installer awaiting a silent update.</summary>
    private const string UpdateFolderName = "update";

    /// <summary>Fallback used only if the <c>ProgramData</c> variable is missing.</summary>
    private const string DefaultProgramData = @"C:\ProgramData";

    /// <summary>
    /// Absolute path to the data directory under <c>%ProgramData%</c>
    /// (typically <c>C:\ProgramData\Curfew</c>). The directory is created on
    /// access if it does not already exist.
    /// </summary>
    /// <exception cref="System.IO.IOException">
    /// The directory could not be created (for example, a file with the same
    /// name already exists, or the volume is read-only).
    /// </exception>
    /// <exception cref="System.UnauthorizedAccessException">
    /// The caller lacks permission to create the directory.
    /// </exception>
    public static string DataDirectory
    {
        get
        {
            var dir = Path.Combine(ProgramDataRoot, AppFolderName);

            // Fail closed if the data directory has been replaced with a reparse
            // point (junction/symlink). A child who can create a junction here could
            // otherwise redirect the SYSTEM service's reads/writes — including the
            // staged installer it later executes — onto an attacker-controlled
            // target. A genuine install is always a real directory.
            var info = new DirectoryInfo(dir);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"Refusing to use '{dir}': it is a reparse point (possible junction redirect).");
            }

            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Absolute path to the SQLite database file
    /// (<c>%ProgramData%\Curfew\data.db</c>). Accessing this ensures
    /// <see cref="DataDirectory"/> exists, but does not create the file itself.
    /// </summary>
    public static string DatabaseFile => Path.Combine(DataDirectory, DatabaseFileName);

    /// <summary>
    /// Absolute path to the activity/tamper event log
    /// (<c>%ProgramData%\Curfew\events.log</c>), written by the service and overlay
    /// and shown to the parent in Settings.
    /// </summary>
    public static string EventLogFile => Path.Combine(DataDirectory, EventLogFileName);

    /// <summary>
    /// Absolute path to the staging folder for downloaded updates
    /// (<c>%ProgramData%\Curfew\update</c>). Accessing this ensures the parent
    /// <see cref="DataDirectory"/> exists, but does not create the update folder.
    /// </summary>
    public static string UpdateDirectory => Path.Combine(DataDirectory, UpdateFolderName);

    /// <summary>
    /// The machine-wide application-data root (<c>%ProgramData%</c>), falling
    /// back to a sensible default when the folder cannot be resolved.
    /// </summary>
    /// <remarks>
    /// Resolved via <see cref="Environment.SpecialFolder.CommonApplicationData"/>
    /// rather than the <c>ProgramData</c> environment variable: a non-admin child
    /// can set a per-process/per-user <c>ProgramData</c> variable and redirect the
    /// whole app onto an attacker-controlled database (enforcing nothing). The
    /// known-folder API is not overridable that way.
    /// </remarks>
    private static string ProgramDataRoot
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return string.IsNullOrWhiteSpace(root) ? DefaultProgramData : root;
        }
    }
}

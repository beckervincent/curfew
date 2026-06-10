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
    /// Absolute path to the staging folder for downloaded updates
    /// (<c>%ProgramData%\Curfew\update</c>). Accessing this ensures the parent
    /// <see cref="DataDirectory"/> exists, but does not create the update folder.
    /// </summary>
    public static string UpdateDirectory => Path.Combine(DataDirectory, UpdateFolderName);

    /// <summary>
    /// The machine-wide application-data root (<c>%ProgramData%</c>), falling
    /// back to a sensible default when the environment variable is unset or blank.
    /// </summary>
    private static string ProgramDataRoot
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("ProgramData");
            return string.IsNullOrWhiteSpace(root) ? DefaultProgramData : root;
        }
    }
}

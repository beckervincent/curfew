namespace Curfew.Core;

/// <summary>well-known filesystem locations for Curfew</summary>
/// <remarks>all data under <c>%ProgramData%\Curfew</c> so SYSTEM service, tray app, overlay agree on one machine-wide location whatever session is active. folder name + layout are an external contract: installer (<c>setup.iss</c>) and uninstall scripts reference the same <c>Curfew</c> / <c>update</c> names, so changing them orphans existing installs</remarks>
public static class CurfewPaths
{
    /// <summary>app folder name under <c>%ProgramData%</c>. mirrors <c>DataFolder</c> in installer; keep in sync</summary>
    public const string AppFolderName = "Curfew";

    /// <summary>legacy single-file settings/usage db (pre-split; migration source)</summary>
    private const string DatabaseFileName = "data.db";

    /// <summary>write-protected config store (policy + secrets)</summary>
    private const string ConfigFileName = "config.db";

    /// <summary>child-writable state store (per-day counters, lock coordination)</summary>
    private const string StateFileName = "state.db";

    /// <summary>parent-facing activity/tamper event log</summary>
    private const string EventLogFileName = "events.log";

    /// <summary>subfolder holding downloaded installer awaiting silent update</summary>
    private const string UpdateFolderName = "update";

    /// <summary>fallback only if <c>ProgramData</c> variable missing</summary>
    private const string DefaultProgramData = @"C:\ProgramData";

    /// <summary>absolute path to data dir under <c>%ProgramData%</c> (typically <c>C:\ProgramData\Curfew</c>). created on access if missing</summary>
    /// <exception cref="System.IO.IOException">dir couldn't be created (same-named file exists, or read-only volume)</exception>
    /// <exception cref="System.UnauthorizedAccessException">caller lacks permission to create dir</exception>
    public static string DataDirectory
    {
        get
        {
            var dir = Path.Combine(ProgramDataRoot, AppFolderName);

            // fail closed if data dir replaced by a reparse point (junction/symlink). a child who creates a junction here could redirect SYSTEM service reads/writes — including the staged installer it runs — onto an attacker target. genuine install is always a real directory
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

    /// <summary>absolute path to SQLite db (<c>%ProgramData%\Curfew\data.db</c>). access ensures <see cref="DataDirectory"/> exists but doesn't create the file</summary>
    public static string DatabaseFile => Path.Combine(DataDirectory, DatabaseFileName);

    /// <summary>absolute path to write-protected config store (<c>%ProgramData%\Curfew\config.db</c>)</summary>
    public static string ConfigFile => Path.Combine(DataDirectory, ConfigFileName);

    /// <summary>absolute path to child-writable state store (<c>%ProgramData%\Curfew\state.db</c>)</summary>
    public static string StateFile => Path.Combine(DataDirectory, StateFileName);

    /// <summary>open split settings stores, migrating from legacy single-file db on first run. one place that wires production paths together</summary>
    public static SettingsStore OpenSettings(DateOnly today, bool configWritable = false) =>
        SettingsStore.OpenSplit(ConfigFile, StateFile, DatabaseFile, today, configWritable);

    /// <summary>absolute path to activity/tamper event log (<c>%ProgramData%\Curfew\events.log</c>), written by service + overlay, shown to parent in Settings</summary>
    public static string EventLogFile => Path.Combine(DataDirectory, EventLogFileName);

    /// <summary>absolute path to update staging folder (<c>%ProgramData%\Curfew\update</c>). access ensures parent <see cref="DataDirectory"/> exists but doesn't create the update folder</summary>
    public static string UpdateDirectory => Path.Combine(DataDirectory, UpdateFolderName);

    /// <summary>machine-wide app-data root (<c>%ProgramData%</c>), falling back to a default when unresolvable</summary>
    /// <remarks>resolved via <see cref="Environment.SpecialFolder.CommonApplicationData"/> not the <c>ProgramData</c> env var: a non-admin child can set a per-process <c>ProgramData</c> var and redirect the whole app onto an attacker db (enforcing nothing). known-folder API isn't overridable that way</remarks>
    private static string ProgramDataRoot
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return string.IsNullOrWhiteSpace(root) ? DefaultProgramData : root;
        }
    }
}

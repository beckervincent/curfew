namespace Curfew.Core;

/// <summary>Well-known filesystem locations for Curfew.</summary>
public static class CurfewPaths
{
    public const string AppFolderName = "Curfew";

    /// <summary>The data directory under ProgramData, created if missing.</summary>
    public static string DataDirectory
    {
        get
        {
            var baseDir = Environment.GetEnvironmentVariable("ProgramData")
                          ?? @"C:\ProgramData";
            var dir = Path.Combine(baseDir, AppFolderName);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DatabaseFile => Path.Combine(DataDirectory, "data.db");

    public static string UpdateDirectory => Path.Combine(DataDirectory, "update");
}

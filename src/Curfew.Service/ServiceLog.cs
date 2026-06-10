namespace Curfew.Service;

/// <summary>Simple file log under ProgramData (SYSTEM-writable). The hosted
/// logger isn't visible when running under nssm, so this gives on-device
/// diagnostics.</summary>
internal static class ServiceLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "Curfew");
            Directory.CreateDirectory(dir);
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(dir, "service.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never throw.
        }
    }
}

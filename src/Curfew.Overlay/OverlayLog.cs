namespace Curfew.Overlay;

/// <summary>Diagnostic file log under ProgramData. The overlay has no console
/// when service-spawned, so this records why it starts or exits.</summary>
internal static class OverlayLog
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
                File.AppendAllText(Path.Combine(dir, "overlay.log"),
                    $"{DateTime.Now:HH:mm:ss} pid={Environment.ProcessId} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never throw.
        }
    }
}

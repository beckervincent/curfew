using System.Diagnostics;

namespace Curfew.Overlay;

/// <summary>
/// Launches and tracks the WinUI lock surface (<c>Curfew.App --lock</c>) that
/// renders on top of the overlay's black enforcement cover. The overlay keeps the
/// hard floor (black window + keyboard hook); this is the pretty layer, relaunched
/// by the overlay if it is killed.
/// </summary>
internal static class LockAppHost
{
    private static Process? _process;

    /// <summary>
    /// Starts the WinUI lock app. Returns false if the app cannot be found or
    /// launched — the caller then falls back to the built-in GDI lock so there is
    /// always an unlock path.
    /// </summary>
    public static bool Launch()
    {
        try
        {
            var overlayPath = Environment.ProcessPath; // ...\overlay\Curfew.Overlay.exe
            if (overlayPath is null) return false;

            var installRoot = Path.GetDirectoryName(Path.GetDirectoryName(overlayPath)!);
            if (installRoot is null) return false;

            var app = Path.Combine(installRoot, "app", "Curfew.App.exe");
            if (!File.Exists(app)) return false;

            // Release the prior surface's Win32 handle before overwriting the field.
            // WhileLockedTick() relaunches once per second while the surface is dead, so
            // without this every relaunch leaks a process handle until GC finalization —
            // a child can amplify this by killing Curfew.App.exe on a tight loop.
            _process?.Dispose();
            _process = Process.Start(new ProcessStartInfo(app, "--lock") { UseShellExecute = false });
            return _process is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether the launched lock app is still alive.</summary>
    public static bool IsRunning
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch { return false; }
        }
    }

    /// <summary>Terminates the lock app (called when the lock is dismissed).</summary>
    public static void Kill()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone or no rights — nothing more to do.
        }
        // Free the handle (and its exit wait registration), not just the reference.
        _process?.Dispose();
        _process = null;
    }
}

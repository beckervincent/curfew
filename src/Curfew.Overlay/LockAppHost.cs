using System.Diagnostics;

namespace Curfew.Overlay;

/// <summary>launch + track WinUI lock surface (<c>Curfew.App --lock</c>) on top of black cover; overlay relaunches if killed</summary>
internal static class LockAppHost
{
    private static Process? _process;

    /// <summary>start WinUI lock app; false if not found/launched -> caller falls back to GDI lock</summary>
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

            // release prior Win32 handle before overwrite; else every relaunch leaks handle until GC (child can loop-kill Curfew.App.exe)
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
            // already gone or no rights
        }
        // free handle + exit wait registration, not just reference
        _process?.Dispose();
        _process = null;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Curfew.Overlay;

/// <summary>process behind foreground window; budget tick exempts allow-listed apps</summary>
internal static class ForegroundApp
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>The foreground window's process image name (no <c>.exe</c>), or null.</summary>
    public static string? ProcessName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;

            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            // process maybe exited between calls -> "unknown"
            return null;
        }
    }

    /// <summary>foreground window full exe path, or null (exited / access denied -> caller treats unknown as not-allow-listed)</summary>
    public static string? ProcessImagePath()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;

            using var process = Process.GetProcessById((int)pid);
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}

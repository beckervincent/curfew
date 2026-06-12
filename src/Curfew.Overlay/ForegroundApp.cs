using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Curfew.Overlay;

/// <summary>
/// Resolves the process behind the current foreground window, so the budget tick
/// can exempt allow-listed apps (homework/IDE) from consuming screen time.
/// </summary>
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
            // The process may have exited between the calls — treat as "unknown".
            return null;
        }
    }

    /// <summary>
    /// The foreground window's full executable path, or null when it cannot be
    /// determined (process exited, or access denied for an elevated process —
    /// callers must treat unknown as not-allow-listed).
    /// </summary>
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

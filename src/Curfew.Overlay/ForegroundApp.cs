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

    /// <summary>foreground process id + image name (no <c>.exe</c>), or (0, null) when none/unavailable. lets the
    /// app-blocklist check the name and then terminate that exact process.</summary>
    public static (int Pid, string? Name) Foreground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return (0, null);
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return (0, null);
            using var process = Process.GetProcessById((int)pid);
            return ((int)pid, process.ProcessName);
        }
        catch
        {
            return (0, null);
        }
    }

    /// <summary>Terminate the process with <paramref name="pid"/>. Best effort — already-exited or access-denied is ignored.</summary>
    public static void Terminate(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(); }
        catch { /* gone or no rights */ }
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

using System.Diagnostics;

namespace Curfew.Service;

/// <summary>
/// Ensures the overlay runs in every active session. The overlay is launched
/// through a logon scheduled task (a .NET app fails to initialize when started
/// via CreateProcessAsUser, but starts cleanly from Task Scheduler). The
/// installer registers the task with an at-logon trigger; this re-triggers it
/// when an active session has no overlay — the watchdog.
/// </summary>
internal sealed class SessionManager
{
    public const string TaskName = "CurfewOverlay";

    /// <summary>Re-run the overlay task when an active session lacks it.</summary>
    public void Tick()
    {
        var active = SessionInterop.ActiveSessions();
        if (active.Count == 0) return;

        var overlaySessions = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("Curfew.Overlay"))
        {
            try { overlaySessions.Add((uint)p.SessionId); }
            catch { /* process exited */ }
            finally { p.Dispose(); }
        }

        var missing = active.Any(s => !overlaySessions.Contains(s));
        if (missing)
        {
            PowerShellRunner.Run($"schtasks /run /tn {TaskName}");
            ServiceLog.Write("triggered overlay task (a session had none)");
        }
    }
}

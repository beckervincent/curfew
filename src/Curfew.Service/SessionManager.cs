using System.Diagnostics;

namespace Curfew.Service;

/// <summary>Ensure overlay runs in every active session. Overlay launched via logon scheduled task (WinUI app fails to init under CreateProcessAsUser, starts clean from Task Scheduler). Installer registers at-logon trigger; this is watchdog re-triggering when active session has no overlay.</summary>
/// <remarks>Single instance held by <see cref="CurfewWorker"/>, ticked every couple seconds, so per-instance fields persist across ticks, debounce expensive <c>schtasks /run</c>.</remarks>
internal sealed class SessionManager
{
    /// <summary>Name of scheduled task launching overlay. Must match installer (see installer/setup.iss). No rename without updating installer.</summary>
    public const string TaskName = "CurfewOverlay";

    /// <summary>Process name (no extension) of overlay executable.</summary>
    private const string OverlayProcessName = "Curfew.Overlay";

    /// <summary>Min delay between two <c>schtasks /run</c> calls. Overlay needs seconds to start and register; without cooldown fast poll loop fires repeatedly while overlay spins up, spawning redundant powershell.exe/schtasks.</summary>
    private static readonly TimeSpan TriggerCooldown = TimeSpan.FromSeconds(15);

    private DateTimeOffset _lastTrigger = DateTimeOffset.MinValue;

    /// <summary>Re-run overlay task when at least one active session has no overlay. Debounced (see <see cref="TriggerCooldown"/>) so starting overlay has time to appear before re-trigger.</summary>
    public void Tick()
    {
        var active = SessionInterop.ActiveSessions();
        if (active.Count == 0) return;

        var overlaySessions = GetOverlaySessions();

        // session needs re-trigger if no overlay process running inside it
        if (!active.Any(session => !overlaySessions.Contains(session)))
            return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastTrigger < TriggerCooldown)
            return; // recent trigger likely still starting; let it

        _lastTrigger = now;
        TriggerOverlayTask();
    }

    /// <summary>Return set of session ids with a running overlay process. Never throws — enumeration best-effort, transient failure treated as "no overlays" just schedules a cheap retrigger.</summary>
    private static HashSet<uint> GetOverlaySessions()
    {
        var sessions = new HashSet<uint>();

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(OverlayProcessName);
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"could not enumerate overlay processes: {ex.Message}");
            return sessions;
        }

        foreach (var process in processes)
        {
            try
            {
                sessions.Add((uint)process.SessionId);
            }
            catch
            {
                // process exited between enumeration and access; ignore
            }
            finally
            {
                process.Dispose();
            }
        }

        return sessions;
    }

    /// <summary>Ask Task Scheduler to run overlay task on demand. Task name quoted to survive spaces; exit code inspected so failures hit the log.</summary>
    private static void TriggerOverlayTask()
    {
        var exitCode = PowerShellRunner.Run($"schtasks /run /tn \"{TaskName}\"");
        if (exitCode == 0)
            ServiceLog.Write("triggered overlay task (a session had none)");
        else
            ServiceLog.Write($"overlay task trigger failed (exit {exitCode})");
    }
}

using System.Diagnostics;

namespace Curfew.Service;

/// <summary>
/// Ensures the overlay runs in every active session. The overlay is launched
/// through a logon scheduled task (a .NET/WinUI app fails to initialize when
/// started directly via CreateProcessAsUser, but starts cleanly from Task
/// Scheduler). The installer registers the task with an at-logon trigger; this
/// class is the watchdog that re-triggers it whenever an active session has no
/// overlay process running.
/// </summary>
/// <remarks>
/// A single instance is held by <see cref="CurfewWorker"/> and ticked roughly
/// every couple of seconds, so per-instance fields persist across ticks and are
/// used here to debounce the relatively expensive <c>schtasks /run</c> call.
/// </remarks>
internal sealed class SessionManager
{
    /// <summary>
    /// Name of the scheduled task that launches the overlay. Must match the task
    /// registered by the installer (see installer/setup.iss). Do not rename
    /// without updating the installer.
    /// </summary>
    public const string TaskName = "CurfewOverlay";

    /// <summary>Process name (without extension) of the overlay executable.</summary>
    private const string OverlayProcessName = "Curfew.Overlay";

    /// <summary>
    /// Minimum delay between two <c>schtasks /run</c> invocations. The overlay
    /// needs a few seconds to start and register its process, so without this
    /// cooldown a fast poll loop would fire the task repeatedly while a freshly
    /// launched overlay is still spinning up, spawning redundant
    /// powershell.exe/schtasks processes.
    /// </summary>
    private static readonly TimeSpan TriggerCooldown = TimeSpan.FromSeconds(15);

    private DateTimeOffset _lastTrigger = DateTimeOffset.MinValue;

    /// <summary>
    /// Re-runs the overlay task when at least one active session has no overlay
    /// process. Triggering is debounced (see <see cref="TriggerCooldown"/>) so a
    /// starting overlay is given time to appear before we trigger again.
    /// </summary>
    public void Tick()
    {
        var active = SessionInterop.ActiveSessions();
        if (active.Count == 0) return;

        var overlaySessions = GetOverlaySessions();

        // A session needs the overlay re-triggered if no overlay process is
        // currently running inside it.
        if (!active.Any(session => !overlaySessions.Contains(session)))
            return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastTrigger < TriggerCooldown)
            return; // A recent trigger is likely still starting up; let it.

        _lastTrigger = now;
        TriggerOverlayTask();
    }

    /// <summary>
    /// Returns the set of session ids that currently have a running overlay
    /// process. Never throws — process enumeration is best effort, and treating
    /// a transient failure as "no overlays" simply schedules a (cheap) retrigger.
    /// </summary>
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
                // Process exited between enumeration and access; ignore it.
            }
            finally
            {
                process.Dispose();
            }
        }

        return sessions;
    }

    /// <summary>
    /// Asks Task Scheduler to run the overlay task on demand. The task name is
    /// quoted so it survives a name containing spaces, and the exit code is
    /// inspected so failures are surfaced to the log.
    /// </summary>
    private static void TriggerOverlayTask()
    {
        var exitCode = PowerShellRunner.Run($"schtasks /run /tn \"{TaskName}\"");
        if (exitCode == 0)
            ServiceLog.Write("triggered overlay task (a session had none)");
        else
            ServiceLog.Write($"overlay task trigger failed (exit {exitCode})");
    }
}

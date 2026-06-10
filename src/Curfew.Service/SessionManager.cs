namespace Curfew.Service;

/// <summary>
/// Keeps the tray app running in every interactive session. A single poll loop
/// both spawns the app on login and respawns it within seconds if it is killed
/// (the watchdog failsafe) — a standard user cannot stop the SYSTEM service
/// that does this.
/// </summary>
internal sealed class SessionManager
{
    private readonly string _appExePath;
    private readonly Dictionary<uint, IntPtr> _processes = new();

    public SessionManager(string appExePath) => _appExePath = appExePath;

    /// <summary>The Win32 overlay executable spawned into each session. It is a
    /// plain Win32 app (no WinUI), which starts reliably under the service's
    /// CreateProcessAsUser; the WinUI dialogs are launched separately by the
    /// user.</summary>
    public static string DefaultAppPath()
    {
        var serviceDir = AppContext.BaseDirectory;
        var overlayExe = Path.GetFullPath(Path.Combine(serviceDir, "..", "overlay", "Curfew.Overlay.exe"));
        if (File.Exists(overlayExe)) return overlayExe;
        // Fall back to a sibling file in the same directory (dev layout).
        return Path.Combine(serviceDir, "Curfew.Overlay.exe");
    }

    /// <summary>One poll pass: spawn missing apps, drop dead/ended sessions.</summary>
    public void Tick()
    {
        var active = SessionInterop.ActiveSessions();
        var activeSet = new HashSet<uint>(active);

        // Forget sessions that ended.
        foreach (var sessionId in _processes.Keys.Where(id => !activeSet.Contains(id)).ToList())
        {
            SessionInterop.Close(_processes[sessionId]);
            _processes.Remove(sessionId);
        }

        foreach (var sessionId in active)
        {
            var running = _processes.TryGetValue(sessionId, out var handle)
                          && !SessionInterop.HasProcessExited(handle);
            if (running) continue;

            if (handle != IntPtr.Zero) SessionInterop.Close(handle);
            _processes.Remove(sessionId);

            var newHandle = SessionInterop.LaunchInSession(sessionId, _appExePath);
            if (newHandle != IntPtr.Zero)
                _processes[sessionId] = newHandle;
        }
    }
}

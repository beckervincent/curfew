using System.Net.NetworkInformation;
using Curfew.Core;

namespace Curfew.Service;

/// <summary>Curfew service loop. Keep overlay alive every session (spawn + watchdog), enforce content filter, run Time Manipulation Guarding, check updates.</summary>
/// <remarks>
/// <para>Two cadences that must not interfere:</para>
/// <list type="bullet">
///   <item>Fast loop (<see cref="PollInterval"/>) only ticks <see cref="SessionManager"/>. Safety-critical; runs on loop thread, never blocks on slow network tasks.</item>
///   <item>Slow tasks (NTP time guard + update check, every <see cref="SlowInterval"/>) and content filter dispatched to thread pool — shell out to PowerShell/NTP, can stall seconds, never sit on loop thread.</item>
/// </list>
/// <para>Every dispatched task has own try/catch: content filter, time guard or updater failure degrades gracefully, never takes overlay watchdog down.</para>
/// </remarks>
public sealed class CurfewWorker : BackgroundService
{
    /// <summary>Cadence of overlay watchdog (safety-critical work).</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Cadence of the heavy network housekeeping (update check + download).</summary>
    private static readonly TimeSpan SlowInterval = TimeSpan.FromHours(6);

    /// <summary>Cadence of the clock-tamper check. Far tighter than <see cref="SlowInterval"/>: it used to
    /// be bundled into the 6-hour cycle, which left a child up to six hours of farmed daily budget (and a
    /// bypassed brute-force lockout) after a forward clock jump before the next NTP correction. The check is
    /// a light UDP query to a few NTP servers, so a few-minute cadence is cheap and shrinks that window.</summary>
    private static readonly TimeSpan TimeGuardInterval = TimeSpan.FromMinutes(5);

    /// <summary>Max time shutdown waits for in-flight slow cycle (maybe mid update-download) before service stops anyway.</summary>
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<CurfewWorker> _logger;
    private readonly SessionManager _sessions = new();

    /// <summary>Settings store to read lock state for Task Manager lockdown.</summary>
    private SettingsStore? _policyStore;

    /// <summary>SID Task Manager currently disabled for, or null if none.</summary>
    private string? _policyAppliedSid;

    /// <summary>Serialise content-filter applies. Applied from startup task and on every <see cref="NetworkChange.NetworkAddressChanged"/> (bursty); lock keeps two PowerShell applies from overlapping.</summary>
    private readonly object _filterGate = new();

    /// <summary>Guard against overlapping slow cycles. <see cref="SlowInterval"/> long, but hung NTP query or download could outlast it; flag ensures at most one cycle in flight.</summary>
    private volatile bool _slowCycleRunning;

    /// <summary>Most recent slow-cycle task, kept only so shutdown can wait for it (maybe mid update-download). Fire-and-forget otherwise.</summary>
    private Task _slowCycle = Task.CompletedTask;

    /// <summary>Guard against overlapping clock-tamper checks: a hung NTP round must not stack a second.</summary>
    private volatile bool _timeGuardRunning;

    /// <summary>Most recent clock-tamper task, kept so shutdown can drain it.</summary>
    private Task _timeGuardCycle = Task.CompletedTask;

    public CurfewWorker(ILogger<CurfewWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Curfew service started");
        ServiceLog.Write("service started");

        // apply content filter once at startup, subscribe to network changes — both off loop thread so slow PowerShell no delay first overlay spawn. handler removed in finally
        var startupFilter = Task.Run(() =>
        {
            // re-register overlay logon task if child removed it
            SelfHeal.EnsureOverlayTask();
            ApplyContentFilter();
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        }, CancellationToken.None);

        // host config-write pipe as SYSTEM so app can change write-protected config.db through us. own settings store (separate connection) keeps off loop thread's connection
        var pipeStore = CurfewPaths.OpenSettings(DateOnly.FromDateTime(DateTime.Now), configWritable: true);
        // config.db now exists (open above made it) — lock down: users read, not write/delete
        ConfigFileGuard.Protect(CurfewPaths.ConfigFile);
        var pipeServer = Task.Run(
            () => new ConfigPipeServer(pipeStore, OnConfigKeyChanged).RunAsync(stoppingToken), CancellationToken.None);

        // MinValue forces the first clock check and slow cycle to run immediately, not after a full interval
        var lastSlow = DateTimeOffset.MinValue;
        var lastTimeGuard = DateTimeOffset.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SafeTickSessions();
                SafeReconcileTaskManagerPolicy();

                if (DateTimeOffset.UtcNow - lastTimeGuard >= TimeGuardInterval && !_timeGuardRunning)
                {
                    lastTimeGuard = DateTimeOffset.UtcNow;
                    StartTimeGuardCycle();
                }

                if (DateTimeOffset.UtcNow - lastSlow >= SlowInterval && !_slowCycleRunning)
                {
                    lastSlow = DateTimeOffset.UtcNow;
                    StartSlowCycle(stoppingToken);
                }

                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            await DrainOnShutdownAsync(startupFilter).ConfigureAwait(false);
            // let config pipe drain, then release its store
            try { await Task.WhenAny(pipeServer, Task.Delay(ShutdownDrainTimeout)).ConfigureAwait(false); } catch { /* shutting down */ }
            pipeStore.Dispose();
            // never leave Task Manager disabled when service stops
            if (_policyAppliedSid is not null) TaskManagerPolicy.Clear(_policyAppliedSid);
            _policyStore?.Dispose();
            _logger.LogInformation("Curfew service stopped");
            ServiceLog.Write("service stopped");
        }
    }

    /// <summary>Dispatch the update check to the thread pool, track task so shutdown can drain it (it may be mid update-download). Guarded by <see cref="_slowCycleRunning"/> so cycles never overlap.</summary>
    private void StartSlowCycle(CancellationToken ct)
    {
        _slowCycleRunning = true;
        _slowCycle = Task.Run(async () =>
        {
            try
            {
                await CheckForUpdatesAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _slowCycleRunning = false;
            }
        }, CancellationToken.None);
    }

    /// <summary>Dispatch the clock-tamper check to the thread pool on its own tight cadence. It shells out to
    /// NTP and can stall for seconds, so it must never sit on the safety-critical watchdog loop; the
    /// <see cref="_timeGuardRunning"/> flag keeps a slow NTP round from stacking with the next due check.</summary>
    private void StartTimeGuardCycle()
    {
        _timeGuardRunning = true;
        _timeGuardCycle = Task.Run(() =>
        {
            try { EnforceTimeGuard(); }
            finally { _timeGuardRunning = false; }
        }, CancellationToken.None);
    }

    /// <summary>Wait, bounded timeout, for startup filter and in-flight slow cycle to settle so service no abandon update mid-write. Never throws — shutdown must complete.</summary>
    private async Task DrainOnShutdownAsync(Task startupFilter)
    {
        try
        {
            var pending = Task.WhenAll(startupFilter, _slowCycle, _timeGuardCycle);
            await Task.WhenAny(pending, Task.Delay(ShutdownDrainTimeout)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // underlying tasks swallow own errors; this only guards unexpected fault while awaiting
            _logger.LogWarning(ex, "Error while draining background work on shutdown");
        }
    }

    private void SafeTickSessions()
    {
        try
        {
            _sessions.Tick();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session tick failed");
            ServiceLog.Write($"session tick threw: {ex.Message}");
        }
    }

    /// <summary>Keep per-user Task Manager lockdown in sync with lock state overlay publishes (<c>lock_active</c> / <c>lock_sid</c>). Fully guarded so registry/DB hiccup no disturb overlay watchdog.</summary>
    private void SafeReconcileTaskManagerPolicy()
    {
        try
        {
            ReconcileTaskManagerPolicy();
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"taskmgr reconcile threw: {ex.Message}");
        }
    }

    private void ReconcileTaskManagerPolicy()
    {
        _policyStore ??= CurfewPaths.OpenSettings(DateOnly.FromDateTime(DateTime.Now), configWritable: true);

        var active = _policyStore.Get("lock_active") == "1";
        var sid = _policyStore.Get("lock_sid");

        if (active && !string.IsNullOrEmpty(sid))
        {
            if (_policyAppliedSid != sid)
            {
                // different session locked — restore previous first
                if (_policyAppliedSid is not null) TaskManagerPolicy.Clear(_policyAppliedSid);
                TaskManagerPolicy.Apply(sid);
                _policyAppliedSid = sid;
            }
            return;
        }

        // not locked. clear what we applied; startup failsafe also clears SID left by prior (maybe crashed) run so Task Manager never stranded disabled
        var stale = _policyAppliedSid ?? (string.IsNullOrEmpty(sid) ? null : sid);
        if (stale is not null) TaskManagerPolicy.Clear(stale);
        _policyAppliedSid = null;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => ApplyContentFilter();

    /// <summary>Config keys whose change should re-apply the content filter (DNS/DoH/hosts) right away.</summary>
    private static readonly HashSet<string> ContentFilterKeys = new(StringComparer.Ordinal)
    {
        "dns_filter_mode", "block_doh_bypass", "safesearch_enabled", "blocked_domains", "blocked_categories",
    };

    /// <summary>Called when a config key is written via the pipe (parent saved a setting). Re-applies the
    /// content filter immediately for the relevant keys so SafeSearch / blocklists / DNS mode take effect on
    /// save, not at the next reboot or network change. Dispatched off the pipe thread; ApplyContentFilter is
    /// self-guarded. Other keys are picked up by the overlay's own 30s reload.</summary>
    private void OnConfigKeyChanged(string key)
    {
        if (ContentFilterKeys.Contains(key))
            _ = Task.Run(ApplyContentFilter);
    }

    /// <summary>(Re)apply DNS content filter. Serialised by <see cref="_filterGate"/>, fully guarded so PowerShell failure or burst of network-change events never destabilise service.</summary>
    private void ApplyContentFilter()
    {
        try
        {
            lock (_filterGate)
            {
                using var settings = OpenSettings();
                ContentFilterApplier.Apply(settings);
                HostsFileApplier.Apply(settings);
            }
            ServiceLog.Write("content filter applied");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Content filter apply failed");
            ServiceLog.Write($"content filter failed: {ex.Message}");
        }
    }

    /// <summary>Run Time Manipulation Guarding when enabled: correct clock from trusted NTP if tampered. No-op when disabled or NTP unreachable (handled in <see cref="TimeGuardService"/>).</summary>
    private void EnforceTimeGuard()
    {
        try
        {
            using var settings = OpenSettings();
            if (settings.GetBool("time_guard_enabled", true))
            {
                TimeGuardService.Enforce();
                TimeGuardService.EnforceTimeZone(settings);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Time guard enforcement failed");
            ServiceLog.Write($"time guard failed: {ex.Message}");
        }
    }

    /// <summary>Check for and (if configured) stage app update. Honours <paramref name="ct"/> so stop request can abort in-progress download.</summary>
    private async Task CheckForUpdatesAsync(CancellationToken ct)
    {
        try
        {
            using var settings = OpenSettings();
            var version = typeof(CurfewWorker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            await UpdateService.RunAsync(settings, version, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopping mid-check expected; nothing to report
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            ServiceLog.Write($"update check failed: {ex.Message}");
        }
    }

    /// <summary>Open shared settings store for "today". Store self-heals corrupt db, so callers only handle I/O/permission failures.</summary>
    private static SettingsStore OpenSettings() =>
        CurfewPaths.OpenSettings(DateOnly.FromDateTime(DateTime.Now), configWritable: true);
}

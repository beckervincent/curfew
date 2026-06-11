using System.Net.NetworkInformation;
using Curfew.Core;

namespace Curfew.Service;

/// <summary>
/// Curfew service loop. Keeps the overlay alive in every session (spawn +
/// watchdog), enforces the content filter, runs Time Manipulation Guarding and
/// checks for updates.
/// </summary>
/// <remarks>
/// <para>
/// The design splits work into two cadences that must not interfere:
/// </para>
/// <list type="bullet">
///   <item>
///     The fast loop (<see cref="PollInterval"/>) only ticks the
///     <see cref="SessionManager"/>. Keeping the overlay running is the safety-
///     critical job, so it runs on the dedicated loop thread and is never
///     allowed to block on the slower, network-bound tasks.
///   </item>
///   <item>
///     The slow tasks (NTP time guard + update check, every
///     <see cref="SlowInterval"/>) and the content filter are dispatched onto
///     the thread pool. They each shell out to PowerShell / NTP and can stall
///     for seconds, so they must never sit on the loop thread.
///   </item>
/// </list>
/// <para>
/// Every dispatched task contains its own try/catch (see the helper methods):
/// a failure in the content filter, time guard or updater must degrade
/// gracefully and can never take the overlay watchdog down with it.
/// </para>
/// </remarks>
public sealed class CurfewWorker : BackgroundService
{
    /// <summary>Cadence of the overlay watchdog (the safety-critical work).</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Cadence of the network-bound housekeeping (time guard + update check).</summary>
    private static readonly TimeSpan SlowInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// Upper bound on how long shutdown waits for an in-flight slow cycle (which
    /// may be mid update-download) to finish before the service stops anyway.
    /// </summary>
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<CurfewWorker> _logger;
    private readonly SessionManager _sessions = new();

    /// <summary>Settings store used to read the lock state for the Task Manager lockdown.</summary>
    private SettingsStore? _policyStore;

    /// <summary>The SID Task Manager is currently disabled for, or null if none.</summary>
    private string? _policyAppliedSid;

    /// <summary>
    /// Serialises content-filter applies. The filter is applied from the startup
    /// task and again on every <see cref="NetworkChange.NetworkAddressChanged"/>
    /// event, and those events can arrive in bursts; the lock keeps two
    /// PowerShell applies from running over the top of each other.
    /// </summary>
    private readonly object _filterGate = new();

    /// <summary>
    /// Guards against overlapping slow cycles. Although <see cref="SlowInterval"/>
    /// is long, a hung NTP query or download could in theory outlast it; the flag
    /// ensures at most one time-guard/update cycle is in flight at a time.
    /// </summary>
    private volatile bool _slowCycleRunning;

    /// <summary>
    /// The most recent slow-cycle task, retained only so shutdown can wait for it
    /// (it may be mid update-download). Fire-and-forget otherwise.
    /// </summary>
    private Task _slowCycle = Task.CompletedTask;

    public CurfewWorker(ILogger<CurfewWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Curfew service started");
        ServiceLog.Write("service started");

        // Apply the content filter once at startup and subscribe to network
        // changes — both off the loop thread so a slow PowerShell call can never
        // delay the first overlay spawn. The handler is removed in the finally.
        var startupFilter = Task.Run(() =>
        {
            // Re-register the overlay logon task if a child removed it.
            SelfHeal.EnsureOverlayTask();
            ApplyContentFilter();
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        }, CancellationToken.None);

        // Host the config-write pipe as SYSTEM so the app can change write-protected
        // config.db through us. Its own settings store (separate connection) keeps it
        // off the loop thread's connection.
        var pipeStore = CurfewPaths.OpenSettings(DateOnly.FromDateTime(DateTime.Now), configWritable: true);
        // config.db now exists (created by the open above) — lock it down so users
        // can read it but not write or delete it.
        ConfigFileGuard.Protect(CurfewPaths.ConfigFile);
        var pipeServer = Task.Run(() => new ConfigPipeServer(pipeStore).RunAsync(stoppingToken), CancellationToken.None);

        // MinValue forces the first slow cycle to run immediately on startup
        // rather than waiting a full SlowInterval for the clock check.
        var lastSlow = DateTimeOffset.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SafeTickSessions();
                SafeReconcileTaskManagerPolicy();

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
            // Normal shutdown.
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            await DrainOnShutdownAsync(startupFilter).ConfigureAwait(false);
            // Let the config pipe drain, then release its store.
            try { await Task.WhenAny(pipeServer, Task.Delay(ShutdownDrainTimeout)).ConfigureAwait(false); } catch { /* shutting down */ }
            pipeStore.Dispose();
            // Never leave Task Manager disabled when the service stops.
            if (_policyAppliedSid is not null) TaskManagerPolicy.Clear(_policyAppliedSid);
            _policyStore?.Dispose();
            _logger.LogInformation("Curfew service stopped");
            ServiceLog.Write("service stopped");
        }
    }

    /// <summary>
    /// Dispatches the time guard and update check onto the thread pool, tracking
    /// the task so shutdown can drain it. Guarded by <see cref="_slowCycleRunning"/>
    /// so cycles never overlap.
    /// </summary>
    private void StartSlowCycle(CancellationToken ct)
    {
        _slowCycleRunning = true;
        _slowCycle = Task.Run(async () =>
        {
            try
            {
                EnforceTimeGuard();
                await CheckForUpdatesAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _slowCycleRunning = false;
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Waits, with a bounded timeout, for the startup filter and any in-flight
    /// slow cycle to settle so the service does not abandon an update mid-write.
    /// Never throws — shutdown must always complete.
    /// </summary>
    private async Task DrainOnShutdownAsync(Task startupFilter)
    {
        try
        {
            var pending = Task.WhenAll(startupFilter, _slowCycle);
            await Task.WhenAny(pending, Task.Delay(ShutdownDrainTimeout)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The underlying tasks already swallow their own errors; this only
            // guards against an unexpected fault while awaiting them.
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

    /// <summary>
    /// Keeps the per-user Task Manager lockdown in sync with the lock state the
    /// overlay publishes (<c>lock_active</c> / <c>lock_sid</c>). Fully guarded so a
    /// registry or DB hiccup can never disturb the overlay watchdog.
    /// </summary>
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
                // A different session became locked — restore the previous one first.
                if (_policyAppliedSid is not null) TaskManagerPolicy.Clear(_policyAppliedSid);
                TaskManagerPolicy.Apply(sid);
                _policyAppliedSid = sid;
            }
            return;
        }

        // Not locked. Clear whatever we applied; as a startup failsafe also clear a
        // SID left recorded by a previous (possibly crashed) run so Task Manager is
        // never stranded in the disabled state.
        var stale = _policyAppliedSid ?? (string.IsNullOrEmpty(sid) ? null : sid);
        if (stale is not null) TaskManagerPolicy.Clear(stale);
        _policyAppliedSid = null;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => ApplyContentFilter();

    /// <summary>
    /// (Re)applies the configured DNS content filter. Serialised by
    /// <see cref="_filterGate"/> and fully guarded so a PowerShell failure or a
    /// burst of network-change events can never destabilise the service.
    /// </summary>
    private void ApplyContentFilter()
    {
        try
        {
            lock (_filterGate)
            {
                using var settings = OpenSettings();
                ContentFilterApplier.Apply(settings);
            }
            ServiceLog.Write("content filter applied");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Content filter apply failed");
            ServiceLog.Write($"content filter failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs Time Manipulation Guarding when enabled: corrects the clock from
    /// trusted NTP time if it has been tampered with. No-op when disabled or
    /// when NTP is unreachable (handled downstream in <see cref="TimeGuardService"/>).
    /// </summary>
    private void EnforceTimeGuard()
    {
        try
        {
            using var settings = OpenSettings();
            if (settings.GetBool("time_guard_enabled", true))
                TimeGuardService.Enforce();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Time guard enforcement failed");
            ServiceLog.Write($"time guard failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks for and (if configured) stages an application update. Honours
    /// <paramref name="ct"/> so a stop request can abort an in-progress download.
    /// </summary>
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
            // Stopping mid-check is expected; nothing to report.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            ServiceLog.Write($"update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the shared settings store for "today". The store self-heals a
    /// corrupt database, so callers only have to handle I/O/permission failures.
    /// </summary>
    private static SettingsStore OpenSettings() =>
        CurfewPaths.OpenSettings(DateOnly.FromDateTime(DateTime.Now), configWritable: true);
}

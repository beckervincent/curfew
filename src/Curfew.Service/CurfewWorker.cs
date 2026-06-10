using System.Net.NetworkInformation;
using Curfew.Core;

namespace Curfew.Service;

/// <summary>
/// Curfew service loop. Keeps the overlay alive in every session (spawn +
/// watchdog), enforces the content filter, runs Time Manipulation Guarding and
/// checks for updates. Session spawning runs first and never blocks on the
/// slower tasks.
/// </summary>
public sealed class CurfewWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SlowInterval = TimeSpan.FromHours(6);

    private readonly ILogger<CurfewWorker> _logger;
    private readonly SessionManager _sessions = new(SessionManager.DefaultAppPath());

    public CurfewWorker(ILogger<CurfewWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Curfew service started");
        ServiceLog.Write($"service started; overlay path = {SessionManager.DefaultAppPath()}");

        // Content filter + network watch run off the spawn loop so a slow or
        // hung PowerShell call can never stop the overlay from launching.
        _ = Task.Run(() =>
        {
            ApplyContentFilter();
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        }, stoppingToken);

        var lastSlow = DateTimeOffset.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SafeTickSessions();

                if (DateTimeOffset.UtcNow - lastSlow >= SlowInterval)
                {
                    lastSlow = DateTimeOffset.UtcNow;
                    _ = Task.Run(async () =>
                    {
                        EnforceTimeGuard();
                        await CheckForUpdatesAsync(stoppingToken);
                    }, stoppingToken);
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
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

    private void OnNetworkChanged(object? sender, EventArgs e) => ApplyContentFilter();

    private void ApplyContentFilter()
    {
        try
        {
            using var settings = OpenSettings();
            ContentFilterApplier.Apply(settings);
            ServiceLog.Write("content filter applied");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Content filter apply failed");
            ServiceLog.Write($"content filter failed: {ex.Message}");
        }
    }

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
        }
    }

    private async Task CheckForUpdatesAsync(CancellationToken ct)
    {
        try
        {
            using var settings = OpenSettings();
            var version = typeof(CurfewWorker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            await UpdateService.RunAsync(settings, version, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
        }
    }

    private static SettingsStore OpenSettings() =>
        SettingsStore.Open(CurfewPaths.DatabaseFile, DateOnly.FromDateTime(DateTime.Now));
}

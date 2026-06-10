using System.Net.NetworkInformation;
using Curfew.Core;

namespace Curfew.Service;

/// <summary>
/// Curfew service loop. Keeps the tray app alive in every session (spawn +
/// watchdog), enforces the content filter, runs Time Manipulation Guarding and
/// checks for updates.
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

        ApplyContentFilter();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;

        var lastSlow = DateTimeOffset.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SafeTickSessions();

                if (DateTimeOffset.UtcNow - lastSlow >= SlowInterval)
                {
                    lastSlow = DateTimeOffset.UtcNow;
                    EnforceTimeGuard();
                    await CheckForUpdatesAsync(stoppingToken);
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
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => ApplyContentFilter();

    private void ApplyContentFilter()
    {
        try
        {
            using var settings = OpenSettings();
            ContentFilterApplier.Apply(settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Content filter apply failed");
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

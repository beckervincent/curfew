using System.Net.NetworkInformation;
using Curfew.Core;

namespace Curfew.Service;

/// <summary>
/// Curfew service loop. Enforces the content filter and Time Manipulation
/// Guarding. Per-session app spawning + watchdog respawn land in a later
/// milestone.
/// </summary>
public sealed class CurfewWorker : BackgroundService
{
    private static readonly TimeSpan TimeGuardInterval = TimeSpan.FromHours(6);

    private readonly ILogger<CurfewWorker> _logger;

    public CurfewWorker(ILogger<CurfewWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Curfew service started");

        ApplyContentFilter();
        // Re-pin the filter whenever the network changes (new Wi-Fi, ethernet, VPN).
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                EnforceTimeGuard();
                await CheckForUpdatesAsync(stoppingToken);
                await Task.Delay(TimeGuardInterval, stoppingToken);
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
            {
                TimeGuardService.Enforce();
            }
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

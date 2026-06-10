namespace Curfew.Service;

/// <summary>
/// Hosting skeleton for the Curfew service. Session enumeration, per-session
/// app spawning and the update cycle are wired up in later milestones.
/// </summary>
public sealed class CurfewWorker : BackgroundService
{
    private readonly ILogger<CurfewWorker> _logger;

    public CurfewWorker(ILogger<CurfewWorker> logger) => _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Curfew service started");
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}

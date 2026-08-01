using TradingFlow.Earnings;

namespace TradingFlow.Web.Services;

/// <summary>
/// Hosts the earnings application module without moving provider or analysis logic into the UI.
/// </summary>
public sealed class EarningsMonitorHostedService : BackgroundService
{
    private readonly EarningsMonitor monitor;

    public EarningsMonitorHostedService(EarningsMonitor monitor)
    {
        this.monitor = monitor;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => monitor.RunAsync(stoppingToken);
}

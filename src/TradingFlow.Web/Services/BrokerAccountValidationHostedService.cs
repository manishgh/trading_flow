using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Services;

/// <summary>
/// Validates the configured broker account before any runtime ingestion,
/// reconciliation, or dispatch hosted service is allowed to start.
/// </summary>
public sealed class BrokerAccountValidationHostedService(
    IBrokerClient broker,
    IBrokerAccountBindingService accountBinding,
    ILogger<BrokerAccountValidationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await accountBinding.ValidateAsync(broker, cancellationToken);
        logger.LogInformation("Broker account binding validated.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

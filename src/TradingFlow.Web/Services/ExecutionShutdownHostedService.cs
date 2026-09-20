using TradingFlow.Alpaca;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Web.Services;

/// <summary>
/// Runs the engine-owned broker drain after order-producing and journal-writing
/// hosted services have stopped. The coordinator performs its own final REST
/// reconciliation rather than relying on a concurrently running background loop.
/// </summary>
public sealed class ExecutionShutdownHostedService(
    AlpacaCredentialProvider credentials,
    IRawArchiveWriter rawArchiveWriter,
    IExecutionShutdownCoordinator shutdown,
    ILogger<ExecutionShutdownHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!credentials.IsConfigured)
        {
            return;
        }

        var alpacaOptions = AlpacaOptions.Create(ProductionProfile.Paper) with
        {
            KeyId = credentials.KeyId,
            SecretKey = credentials.SecretKey
        };
        using var broker = new AlpacaBrokerClient(
            new HttpClient(),
            new HttpClient(),
            alpacaOptions,
            rawArchiveWriter);
        var result = await shutdown.ShutdownAsync(broker, cancellationToken);
        logger.LogInformation(
            "Execution shutdown completed. NonProtectiveOrdersCanceled={NonProtectiveOrdersCanceled} PositionsFlattened={PositionsFlattened} PositionsRetained={PositionsRetained} ProtectionFailures={ProtectionFailures}.",
            result.NonProtectiveOrdersCanceled,
            result.PositionsFlattened,
            result.PositionsRetained,
            result.ProtectionFailures);
    }
}

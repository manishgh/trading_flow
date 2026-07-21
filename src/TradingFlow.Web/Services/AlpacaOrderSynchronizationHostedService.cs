using TradingFlow.Alpaca;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Web.Services;

/// <summary>
/// Hosts the provider-neutral order synchronizer with one Alpaca account WebSocket per process.
/// </summary>
public sealed class AlpacaOrderSynchronizationHostedService(
    AlpacaCredentialProvider credentials,
    IRawArchiveWriter rawArchiveWriter,
    IOrderSynchronizationCoordinator coordinator,
    IEntryAdmissionControl entryAdmission,
    OrderSynchronizationOptions options,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    ILogger<AlpacaOrderSynchronizationHostedService> logger) : BackgroundService
{
    private const string CredentialBlockSource = "alpaca_order_credentials";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!credentials.IsConfigured)
        {
            entryAdmission.Block(
                CredentialBlockSource,
                "ALPACA_CREDENTIALS_MISSING",
                "Alpaca credentials are absent; market research remains available but broker entries are disabled.",
                timeProvider.GetUtcNow());
            logger.LogWarning(
                "Alpaca order synchronization was not started because credentials are not configured. Broker entries are blocked.");
            return;
        }

        entryAdmission.Clear(CredentialBlockSource);
        var alpacaOptions = AlpacaOptions.Create(ProductionProfile.Paper) with
        {
            KeyId = credentials.KeyId,
            SecretKey = credentials.SecretKey
        };
        using var broker = new AlpacaBrokerClient(
            new HttpClient(),
            alpacaOptions,
            rawArchiveWriter);
        var runner = new OrderSynchronizationRunner(
            new AlpacaTradeUpdateStreamerFactory(alpacaOptions),
            broker,
            coordinator,
            options,
            timeProvider,
            loggerFactory.CreateLogger<OrderSynchronizationRunner>());
        await runner.RunAsync(stoppingToken);
    }
}

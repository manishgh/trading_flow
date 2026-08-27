using TradingFlow.Alpaca;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Web.Services;

public interface IAlpacaMarketStateStreamClientFactory
{
    IAlpacaMarketStateStreamClient Create();
}

/// <summary>
/// Creates the SIP transport owned by the fenced market-state service.
/// Credentials stay in the existing provider and are never copied to run files.
/// </summary>
public sealed class AlpacaMarketStateStreamClientFactory(
    AlpacaCredentialProvider credentials,
    ILoggerFactory loggerFactory) : IAlpacaMarketStateStreamClientFactory
{
    public IAlpacaMarketStateStreamClient Create()
    {
        var clientOptions = AlpacaOptions.Create(ProductionProfile.Paper) with
        {
            KeyId = credentials.KeyId,
            SecretKey = credentials.SecretKey,
            MarketDataFeed = "sip"
        };
        return new AlpacaStreamClient(
            clientOptions,
            loggerFactory.CreateLogger<AlpacaStreamClient>());
    }
}

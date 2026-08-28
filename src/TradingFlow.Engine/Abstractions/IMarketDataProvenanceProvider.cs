namespace TradingFlow.Engine.Abstractions;

/// <summary>
/// Non-secret identity for reproducible historical market-data caching. The
/// values describe the provider contract, never credentials or endpoints.
/// </summary>
public sealed record MarketDataProvenance(
    string ProviderIdentity,
    string DataFeed,
    string AdjustmentPolicy);

public interface IMarketDataProvenanceProvider
{
    MarketDataProvenance MarketDataProvenance { get; }
}

namespace TradingFlow.Engine.Abstractions;

/// <summary>
/// A market data source that can also say where each symbol is listed (Alpaca exchange codes such as NASDAQ, NYSE, ARCA,
/// AMEX, BATS). Backtests read a frozen security-master snapshot so every run names the exact listing data it used; it is
/// current listing data, not a history of listing changes. Used by the Nasdaq home-market regime rule.
/// </summary>
public interface IListingExchangeSource
{
    /// <summary>Symbol → listing exchange for every symbol the source knows; symbols it does not know are absent.</summary>
    Task<IReadOnlyDictionary<string, string>> LoadListingExchangesAsync(CancellationToken cancellationToken);
}

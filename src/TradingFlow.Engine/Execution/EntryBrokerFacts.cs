namespace TradingFlow.Engine.Execution;

/// <summary>
/// Current broker account facts used for pre-trade admission. Values are captured
/// together so buying-power and exposure decisions use one coherent observation.
/// </summary>
public sealed record BrokerAccountSnapshot(
    string AccountId,
    string Status,
    bool AccountBlocked,
    bool TradingBlocked,
    bool TradeSuspendedByUser,
    bool ShortingEnabled,
    decimal BuyingPower,
    decimal Equity,
    decimal LongMarketValue,
    decimal ShortMarketValue,
    DateTimeOffset ObservedAtUtc);

public interface IBrokerAccountProvider
{
    Task<BrokerAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Consolidated best bid/offer and latest eligible trade from one named feed.
/// A quote proves price quality; a trade proves that executions have recently occurred.
/// </summary>
public sealed record BrokerMarketObservation(
    string Symbol,
    string Feed,
    decimal? BidPrice,
    decimal? AskPrice,
    DateTimeOffset? QuoteTimestampUtc,
    decimal? LastTradePrice,
    DateTimeOffset? LastTradeTimestampUtc,
    DateTimeOffset ObservedAtUtc)
{
    public decimal? MidPrice => BidPrice is > 0m && AskPrice is > 0m
        ? (BidPrice.Value + AskPrice.Value) / 2m
        : null;

    public decimal? SpreadBps => MidPrice is > 0m && BidPrice is not null && AskPrice is not null
        ? (AskPrice.Value - BidPrice.Value) / MidPrice.Value * 10_000m
        : null;
}

public interface IBrokerMarketObservationProvider
{
    Task<BrokerMarketObservation> GetMarketObservationAsync(
        string symbol,
        EquityTradingSession session,
        CancellationToken cancellationToken);
}

public enum SecurityTradingState
{
    Unknown,
    TradingObserved,
    Halted,
    Paused
}

public sealed record SecurityTradingStatus(
    string Symbol,
    SecurityTradingState State,
    string? ProviderStatusCode,
    string? ProviderReasonCode,
    DateTimeOffset? ProviderTimestampUtc,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Holds event-driven halt/resume state. A current trade may establish the initial
/// normal baseline, but it must never clear an explicitly observed halt or pause.
/// </summary>
public interface ISecurityTradingStatusProvider
{
    Task EnsureObservedAsync(string symbol, CancellationToken cancellationToken);

    SecurityTradingStatus GetStatus(string symbol);
}

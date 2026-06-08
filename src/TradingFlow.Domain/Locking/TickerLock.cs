namespace TradingFlow.Domain.Locking;

public sealed record TickerLock(
    string Ticker,
    string PodId,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt
);

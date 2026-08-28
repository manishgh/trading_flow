namespace TradingFlow.Domain.Market;

public sealed record OhlcvBar(
    string Ticker,
    DateTimeOffset Timestamp,
    string Timeframe,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    string DataFeed = "unspecified",
    string AdjustmentPolicy = "unspecified",
    DateTimeOffset? KnownAtUtc = null,
    DateTimeOffset? CoverageVerifiedThroughUtc = null);

namespace TradingFlow.Domain.Market;

public sealed record OhlcvBar(
    string Ticker,
    DateTimeOffset Timestamp,
    string Timeframe,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);


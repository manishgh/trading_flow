namespace TradingFlow.Domain.Market;

public sealed record IndicatorSnapshot(
    string Ticker,
    DateTimeOffset Timestamp,
    string Timeframe,
    decimal CurrentPrice,
    decimal CurrentVolume,
    decimal? Vwap,
    decimal? Rsi,
    decimal? Atr,
    decimal? Ema20,
    decimal? Ema50,
    decimal? Ema200,
    decimal? BollingerMiddle,
    decimal? BollingerUpper,
    decimal? BollingerLower,
    decimal? RelativeVolume,
    decimal? MacdLine,
    decimal? MacdSignal,
    decimal? MacdHistogram,
    CatalystEvent? Catalyst = null);


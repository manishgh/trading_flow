namespace TradingFlow.Domain.Strategies;

public sealed record TradeSignal(
    string Ticker,
    DateTimeOffset Timestamp,
    string Timeframe,
    decimal CurrentPrice,
    decimal CurrentVolume,
    decimal CurrentRsi,
    decimal CurrentAtr,
    bool IsAboveVwap,
    bool IsVwapPullback,
    bool IsVwapReclaim,
    bool IsEma20Pullback,
    bool IsOpeningRangeBreakout,
    bool IsRecentHighBreakout,
    bool IsVolatilityContraction,
    bool IsPriceAboveEma20,
    bool IsPriceAboveEma50,
    bool IsEma20AboveEma50,
    decimal? VwapExtensionAtr,
    bool IsAboveBollingerMiddle,
    bool IsMacdHistogramPositive,
    bool IsMacdNotBearish);

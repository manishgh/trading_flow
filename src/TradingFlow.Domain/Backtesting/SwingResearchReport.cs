namespace TradingFlow.Domain.Backtesting;

public sealed record SwingResearchOptions(
    decimal MinimumOpportunityMovePct = 10m,
    int MaximumOpportunityHoldingBars = 30,
    int MaximumOpportunitiesPerDirectionPerTicker = 5);

public sealed record SwingResearchReport(
    string RunName,
    string StrategyName,
    decimal StartingCapital,
    decimal EndingCapital,
    decimal TotalReturnPct,
    IReadOnlyList<SwingWeeklyEquity> WeeklyEquity,
    IReadOnlyList<SwingTickerResearch> Tickers);

public sealed record SwingWeeklyEquity(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    decimal WeekEndEquity,
    decimal NetProfit,
    decimal GainPct,
    int ClosedTradeCount,
    IReadOnlyList<string> ClosedTickers);

public sealed record SwingTickerResearch(
    string Ticker,
    decimal? BuyAndHoldReturnPct,
    IReadOnlyList<SwingTradeDiagnostic> Trades,
    IReadOnlyList<SwingOpportunityDiagnostic> LongOpportunities,
    IReadOnlyList<SwingOpportunityDiagnostic> ShortOpportunities);

public sealed record SwingTradeDiagnostic(
    string Ticker,
    string Direction,
    DateTimeOffset EntryTimestamp,
    DateTimeOffset ExitTimestamp,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal NetProfit,
    decimal TradeReturnPct,
    string ExitReason,
    decimal MaxFavorableMovePct,
    decimal MaxAdverseMovePct,
    SwingIndicatorContext? EntryContext,
    SwingIndicatorContext? ExitContext);

public sealed record SwingOpportunityDiagnostic(
    string Ticker,
    string Direction,
    DateTimeOffset StartTimestamp,
    DateTimeOffset EndTimestamp,
    decimal StartClose,
    decimal EndClose,
    decimal MovePct,
    bool WasCaptured,
    int? EntryLagBars,
    decimal? CapturedMovePct,
    string Classification,
    SwingIndicatorContext? StartContext,
    SwingIndicatorContext? EndContext);

public sealed record SwingIndicatorContext(
    DateTimeOffset Timestamp,
    decimal Close,
    decimal Volume,
    decimal? Rsi,
    decimal? Atr,
    decimal? MacdHistogram,
    decimal? Sma10,
    decimal? Sma20,
    decimal? Sma50,
    decimal? Ema20,
    decimal? Vwap,
    decimal? BollingerUpper,
    decimal? BollingerLower,
    decimal? RelativeVolume);

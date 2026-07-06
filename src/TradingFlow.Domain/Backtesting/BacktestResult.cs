using TradingFlow.Domain.Orders;

namespace TradingFlow.Domain.Backtesting;

public sealed record BacktestResult(
    string RunName,
    string ResultPath,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    decimal StartingCapital,
    decimal EndingCapital,
    decimal NetProfit,
    decimal TotalReturnPct,
    decimal AverageDailyReturnPct,
    int TradingDayCount,
    decimal MaxDrawdownPct,
    WinnerStrategySummary? Winner,
    int ProcessedBarCount,
    int CandidateTradeCount,
    int AcceptedTradeCount,
    int RejectedTradeCount,
    int WinningTradeCount,
    int LosingTradeCount,
    IReadOnlyList<StrategyBacktestResult> StrategyResults,
    IReadOnlyList<TickerBacktestResult> TickerResults,
    BacktestValidationReport Validation,
    IReadOnlyList<BacktestTrade> CompletedTrades,
    IReadOnlyList<StrategyDiagnosticReport> Diagnostics,
    IReadOnlyList<MissedMoveAudit> MissedMoves,
    IReadOnlyList<FinalizedOrder> AcceptedOrders);

public sealed record WinnerStrategySummary(
    string StrategyId,
    string StrategyName,
    decimal EndingCapital,
    decimal NetProfit,
    decimal TotalReturnPct,
    decimal AverageDailyReturnPct,
    decimal MaxDrawdownPct,
    int AcceptedTradeCount,
    int RejectedTradeCount);


public sealed record MissedMoveAudit(
    string Ticker,
    string Timeframe,
    DateTimeOffset StartTimestamp,
    DateTimeOffset PeakTimestamp,
    decimal StartClose,
    decimal PeakHigh,
    decimal MovePct,
    int BarsToPeak,
    IReadOnlyList<string> StrategiesWithEntries,
    IReadOnlyList<string> StrategiesWithoutEntries);
public sealed record StrategyBacktestResult(
    string StrategyId,
    string StrategyName,
    string Source,
    decimal StartingCapital,
    decimal EndingCapital,
    decimal NetProfit,
    decimal TotalReturnPct,
    decimal AverageDailyReturnPct,
    int TradingDayCount,
    decimal MaxDrawdownPct,
    int CandidateTradeCount,
    int AcceptedTradeCount,
    int RejectedTradeCount,
    int WinningTradeCount,
    int LosingTradeCount,
    IReadOnlyList<BacktestTrade> CompletedTrades);

public sealed record TickerBacktestResult(
    string Ticker,
    bool Succeeded,
    int ProcessedBarCount,
    int CandidateOrderCount,
    string? ErrorMessage)
{
    public static TickerBacktestResult Completed(string ticker, int processedBarCount, int candidateOrderCount)
    {
        return new TickerBacktestResult(ticker, true, processedBarCount, candidateOrderCount, null);
    }

    public static TickerBacktestResult Failed(string ticker, Exception exception)
    {
        return new TickerBacktestResult(ticker, false, 0, 0, exception.Message);
    }
}

public sealed record StrategyDiagnosticReport(
    string StrategyId,
    string StrategyName,
    int EvaluatedBarCount,
    int CandidateTradeCount,
    int AcceptedTradeCount,
    decimal WinRatePct,
    decimal AverageWin,
    decimal AverageLoss,
    decimal RealizedRewardRiskRatio,
    DailyPnlSummary DailyPnl,
    IReadOnlyList<DirectionPnlSummary> DirectionPnl,
    IReadOnlyDictionary<string, int> ExitReasonCounts,
    IReadOnlyDictionary<string, int> RejectionCounts,
    IReadOnlyDictionary<string, IReadOnlyList<string>> RejectionExamples,
    IReadOnlyList<string> Suggestions);

public sealed record DailyPnlSummary(
    int TradingDayCount,
    int ActiveTradeDayCount,
    int WinningDayCount,
    int LosingDayCount,
    decimal NetProfit,
    decimal AverageNetProfitPerTradingDay,
    decimal AverageNetProfitPerActiveTradeDay,
    decimal BestDayNetProfit,
    DateOnly? BestDay,
    decimal WorstDayNetProfit,
    DateOnly? WorstDay);

public sealed record DirectionPnlSummary(
    string Direction,
    int TradeCount,
    int WinningTradeCount,
    int LosingTradeCount,
    decimal NetProfit,
    decimal AverageNetProfit,
    decimal BestTradeNetProfit,
    decimal WorstTradeNetProfit);




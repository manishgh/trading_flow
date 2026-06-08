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
    IReadOnlyList<FinalizedOrder> AcceptedOrders);

public sealed record WinnerStrategySummary(
    string StrategyId,
    string StrategyName,
    decimal EndingCapital,
    decimal NetProfit,
    decimal TotalReturnPct,
    decimal MaxDrawdownPct,
    int AcceptedTradeCount,
    int RejectedTradeCount);

public sealed record StrategyBacktestResult(
    string StrategyId,
    string StrategyName,
    string Source,
    decimal StartingCapital,
    decimal EndingCapital,
    decimal NetProfit,
    decimal TotalReturnPct,
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
    IReadOnlyDictionary<string, int> ExitReasonCounts,
    IReadOnlyDictionary<string, int> RejectionCounts,
    IReadOnlyList<string> Suggestions);

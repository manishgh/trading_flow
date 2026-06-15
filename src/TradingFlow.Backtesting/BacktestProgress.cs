namespace TradingFlow.Backtesting;

public sealed record BacktestProgress(
    string Stage,
    string Message,
    string? Ticker,
    int CompletedTickerCount,
    int TotalTickerCount,
    string? StrategyName = null)
{
    public static BacktestProgress StageOnly(string stage, string message)
    {
        return new BacktestProgress(stage, message, null, 0, 0);
    }

    public static BacktestProgress StrategyGroup(string strategyName, int totalTickers)
    {
        return new BacktestProgress("strategy_group_ready", $"Strategy group ready: {strategyName}.", null, 0, totalTickers, strategyName);
    }
}

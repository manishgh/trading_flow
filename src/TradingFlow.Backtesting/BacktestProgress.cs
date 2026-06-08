namespace TradingFlow.Backtesting;

public sealed record BacktestProgress(
    string Stage,
    string Message,
    string? Ticker,
    int CompletedTickerCount,
    int TotalTickerCount)
{
    public static BacktestProgress StageOnly(string stage, string message)
    {
        return new BacktestProgress(stage, message, null, 0, 0);
    }
}

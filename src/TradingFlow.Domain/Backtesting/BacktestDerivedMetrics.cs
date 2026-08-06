namespace TradingFlow.Domain.Backtesting;

/// <summary>
/// One point on a strategy's realised equity curve.
/// </summary>
/// <param name="Date">Trading date the point closes.</param>
/// <param name="Equity">Capital after every exit realised up to and including this date.</param>
/// <param name="DrawdownPct">Percentage below the running peak of realised equity.</param>
public sealed record EquityPoint(DateOnly Date, decimal Equity, decimal DrawdownPct);

/// <summary>
/// Figures the backtest result does not carry but that are derivable from the
/// trades it does carry.
///
/// The equity curve here is <b>realised</b> equity: it is replayed from completed
/// trades at their exit timestamps, so it is a step function and it understates
/// intraday drawdown. That is a real limitation, not a rounding detail - a
/// strategy that went deeply underwater inside a position and recovered before
/// exit will look smooth here. The screen labels the axis accordingly. Emitting
/// a true per-bar curve from the run would replace this; see design/api-gaps.md.
/// </summary>
public static class BacktestDerivedMetrics
{
    /// <summary>
    /// Replays completed trades cumulatively into a daily realised-equity series.
    /// Returns an empty series when the strategy produced no trades: a flat line
    /// at starting capital would imply the strategy ran and did nothing, which is
    /// a different claim.
    /// </summary>
    public static IReadOnlyList<EquityPoint> BuildRealisedEquityCurve(
        StrategyBacktestResult strategy)
    {
        if (strategy.CompletedTrades.Count == 0)
        {
            return [];
        }

        var byExitDate = strategy.CompletedTrades
            .GroupBy(trade => DateOnly.FromDateTime(trade.ExitTimestamp.UtcDateTime))
            .OrderBy(group => group.Key);

        var points = new List<EquityPoint>();
        var equity = strategy.StartingCapital;
        var peak = equity;
        foreach (var day in byExitDate)
        {
            equity += day.Sum(trade => trade.NetProfit);
            peak = Math.Max(peak, equity);
            var drawdown = peak > 0m ? (peak - equity) / peak * 100m : 0m;
            points.Add(new EquityPoint(day.Key, equity, drawdown));
        }

        return points;
    }

    /// <summary>
    /// Mean holding period across completed trades, or null when there are none.
    /// </summary>
    public static TimeSpan? AverageHold(StrategyBacktestResult strategy)
    {
        if (strategy.CompletedTrades.Count == 0)
        {
            return null;
        }

        var totalTicks = strategy.CompletedTrades
            .Sum(trade => (trade.ExitTimestamp - trade.EntryTimestamp).Ticks);
        return TimeSpan.FromTicks(totalTicks / strategy.CompletedTrades.Count);
    }

    /// <summary>
    /// Gross profit divided by gross loss.
    /// </summary>
    /// <remarks>
    /// Null when there are no losses at all: the ratio is undefined rather than
    /// infinite, and rendering it as a huge number would read as an extraordinary
    /// edge rather than as too small a sample to have lost yet.
    /// </remarks>
    public static decimal? ProfitFactor(StrategyBacktestResult strategy)
    {
        var grossProfit = strategy.CompletedTrades.Where(trade => trade.NetProfit > 0m).Sum(trade => trade.NetProfit);
        var grossLoss = Math.Abs(strategy.CompletedTrades.Where(trade => trade.NetProfit < 0m).Sum(trade => trade.NetProfit));
        return grossLoss <= 0m ? null : grossProfit / grossLoss;
    }

    /// <summary>
    /// Realised R multiple for one trade: the move achieved divided by the risk
    /// taken, where risk is the distance from entry to the initial stop.
    /// </summary>
    /// <remarks>
    /// Null when no stop was recorded, or when entry and stop coincide. A trade
    /// with no stop distance has no R, and substituting zero would make every
    /// such trade look like a break-even.
    /// </remarks>
    public static decimal? RMultiple(BacktestTrade trade)
    {
        var isShort = trade.Direction.Equals("short", StringComparison.OrdinalIgnoreCase);
        var risk = isShort
            ? trade.StopLossPrice - trade.EntryPrice
            : trade.EntryPrice - trade.StopLossPrice;
        if (risk <= 0m)
        {
            return null;
        }

        var move = isShort
            ? trade.EntryPrice - trade.ExitPrice
            : trade.ExitPrice - trade.EntryPrice;
        return move / risk;
    }
}

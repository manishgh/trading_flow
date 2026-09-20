using TradingFlow.Domain.Backtesting;
using TradingFlow.Web.Models;
using TradingFlow.Web.Pages;

namespace TradingFlow.Web.Pages.Shared;

/// <summary>
/// Everything the results phase renders, gathered so the partial takes one model
/// rather than reaching back through the page.
/// </summary>
public sealed record LabResultsModel(
    BacktestsModel Page,
    BacktestJobSnapshot Job,
    BacktestResult Result,
    StrategyBacktestResult Drilled,
    ResearchPromotionAssessment Gates)
{
    /// <summary>
    /// One plotted series: a strategy's realised equity curve reduced to the
    /// points the chart needs.
    /// </summary>
    /// <param name="StrategyId">Identifies the series for the legend.</param>
    /// <param name="Name">Legend label.</param>
    /// <param name="TotalReturnPct">Drives the stroke colour and the legend figure.</param>
    /// <param name="Points">Realised equity, one point per day with an exit.</param>
    public sealed record EquitySeries(
        string StrategyId,
        string Name,
        decimal TotalReturnPct,
        IReadOnlyList<EquityPoint> Points);

    /// <summary>
    /// Realised equity per strategy.
    /// </summary>
    /// <remarks>
    /// Replayed from completed trades, so it is a step function at exit
    /// timestamps and understates within-bar drawdown. The chart says so on its
    /// axis rather than presenting it as a true equity curve.
    /// </remarks>
    public IReadOnlyList<EquitySeries> Series => Result.StrategyResults
        .Select(strategy => new EquitySeries(
            strategy.StrategyId,
            strategy.StrategyName,
            strategy.TotalReturnPct,
            BacktestDerivedMetrics.BuildRealisedEquityCurve(strategy)))
        .Where(series => series.Points.Count > 1)
        .ToArray();

    /// <summary>Whether there is enough of a series to draw anything honest.</summary>
    public bool HasCurve => Series.Count > 0;

    /// <summary>
    /// Percentage return of each point against starting capital, so every series
    /// shares one axis regardless of the capital each strategy was given.
    /// </summary>
    public IReadOnlyList<decimal> ReturnPercents(EquitySeries series)
    {
        var start = Result.StrategyResults
            .First(strategy => strategy.StrategyId == series.StrategyId)
            .StartingCapital;
        return start <= 0m
            ? series.Points.Select(_ => 0m).ToArray()
            : series.Points.Select(point => (point.Equity - start) / start * 100m).ToArray();
    }

    public decimal AxisMaximum => Series.Count == 0
        ? 1m
        : Math.Max(1m, Series.SelectMany(ReturnPercents).DefaultIfEmpty(0m).Max());

    public decimal AxisMinimum => Series.Count == 0
        ? -1m
        : Math.Min(-1m, Series.SelectMany(ReturnPercents).DefaultIfEmpty(0m).Min());

    /// <summary>Largest absolute leaderboard value, used to scale the bars.</summary>
    public decimal LeaderboardScale => Result.StrategyResults.Count == 0
        ? 1m
        : Math.Max(1m, Result.StrategyResults.Max(strategy => Math.Abs(Page.MetricValue(strategy))));

    /// <summary>Up to four rejection reasons, largest first.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> TopRejections =>
        Page.DrilledDiagnostic?.RejectionCounts
            .OrderByDescending(entry => entry.Value)
            .Take(4)
            .ToArray()
        ?? [];

    public int LargestRejection => TopRejections.Count == 0 ? 1 : Math.Max(1, TopRejections.Max(entry => entry.Value));
}

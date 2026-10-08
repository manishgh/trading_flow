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
    /// <summary>Up to four rejection reasons, largest first.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> TopRejections =>
        Page.DrilledDiagnostic?.RejectionCounts
            .OrderByDescending(entry => entry.Value)
            .Take(4)
            .ToArray()
        ?? [];

    public int LargestRejection => TopRejections.Count == 0 ? 1 : Math.Max(1, TopRejections.Max(entry => entry.Value));
}

using TradingFlow.Domain.Backtesting;
using TradingFlow.Web.Models;
using TradingFlow.Web.Pages;
using TradingFlow.Application.Jobs;
using TradingFlow.Contracts.V1;

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
    /// <summary>Displays the selected strategy's server-calculated terminal book through the same API projection.</summary>
    public RunPortfolioStateResponse? PortfolioState => RunPortfolioStateProjection.Project(Drilled.PortfolioState);

    /// <summary>Uses the shared evidence contract for the selected zone strategy.</summary>
    public IReadOnlyList<RunZoneDecisionDetail> ZoneDecisions => RunPortfolioStateProjection.ProjectZones(
        Result.ZoneDecisions?.Where(item => item.StrategyId == Drilled.StrategyId).TakeLast(100).ToArray());

    /// <summary>Limits history observations to the selected strategy without interpreting an unknown status.</summary>
    public IReadOnlyList<RunHistoryEligibility> HistoryEligibility => RunPortfolioStateProjection.ProjectEligibility(
        Result.HistoryEligibility.Where(item => item.StrategyId == Drilled.StrategyId).ToArray());

    /// <summary>Up to four rejection reasons, largest first.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> TopRejections =>
        Page.DrilledDiagnostic?.RejectionCounts
            .OrderByDescending(entry => entry.Value)
            .Take(4)
            .ToArray()
        ?? [];

    public int LargestRejection => TopRejections.Count == 0 ? 1 : Math.Max(1, TopRejections.Max(entry => entry.Value));
}

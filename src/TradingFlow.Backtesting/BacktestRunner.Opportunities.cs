using System.Collections.Immutable;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Regime;

namespace TradingFlow.Backtesting;

public sealed partial class BacktestRunner
{
    private static BacktestOpportunitySnapshot CaptureOpportunities(string runName, PortfolioConfig portfolio,
        IReadOnlyCollection<StrategyDefinition> strategies, IReadOnlyList<BacktestCandidateTrade> candidates,
        IReadOnlyCollection<OhlcvBar> marketBars, UniverseMembership? universe,
        IReadOnlyDictionary<string, RegimeCalendar>? regimes)
    {
        var frozenCandidates = candidates.Select(candidate => candidate with {
            ExecutionReplay = candidate.ExecutionReplay is { } replay ? replay with {
                RequiredSliceStarts = replay.RequiredSliceStarts.ToImmutableArray() } : null }).ToImmutableArray();
        var gates = frozenCandidates.Select(candidate => new BacktestOpportunityEntryEligibility(
            candidate.CandidateId, candidate.EntryTimestamp.ToUniversalTime(),
            universe?.IsMember(candidate.Ticker, DateOnly.FromDateTime(candidate.EntryTimestamp.UtcDateTime)) ?? true,
            regimes is null || !regimes.TryGetValue(candidate.StrategyId, out var regime) || regime.IsOn(candidate.EntryTimestamp)))
            .ToImmutableArray();
        return new(runName, portfolio, strategies.ToImmutableArray(), frozenCandidates, marketBars.ToImmutableArray(), gates);
    }

    internal static UnifiedPortfolioBacktestResult ReplaySingleOpportunity(PortfolioConfig portfolio,
        StrategyDefinition strategy, BacktestCandidateTrade candidate, IReadOnlyCollection<OhlcvBar> marketBars,
        BacktestOpportunityEntryEligibility eligibility, IExecutionEventSink evidence)
    {
        var day = DateOnly.FromDateTime(candidate.EntryTimestamp.UtcDateTime);
        // Freeze the exact original entry-time gate verdicts; the same existing helper
        // applies them without consulting future membership or a mutable regime object.
        var universe = new UniverseMembership(new Dictionary<string, HashSet<DateOnly>>(StringComparer.OrdinalIgnoreCase) {
            [candidate.Ticker] = eligibility.UniverseEligible ? [day] : [] });
        var regimes = new Dictionary<string, RegimeCalendar>(StringComparer.Ordinal) {
            [strategy.StrategyId] = RegimeCalendar.FromOnDates(eligibility.RegimeEligible ? new HashSet<DateOnly> { day } : []) };
        return BuildUnifiedPortfolioResult(portfolio, [strategy], [candidate], marketBars, universe, regimes, evidence);
    }
}

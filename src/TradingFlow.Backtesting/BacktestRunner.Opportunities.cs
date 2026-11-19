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
    /// <summary>
    /// Captures every pre-portfolio candidate together with its frozen entry-time universe verdict, regime verdict,
    /// exact refusal reason (SPY, Nasdaq/QQQ, or VIX), and per-entry risk scale so independent shadow replays match
    /// original portfolio sizing and rejection labels.
    /// </summary>
    private static BacktestOpportunitySnapshot CaptureOpportunities(string runName, PortfolioConfig portfolio,
        IReadOnlyCollection<StrategyDefinition> strategies, IReadOnlyList<BacktestCandidateTrade> candidates,
        IReadOnlyCollection<OhlcvBar> marketBars, UniverseMembership? universe,
        IReadOnlyDictionary<string, RegimeCalendar>? regimes)
    {
        var frozenCandidates = candidates.Select(candidate => candidate with {
            ExecutionReplay = candidate.ExecutionReplay is { } replay ? replay with {
                RequiredSliceStarts = replay.RequiredSliceStarts.ToImmutableArray() } : null }).ToImmutableArray();
        var gates = frozenCandidates.Select(candidate =>
        {
            var universeEligible = universe?.IsMember(candidate.Ticker, DateOnly.FromDateTime(candidate.EntryTimestamp.UtcDateTime)) ?? true;
            var decision = regimes is not null && regimes.TryGetValue(candidate.StrategyId, out var regime)
                ? regime.EntryDecisionFor(candidate.EntryTimestamp, candidate.Ticker)
                : RegimeEntryDecision.Allowed;
            var regimeEligible = decision.Rejection is null;
            return new BacktestOpportunityEntryEligibility(
                candidate.CandidateId,
                candidate.EntryTimestamp.ToUniversalTime(),
                universeEligible,
                regimeEligible,
                RegimeRejection: decision.Rejection,
                EntryRiskScale: regimeEligible ? decision.RiskScale : 0m);
        }).ToImmutableArray();
        return new(runName, portfolio, strategies.ToImmutableArray(), frozenCandidates, marketBars.ToImmutableArray(), gates);
    }

    /// <summary>
    /// Replays a single candidate in an isolated fresh book using the exact frozen universe verdict, regime refusal
    /// reason, and per-entry risk scale captured at original admission time.
    /// </summary>
    internal static UnifiedPortfolioBacktestResult ReplaySingleOpportunity(PortfolioConfig portfolio,
        StrategyDefinition strategy, BacktestCandidateTrade candidate, IReadOnlyCollection<OhlcvBar> marketBars,
        BacktestOpportunityEntryEligibility eligibility, IExecutionEventSink evidence)
    {
        var day = DateOnly.FromDateTime(candidate.EntryTimestamp.UtcDateTime);
        // Freeze the exact original entry-time gate verdicts; the same existing helper
        // applies them without consulting future membership or a mutable regime object.
        var universe = new UniverseMembership(new Dictionary<string, HashSet<DateOnly>>(StringComparer.OrdinalIgnoreCase) {
            [candidate.Ticker] = eligibility.UniverseEligible ? [day] : [] });
        var rejection = eligibility.RegimeEligible
            ? null
            : eligibility.RegimeRejection ?? RegimeCalendar.MarketOffReason;
        var regimes = new Dictionary<string, RegimeCalendar>(StringComparer.Ordinal) {
            [strategy.StrategyId] = RegimeCalendar.FromFixedDecision(rejection, eligibility.EntryRiskScale) };
        return BuildUnifiedPortfolioResult(portfolio, [strategy], [candidate], marketBars, universe, regimes, evidence);
    }
}

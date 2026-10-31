using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Backtesting;

public sealed partial class BacktestRunner
{
    /// <summary>Reject a missing event source before universe, provider or candidate work can hide it.</summary>
    private void RequireEventBinding(BacktestRunConfig run)
    {
        if (ExecutionReplayPolicy.IsEventPortfolio(run.Engine.ExecutionReplayPolicyId) && eventPortfolioReplay is null)
            throw new InvalidOperationException("The SIP portfolio policy requires a frozen event source binding; candle fallback is forbidden.");
    }
    /// <summary>Runs the frozen event binding once and links its fills and admission decisions to ordinary result journals.</summary>
    internal async Task<PortfolioBuildOutcome> ExecuteEventPortfolioAsync(BacktestRunConfig run,
        StrategyDefinition[] strategies, IReadOnlyList<BacktestCandidateTrade> candidates,
        IReadOnlyCollection<OhlcvBar> bars, UniverseMembership? membership,
        IReadOnlyDictionary<string, RegimeCalendar> calendars, IExecutionEventSink journal, CancellationToken cancellationToken)
    {
        var binding = eventPortfolioReplay ?? throw new InvalidOperationException("The SIP portfolio requires a frozen event source binding.");
        var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (membership is not null && !membership.IsMember(candidate.Ticker, DateOnly.FromDateTime(candidate.EntryTimestamp.UtcDateTime)))
                excluded[candidate.CandidateId] = "universe_membership_missing";
            else if (calendars.TryGetValue(candidate.StrategyId, out var calendar) && !calendar.IsOn(candidate.EntryTimestamp))
                excluded[candidate.CandidateId] = "market_regime_off_on_entry_day";
        }
        var outcome = await binding.ExecuteAsync(new(run.RunName, run.Portfolio, strategies, candidates, bars, excluded), cancellationToken);
        if (outcome.UnavailableReasons.Count > 0 && outcome.Failures.Count == 0)
            throw new InvalidDataException("An unavailable event portfolio must provide explicit execution failures.");
        var audit = new PortfolioAdmissionAudit();
        foreach (var disposition in outcome.Dispositions.Where(x => !x.Accepted))
            audit.Reject(candidates.Single(x => x.CandidateId == disposition.CandidateId), disposition.Reason);
        foreach (var observation in outcome.Events)
        {
            var payload = JsonSerializer.SerializeToNode(observation.Event)!.AsObject();
            payload["OccurredAtNanoseconds"] = observation.OccurredAtNanoseconds;
            payload["KnownAtNanoseconds"] = observation.KnownAtNanoseconds;
            journal.Append(new(observation.Event.Symbol, strategies[0].StrategyName,
                observation.Event.OccurredAtUtc,
                observation.Event.LastFillQuantity > 0 ? ExecutionState.OrderFilled : ExecutionState.OrderStateChanged,
                observation.Event.Reason, "portfolio_execution", observation.Event.ClientOrderId.Split(':')[0], payload.ToJsonString()));
        }
        foreach (var failure in outcome.Failures)
            journal.Append(new(failure.Ticker, failure.StrategyName, failure.LastMarketTimestampUtc ?? failure.EntryTimestamp,
                ExecutionState.OrderRejected, failure.Reason, "portfolio_execution", failure.CandidateId, JsonSerializer.Serialize(failure)));
        return new(outcome.Trades, audit, outcome.Failures) { EventReplay = outcome };
    }
}

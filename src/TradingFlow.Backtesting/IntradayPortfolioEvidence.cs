using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Backtesting;

/// <summary>Publish the standard immutable hypothesis, fill and cutoff contracts, with intraday policy updates alongside them.</summary>
public static class IntradayPortfolioEvidence
{
    /// <summary>Bind every admitted order and actual fill to its original candidate before ordinary artifact verification runs.</summary>
    public static void Publish(IExecutionEventSink hypotheses, IExecutionEventSink execution,
        IReadOnlyList<BacktestCandidateTrade> candidates, IntradayPortfolioReplayOutcome replay)
    {
        var evidence = new List<ExecutionEvent>();
        var byId = candidates.ToDictionary(x => x.CandidateId, StringComparer.Ordinal);
        foreach (var candidate in candidates)
            hypotheses.Append(new(candidate.Ticker, candidate.StrategyName, candidate.EntryTimestamp,
                ExecutionState.SignalGenerated, "Completed intraday order hypothesis; future executions remain unobserved.",
                "candidate_hypothesis", candidate.CandidateId, JsonSerializer.Serialize(candidate)));
        foreach (var item in replay.Events)
            evidence.Add(new(item.Ticker, byId[item.CandidateId].StrategyName, item.TimestampUtc,
                item.State == "protection_updated" ? ExecutionState.TrailingStopAdjusted : ExecutionState.OrderStateChanged,
                item.Reason, "portfolio_execution", item.CandidateId, JsonSerializer.Serialize(item)));
        PublishZoneAlternativeDecisions(evidence, byId, replay.AlternativeDecisions);
        foreach (var fill in replay.FillEvents)
            evidence.Add(new(fill.Event.Symbol, byId[fill.CandidateId].StrategyName, fill.Event.OccurredAtUtc,
                ExecutionState.OrderFilled, fill.Event.Reason, "portfolio_execution", fill.CandidateId,
                FillPayload(fill)));
        PublishFailures(evidence, replay);
        PublishCutoffs(evidence, byId, replay.PortfolioState);
        foreach (var item in evidence.OrderBy(x => x.Timestamp)) execution.Append(item);
    }

    // Retain every ordered sizing and ratio check in the ordinary journal, including attempts beyond the bounded UI rows.
    private static void PublishZoneAlternativeDecisions(ICollection<ExecutionEvent> execution,
        IReadOnlyDictionary<string, BacktestCandidateTrade> candidates, IReadOnlyList<IntradayZoneAlternativeDecision> alternatives)
    {
        foreach (var alternative in alternatives)
        {
            var candidate = candidates[alternative.CandidateId];
            execution.Add(new(candidate.Ticker, candidate.StrategyName, candidate.EntryTimestamp,
                ExecutionState.OrderStateChanged, alternative.Reason, "portfolio_execution", candidate.CandidateId,
                JsonSerializer.Serialize(alternative)));
        }
    }

    /// <summary>Keep the existing physical fill contract and retain its interval/phase sequence when adjacent frames share a UTC timestamp.</summary>
    private static string FillPayload(IntradayExecutionFill fill)
    {
        var payload = JsonSerializer.SerializeToNode(fill.Event)!.AsObject();
        payload[nameof(fill.SourceIntervalStartUtc)] = JsonSerializer.SerializeToNode(fill.SourceIntervalStartUtc);
        payload[nameof(fill.ExecutionPhase)] = fill.ExecutionPhase;
        payload[nameof(fill.ExecutionSequence)] = fill.ExecutionSequence;
        return payload.ToJsonString();
    }

    /// <summary>Retain each unresolved route/exposure once using its observed failure time, never the wall clock at publication.</summary>
    private static void PublishFailures(ICollection<ExecutionEvent> execution, IntradayPortfolioReplayOutcome replay)
    {
        var times = replay.Events.Where(x => x.State == "unresolved").ToDictionary(x => x.CandidateId, x => x.TimestampUtc, StringComparer.Ordinal);
        foreach (var failure in replay.Failures)
            execution.Add(new(failure.Ticker, failure.StrategyName,
                times.GetValueOrDefault(failure.CandidateId, replay.PortfolioState.CutoffUtc), ExecutionState.OrderStateChanged,
                failure.Reason, "portfolio_execution", failure.CandidateId, JsonSerializer.Serialize(failure)));
    }

    /// <summary>Capture one terminal observation per admitted parent, including canceled nonfills and every outstanding exit intention.</summary>
    private static void PublishCutoffs(ICollection<ExecutionEvent> execution,
        IReadOnlyDictionary<string, BacktestCandidateTrade> candidates, BacktestPortfolioState state)
    {
        var pending = state.PendingOrders.ToLookup(x => x.CandidateId, StringComparer.Ordinal);
        var exits = state.PendingExits.ToLookup(x => x.CandidateId, StringComparer.Ordinal);
        var positions = state.OpenPositions.ToDictionary(x => x.CandidateId, x => x.SignedQuantity, StringComparer.Ordinal);
        foreach (var order in state.AdmittedOrders)
            execution.Add(new(order.Ticker, candidates[order.CandidateId].StrategyName, state.CutoffUtc,
                ExecutionState.OrderStateChanged, "Known intraday book state at study cutoff.", "portfolio_execution",
                order.CandidateId, JsonSerializer.Serialize(new
                {
                    state.CutoffUtc, AdmittedOrder = order, PendingOrders = pending[order.CandidateId].ToArray(),
                    PendingExits = exits[order.CandidateId].ToArray(), TerminalSignedQuantity = positions.GetValueOrDefault(order.CandidateId)
                })));
    }
}

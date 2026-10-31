using System.Collections.Immutable;
using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Backtesting;

/// <summary>Research observation hook; never participates in actual portfolio admission.</summary>
public interface IBacktestOpportunityObserver
{
    Task ObserveAsync(BacktestOpportunitySnapshot snapshot, CancellationToken cancellationToken);
}

public sealed record BacktestOpportunityEntryEligibility(string CandidateId,
    DateTimeOffset EntryTimestampUtc, bool UniverseEligible, bool RegimeEligible,
    string EvidenceSource = "original_runner_entry_time_universe_and_regime_gates");

/// <summary>Every candidate before portfolio selection, with no mutable membership/provider state.</summary>
public sealed record BacktestOpportunitySnapshot(string RunName, PortfolioConfig Portfolio,
    ImmutableArray<StrategyDefinition> Strategies, ImmutableArray<BacktestCandidateTrade> Candidates,
    ImmutableArray<OhlcvBar> MarketBars, ImmutableArray<BacktestOpportunityEntryEligibility> EntryEligibility);

public sealed record BacktestOpportunityShadowOutcome(BacktestCandidateTrade Candidate,
    BacktestOpportunityEntryEligibility EntryEligibility, string Status, BacktestTrade? CompletedTrade,
    ImmutableDictionary<string, int> AdmissionRejectionCounts,
    ImmutableArray<BacktestExecutionFailure> ExecutionFailures, long ExecutionEventCount,
    int EntryFilledQuantity, int ExitFilledQuantity, decimal Fees,
    string EvidenceScope = "independent_opportunity_shadow");

/// <summary>
/// Replays each unchanged candidate in a fresh book with the original portfolio
/// sizing. The returned opportunities do not form a combined executable portfolio.
/// </summary>
public static class BacktestOpportunityShadowReplay
{
    public static ImmutableArray<BacktestOpportunityShadowOutcome> Replay(
        BacktestOpportunitySnapshot snapshot, IExecutionEventSink? executionEvidence = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Candidates.IsDefault || snapshot.Strategies.IsDefault || snapshot.MarketBars.IsDefault ||
            snapshot.EntryEligibility.IsDefault || snapshot.EntryEligibility.Length != snapshot.Candidates.Length)
            throw new InvalidDataException("Opportunity replay requires a complete immutable pre-portfolio snapshot.");
        var eligibility = snapshot.EntryEligibility.ToDictionary(item => item.CandidateId, StringComparer.Ordinal);
        if (snapshot.Candidates.Select(item => item.CandidateId).Distinct(StringComparer.Ordinal).Count() != snapshot.Candidates.Length)
            throw new InvalidDataException("Opportunity snapshot contains duplicated candidates.");
        var outcomes = ImmutableArray.CreateBuilder<BacktestOpportunityShadowOutcome>(snapshot.Candidates.Length);
        foreach (var candidate in snapshot.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!eligibility.TryGetValue(candidate.CandidateId, out var gate) || gate.EntryTimestampUtc != candidate.EntryTimestamp)
                throw new InvalidDataException("Opportunity candidate and original entry eligibility do not match.");
            var strategy = snapshot.Strategies.Single(item => item.StrategyId == candidate.StrategyId);
            // Pass real evidence explicitly, even when empty, so the existing book
            // records a missing-opening failure instead of synthesizing a bar.
            var sink = new OpportunityEvidenceSink(candidate.CandidateId, executionEvidence);
            var result = BacktestRunner.ReplaySingleOpportunity(snapshot.Portfolio, strategy,
                candidate, snapshot.MarketBars, gate, sink);
            var failures = (result.ExecutionFailures ?? []).ToImmutableArray();
            var rejections = (result.AdmissionRejectionCounts ?? ImmutableDictionary<string, int>.Empty)
                .ToImmutableDictionary(StringComparer.Ordinal);
            var trade = result.CompletedTrades.SingleOrDefault();
            var status = failures.Length > 0 ? "incomplete" : trade is not null ? "completed" :
                rejections.Count > 0 ? "admission_rejected" : sink.EventCount > 0 && sink.EntryFilledQuantity == 0
                    ? "not_filled" : "unavailable";
            outcomes.Add(new(candidate, gate, status, trade, rejections, failures, sink.EventCount,
                sink.EntryFilledQuantity, sink.ExitFilledQuantity, sink.Fees));
        }
        return outcomes.MoveToImmutable();
    }

    private sealed class OpportunityEvidenceSink(string candidateId, IExecutionEventSink? downstream) : IExecutionEventSink
    {
        public long EventCount { get; private set; }
        public int EntryFilledQuantity { get; private set; }
        public int ExitFilledQuantity { get; private set; }
        public decimal Fees { get; private set; }
        public void Append(ExecutionEvent item)
        {
            if (item.ReferenceId != candidateId)
                throw new InvalidDataException("Shadow execution evidence references another opportunity.");
            if (item.EvidenceJson is not null)
            {
                using var document = JsonDocument.Parse(item.EvidenceJson);
                var value = document.RootElement;
                // Fail() emits a BacktestExecutionFailure, not a fill event.
                // Preserve it without interpreting cumulative failure fees as
                // another fill or attempting to read absent fill properties.
                if (value.TryGetProperty("LastFillQuantity", out var lastFill) &&
                    value.TryGetProperty("ClientOrderId", out var orderId) &&
                    value.TryGetProperty("Fees", out var fees))
                {
                    var filled = lastFill.GetInt32();
                    if (orderId.GetString() == "entry-" + candidateId) EntryFilledQuantity += filled;
                    else ExitFilledQuantity += filled;
                    Fees += fees.GetDecimal();
                }
                else if (item.State == ExecutionState.OrderFilled)
                    throw new InvalidDataException("A filled shadow event has no complete fill evidence.");
            }
            EventCount++;
            downstream?.Append(item with { EvidenceScope = "independent_opportunity_shadow" });
        }
    }
}

using System.Collections.Immutable;
using System.Reflection;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class BacktestOpportunityShadowReplayTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
    private static StrategyDefinition Strategy(decimal slippage = 10) {
        var original = StrategyDecisionKernelTests.CreateRequest().Strategy;
        return original with { Execution = original.Execution with { SlippageBps = slippage } };
    }
    private static PortfolioConfig Portfolio() => new(10000, 1, 25, 4, 1, 1, 1, true,
        MaxBarParticipationPct: 100, SecFeeRate: .0000278m, FinraTafPerShare: .000166m, FinraTafCap: 8.3m);
    private static OhlcvBar Bar(int minute, decimal price = 10, decimal volume = 10000) =>
        new("X", Start.AddMinutes(minute), "5m", price, price, price, price, volume);
    private static BacktestCandidateTrade Candidate(string id = "candidate", decimal stop = 9) =>
        new("X", Strategy().StrategyName, "long", Start, 10, stop, 12, Start.AddMinutes(10), 0,
            "technical_exit", 10 - stop, StrategyId: Strategy().StrategyId, CandidateId: id) {
            ExecutionReplay = new(ExecutionReplayPolicy.DailyRthFiveMinuteV1, Start.Date.AddHours(4),
                Start.Date.AddDays(1).AddHours(4), Start.AddMinutes(15),
                new[] { Start, Start.AddMinutes(5), Start.AddMinutes(10), Start.AddMinutes(15) }) };
    private static BacktestOpportunitySnapshot Snapshot(params BacktestCandidateTrade[] candidates) =>
        new("fixture", Portfolio(), [Strategy()], candidates.ToImmutableArray(),
            [Bar(0), Bar(5), Bar(10, 11), Bar(15, 11)],
            candidates.Select(item => new BacktestOpportunityEntryEligibility(item.CandidateId, item.EntryTimestamp, true, true)).ToImmutableArray());

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    public void SingleOpportunityMatchesSharedPlannerAndCoordinatorIncludingCosts(int slippage)
    {
        var snapshot = Snapshot(Candidate()) with { Strategies = [Strategy(slippage)] };
        var outcome = Assert.Single(BacktestOpportunityShadowReplay.Replay(snapshot));
        var candidate = snapshot.Candidates[0];
        var planned = new SharedOrderRiskPlanner().Plan(new(candidate.Ticker, PlannedOrderSide.Long,
            snapshot.Portfolio.StartingCapital, candidate.EntryPrice,
            PlannedStopRequest.FromResolvedPrice(PlannedStopKind.Structural, candidate.StopLossPrice),
            new(snapshot.Portfolio.AccountRiskBudgetPct, snapshot.Portfolio.MaxPositionNotionalPct),
            new(0, snapshot.Portfolio.FixedBuyFee, snapshot.Portfolio.FixedSellFee), 2500));
        Assert.True(planned.IsAccepted);
        var coordinator = new ChronologicalBacktestExecutionCoordinator(snapshot.Portfolio, snapshot.MarketBars);
        Assert.True(coordinator.TrySchedule(new(candidate, snapshot.Strategies[0], planned.Order!.Quantity), out var rejection), rejection);
        coordinator.Complete();
        Assert.Empty(coordinator.Failures);
        Assert.Equal(Assert.Single(coordinator.CompletedTrades), outcome.CompletedTrade);
        Assert.Equal("completed", outcome.Status);
        Assert.True(outcome.Fees > 2m);
        Assert.Equal(outcome.CompletedTrade!.ShareQuantity, outcome.EntryFilledQuantity);
        Assert.Equal(outcome.EntryFilledQuantity, outcome.ExitFilledQuantity);
    }

    [Fact]
    public void KeptOpeningGap_IsSizedFromThePriorCloseAndCompletesThroughThePortfolioBook()
    {
        var baseStrategy = Strategy();
        var strategy = baseStrategy with { Execution = baseStrategy.Execution with { Timeframe = "5m" } };
        var candidate = new BacktestCandidateTrade("X", strategy.StrategyName, "long", Start, 9, 9.5m, 12, Start.AddMinutes(5), 9,
            "gap_fill_through_structural_stop", 0.5m, StrategyId: strategy.StrategyId, CandidateId: "gap") { PlanningReferencePrice = 10 };
        var method = typeof(BacktestRunner).GetMethod("BuildUnifiedPortfolioResult", BindingFlags.Static | BindingFlags.NonPublic)!;

        var combined = (UnifiedPortfolioBacktestResult)method.Invoke(null, new object?[] { Portfolio(), new[] { strategy },
            new[] { candidate }, new[] { Bar(0, 9), Bar(5, 9), Bar(10, 9) }, null, null, null, null })!;

        var trade = Assert.Single(combined.CompletedTrades);
        // One percent of 10,000 at 0.50 risk per share allows at most 200 shares, whatever the open turned out to be.
        Assert.InRange(trade.ShareQuantity, 1, 200);
    }

    [Fact]
    public void OverlappingControlCandidatesEachReceiveAnIndependentBookWithoutCapitalExpansion()
    {
        var snapshot = Snapshot(Candidate("first", 9.99m), Candidate("second", 9.99m));
        var outcomes = BacktestOpportunityShadowReplay.Replay(snapshot);
        Assert.Equal(2, outcomes.Length);
        Assert.All(outcomes, item => Assert.Equal("completed", item.Status));
        Assert.Equal(4, snapshot.Portfolio.MaxConcurrentPositions);
        Assert.All(outcomes, item => Assert.InRange(item.CompletedTrade!.ShareQuantity, 1, 250));
        var method = typeof(BacktestRunner).GetMethod("BuildUnifiedPortfolioResult", BindingFlags.Static | BindingFlags.NonPublic)!;
        var combined = (UnifiedPortfolioBacktestResult)method.Invoke(null,
            new object?[] { snapshot.Portfolio, snapshot.Strategies, snapshot.Candidates, snapshot.MarketBars, null, null, null, null })!;
        Assert.Single(combined.CompletedTrades);
        Assert.Contains("portfolio_overlapping_ticker_position", combined.AdmissionRejectionCounts!.Keys);
    }

    [Fact]
    public void MissingOpeningAndMiddleSliceRemainIncompletePerOpportunity()
    {
        var original = Snapshot(Candidate());
        foreach (var bars in new[] { ImmutableArray<OhlcvBar>.Empty,
            ImmutableArray.Create(Bar(5), Bar(10)), ImmutableArray.Create(Bar(0), Bar(10)) })
        {
            var result = Assert.Single(BacktestOpportunityShadowReplay.Replay(original with { MarketBars = bars }));
            Assert.Equal("incomplete", result.Status);
            Assert.Null(result.CompletedTrade);
            Assert.NotEmpty(result.ExecutionFailures);
        }
    }

    [Fact]
    public void OriginalEntryGateVetoIsRetainedWithoutInventingZeroProfit()
    {
        var snapshot = Snapshot(Candidate());
        snapshot = snapshot with { EntryEligibility = [snapshot.EntryEligibility[0] with { RegimeEligible = false }] };
        var outcome = Assert.Single(BacktestOpportunityShadowReplay.Replay(snapshot));
        Assert.Equal("admission_rejected", outcome.Status);
        Assert.Null(outcome.CompletedTrade);
        Assert.Contains("portfolio_market_regime_off_on_entry_day", outcome.AdmissionRejectionCounts.Keys);
    }

    [Fact]
    public void OpeningIocNonFillIsRetainedSeparatelyFromAZeroProfitTrade()
    {
        var snapshot = Snapshot(Candidate());
        snapshot = snapshot with { Portfolio = snapshot.Portfolio with { MaxBarParticipationPct = 5 },
            MarketBars = snapshot.MarketBars.SetItem(0, Bar(0, volume: 1)) };
        var outcome = Assert.Single(BacktestOpportunityShadowReplay.Replay(snapshot));
        Assert.Equal("not_filled", outcome.Status);
        Assert.Null(outcome.CompletedTrade);
        Assert.Empty(outcome.ExecutionFailures);
        Assert.Equal(0, outcome.EntryFilledQuantity);
    }

    [Fact]
    public void SnapshotFreezesAllCandidatesAndRequiredSliceArraysBeforePortfolioSelection()
    {
        var candidate = Candidate();
        var mutableSlices = candidate.ExecutionReplay!.RequiredSliceStarts.ToArray();
        candidate = candidate with { ExecutionReplay = candidate.ExecutionReplay with { RequiredSliceStarts = mutableSlices } };
        var method = typeof(BacktestRunner).GetMethod("CaptureOpportunities", BindingFlags.Static | BindingFlags.NonPublic)!;
        var snapshot = (BacktestOpportunitySnapshot)method.Invoke(null, new object?[] {
            "fixture", Portfolio(), new[] { Strategy() }, new[] { candidate, Candidate("second") },
            new[] { Bar(0), Bar(5), Bar(10) }, null, null })!;
        mutableSlices[0] = Start.AddYears(1);
        Assert.Equal(2, snapshot.Candidates.Length);
        Assert.Equal(Start, snapshot.Candidates[0].ExecutionReplay!.RequiredSliceStarts[0]);
        Assert.Equal(2, snapshot.EntryEligibility.Length);
    }

    [Fact]
    public void OptionalEvidenceSinkDoesNotChangeOutcomesAndUsesSeparateAuditScope()
    {
        var snapshot = Snapshot(Candidate());
        var baseline = BacktestOpportunityShadowReplay.Replay(snapshot);
        var sink = new CollectingSink();
        var observed = BacktestOpportunityShadowReplay.Replay(snapshot, sink);
        Assert.Equal(baseline[0].CompletedTrade, observed[0].CompletedTrade);
        Assert.Equal(baseline[0].Status, observed[0].Status);
        Assert.All(sink.Events, item => Assert.Equal("independent_opportunity_shadow", item.EvidenceScope));
    }

    private sealed class CollectingSink : IExecutionEventSink {
        public List<ExecutionEvent> Events { get; } = [];
        public void Append(ExecutionEvent item) => Events.Add(item);
    }
}

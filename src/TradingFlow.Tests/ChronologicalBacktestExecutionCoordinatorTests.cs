using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Execution;
using Xunit;

namespace TradingFlow.Tests;

public sealed class ChronologicalBacktestExecutionCoordinatorTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 4, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PartialEntryAndExitRemainders_ProgressAcrossRealBars()
    {
        var portfolio = Portfolio(maxParticipationPct: 10m);
        var strategy = Strategy();
        var evidence = new CollectingExecutionSink();
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            portfolio,
            [
                Bar(0, 10m, 1_000m),
                Bar(5, 10.10m, 1_000m),
                Bar(10, 10.20m, 1_000m),
                Bar(15, 12m, 3_000m),
                Bar(20, 11.80m, 600m),
                Bar(25, 11.60m, 2_000m)
            ], executionEvidence: evidence);

        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(Candidate("one", 0, 15), strategy, 250),
            out var rejection));
        Assert.Null(rejection);

        coordinator.Complete();

        var trade = Assert.Single(coordinator.CompletedTrades);
        Assert.Empty(coordinator.Failures);
        Assert.Equal(250, trade.RequestedShareQuantity);
        Assert.Equal(250, trade.ShareQuantity);
        Assert.Equal("filled", trade.EntryFillStatus);
        Assert.Equal(10.08m, trade.EntryPrice);
        Assert.Equal(12m, trade.ExitPrice);
        Assert.Equal(Start.AddMinutes(5), trade.EntryTimestamp);
        Assert.Equal(Start.AddMinutes(20), trade.ExitTimestamp);
        Assert.Equal(0m, trade.Fees);
        Assert.NotEmpty(evidence.Events);
        Assert.All(evidence.Events, item => Assert.Equal("portfolio_execution", item.EvidenceScope));
        Assert.All(evidence.Events, item => Assert.Equal("one", item.ReferenceId));
        Assert.Contains(evidence.Events, item => item.EvidenceJson is not null &&
            item.EvidenceJson.Contains("LastFillQuantity", StringComparison.Ordinal));
    }

    private sealed class CollectingExecutionSink : IExecutionEventSink
    {
        public List<ExecutionEvent> Events { get; } = [];
        public void Append(ExecutionEvent item) => Events.Add(item);
    }

    [Fact]
    public void CompetingOrders_ShareEachSymbolBarLiquidity_AndReplayDeterministically()
    {
        var bars = new[]
        {
            Bar(0, 10m, 1_000m),
            Bar(5, 11m, 1_000m),
            Bar(10, 12m, 2_000m)
        };
        var first = ExecuteInOrder(bars, "a", "b");
        var shuffled = ExecuteInOrder(bars.Reverse().ToArray(), "b", "a");

        Assert.Equal(first, shuffled);
        Assert.Collection(
            first,
            trade =>
            {
                Assert.Equal("Strategy a", trade.StrategyName);
                Assert.Equal(10m, trade.EntryPrice);
            },
            trade =>
            {
                Assert.Equal("Strategy b", trade.StrategyName);
                Assert.Equal(10.75m, trade.EntryPrice);
            });
    }

    [Fact]
    public void ZeroLiquidityUntilPlannedExit_ProducesNoFillFailure()
    {
        var candidate = Candidate("no-fill", 0, 10);
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            Portfolio(maxParticipationPct: 10m),
            [Bar(0, 10m, 0m), Bar(5, 10m, 0m), Bar(10, 9m, 1_000m)]);
        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(candidate, Strategy(), 100),
            out _));

        coordinator.Complete();

        Assert.Empty(coordinator.CompletedTrades);
        var failure = Assert.Single(coordinator.Failures);
        Assert.Equal(candidate.CandidateId, failure.CandidateId);
        Assert.Equal(candidate.Ticker, failure.Ticker);
        Assert.Equal("entry_not_filled_before_planned_exit", failure.Reason);
    }

    [Fact]
    public void CostsAndParticipationImpact_AreAppliedToBothSides()
    {
        var portfolio = Portfolio(maxParticipationPct: 10m) with
        {
            FixedBuyFee = 1m,
            FixedSellFee = 2m,
            ImpactBpsAtMaxParticipation = 20m
        };
        var evidence = new CollectingExecutionSink();
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            portfolio,
            [Bar(0, 10m, 1_000m), Bar(5, 12m, 1_000m)],
            executionEvidence: evidence);
        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(Candidate("impact", 0, 5), Strategy(), 100),
            out _));

        coordinator.Complete();

        var trade = Assert.Single(coordinator.CompletedTrades);
        Assert.Equal(10.02m, trade.EntryPrice);
        Assert.Equal(12m, trade.ExitPrice);
        Assert.Equal(198m, trade.GrossProfit);
        Assert.Equal(3m, trade.Fees);
        var evidencedFees = evidence.Events
            .Where(item => item.EvidenceJson is not null)
            .Select(item => JsonSerializer.Deserialize<SimulatedExecutionEvent>(item.EvidenceJson!))
            .Where(item => item is not null)
            .Sum(item => item!.Fees);
        Assert.Equal(trade.Fees, evidencedFees);
        Assert.Equal(195m, trade.NetProfit);
        Assert.Equal(10m, trade.EntryEstimatedParticipationPct);
    }

    [Fact]
    public void ShortTrade_AsymmetricFixedFeesReconcileWithExecutionEvidence()
    {
        var portfolio = Portfolio(maxParticipationPct: 100m) with
        {
            FixedBuyFee = 1m,
            FixedSellFee = 2m
        };
        var evidence = new CollectingExecutionSink();
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            portfolio,
            [Bar(0, 10m, 1_000m), Bar(5, 8m, 1_000m)],
            executionEvidence: evidence);
        var candidate = Candidate("short-fees", 0, 5) with
        {
            Direction = "short",
            StopLossPrice = 11m,
            TakeProfitPrice = 8m,
            ExitPrice = 8m,
            ExitReason = "take_profit"
        };
        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(candidate, Strategy(), 100),
            out _));

        coordinator.Complete();

        var trade = Assert.Single(coordinator.CompletedTrades);
        var evidencedFees = evidence.Events
            .Where(item => item.EvidenceJson is not null)
            .Select(item => JsonSerializer.Deserialize<SimulatedExecutionEvent>(item.EvidenceJson!))
            .Where(item => item is not null)
            .Sum(item => item!.Fees);
        Assert.Equal(3m, trade.Fees);
        Assert.Equal(trade.Fees, evidencedFees);
    }

    [Fact]
    public void MissingCandidateIdentity_IsRejectedBeforeCoordinatorStateChanges()
    {
        var evidence = new CollectingExecutionSink();
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            Portfolio(maxParticipationPct: 10m),
            [Bar(0, 10m, 1_000m), Bar(5, 12m, 1_000m)],
            executionEvidence: evidence);

        Assert.False(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(Candidate(String.Empty, 0, 5), Strategy(), 100),
            out var rejection));

        Assert.Equal("candidate_id_missing", rejection);
        Assert.Empty(coordinator.ActiveExecutions);
        Assert.Empty(coordinator.CompletedTrades);
        Assert.Empty(coordinator.Failures);
        Assert.Empty(evidence.Events);
    }

    [Fact]
    public void CandidateIdentityHistory_FailsClosedAtConfiguredLimit()
    {
        var portfolio = Portfolio(maxParticipationPct: 10m) with
        {
            MaxExecutionOrderHistory = 1
        };
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            portfolio,
            [Bar(0, 10m, 1_000m), Bar(5, 12m, 1_000m)]);
        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(Candidate("first", 0, 5), Strategy(), 10),
            out _));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            coordinator.TrySchedule(
                new ChronologicalExecutionPlan(Candidate("second", 0, 5), Strategy(), 10),
                out _));

        Assert.Contains("max_execution_order_history", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialExitAtEndOfData_PreservesOpenExposureAndTerminalEvidence()
    {
        var evidence = new CollectingExecutionSink();
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            Portfolio(maxParticipationPct: 10m),
            [Bar(0, 10m, 1_000m), Bar(5, 11m, 400m)],
            executionEvidence: evidence);
        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(
                Candidate("partial-open", 0, 5) with { ExitReason = "time_exit", ExitPrice = 11m },
                Strategy(),
                100),
            out _));

        coordinator.Complete();

        Assert.Empty(coordinator.CompletedTrades);
        var failure = Assert.Single(coordinator.Failures);
        Assert.Equal("partial-open", failure.CandidateId);
        Assert.Equal(100, failure.EntryFilledQuantity);
        Assert.Equal(40, failure.ExitFilledQuantity);
        Assert.Equal(60, failure.OpenSignedQuantity);
        Assert.Equal(11m, failure.LastMarkedPrice);
        var terminal = Assert.Single(evidence.Events, item =>
            item.ReferenceId == "partial-open" &&
            item.Message.Contains("ended incomplete", StringComparison.Ordinal));
        Assert.Equal("portfolio_execution", terminal.EvidenceScope);
        Assert.NotNull(terminal.EvidenceJson);
    }

    [Fact]
    public void BarCannotAffectPortfolioBeforeItsCompletedAvailabilityTime()
    {
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            Portfolio(maxParticipationPct: 10m),
            [Bar(0, 10m, 1_000m), Bar(5, 12m, 1_000m)]);
        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(Candidate("timing", 0, 5), Strategy(), 100),
            out _));

        coordinator.AdvanceThrough(Start.AddMinutes(4));
        Assert.Empty(coordinator.CompletedTrades);

        coordinator.AdvanceThrough(Start.AddMinutes(5));
        Assert.Empty(coordinator.CompletedTrades);

        coordinator.Complete();
        Assert.Single(coordinator.CompletedTrades);
    }

    [Fact]
    public void ConcurrentStrategiesWithDifferentExecutionTimeframes_FailClosedWithoutNormalization()
    {
        var fiveMinute = Strategy();
        var oneMinute = Strategy() with
        {
            StrategyId = "strategy.one-minute",
            StrategyName = "One minute",
            Execution = new ExecutionRules("1m", 0m)
        };
        var oneMinuteCandidate = Candidate("one-minute", 0, 5) with
        {
            StrategyId = oneMinute.StrategyId,
            StrategyName = oneMinute.StrategyName
        };
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            Portfolio(maxParticipationPct: 10m),
            [
                Bar(0, 10m, 1_000m),
                new OhlcvBar("TEST", Start, "1m", 10m, 10m, 10m, 10m, 1_000m),
                Bar(5, 12m, 1_000m)
            ]);

        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(Candidate("five-minute", 0, 5), fiveMinute, 100),
            out _));
        Assert.False(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(oneMinuteCandidate, oneMinute, 100),
            out var rejection));

        Assert.Equal("mixed_execution_timeframe_requires_explicit_normalization", rejection);
    }

    [Fact]
    public void FiveMinuteStrategy_DoesNotConsumeCoTimestampedOneMinuteLiquidity()
    {
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            Portfolio(maxParticipationPct: 10m),
            [
                new OhlcvBar("TEST", Start, "1m", 99m, 99m, 99m, 99m, 10_000m),
                Bar(0, 10m, 1_000m),
                Bar(5, 12m, 1_000m)
            ]);
        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(Candidate("five-minute-only", 0, 5), Strategy(), 100),
            out _));

        coordinator.Complete();

        var trade = Assert.Single(coordinator.CompletedTrades);
        Assert.Empty(coordinator.Failures);
        Assert.Equal(10m, trade.EntryPrice);
        Assert.Equal(12m, trade.ExitPrice);
        Assert.Equal(10m, trade.EntryEstimatedParticipationPct);
    }

    [Fact]
    public void CommandTimestampInsideExecutionBar_CannotRetroactivelyConsumeThatBar()
    {
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            Portfolio(maxParticipationPct: 10m),
            [Bar(0, 10m, 1_000m), Bar(5, 12m, 1_000m)]);
        var candidate = Candidate("mid-bar", 0, 5) with
        {
            EntryTimestamp = Start.AddMinutes(2)
        };

        Assert.False(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(candidate, Strategy(), 100),
            out var rejection));

        Assert.Equal("entry_execution_bar_missing", rejection);
    }

    [Fact]
    public void ProviderDelayedCompletedBar_IsNotConsumedBeforeKnownAtUtc()
    {
        var delayedAvailability = Start.AddMinutes(7);
        var simulator = new RecordingExecutionSimulator();
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            Portfolio(maxParticipationPct: 10m),
            [
                Bar(0, 10m, 1_000m) with { KnownAtUtc = delayedAvailability },
                Bar(5, 12m, 1_000m) with { KnownAtUtc = Start.AddMinutes(10) }
            ],
            simulator);
        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(Candidate("delayed", 0, 5), Strategy(), 100),
            out _));

        coordinator.AdvanceThrough(Start.AddMinutes(5));
        Assert.Empty(coordinator.CompletedTrades);
        Assert.Single(coordinator.ActiveExecutions);
        Assert.Empty(simulator.Requests);

        coordinator.AdvanceThrough(delayedAvailability);
        Assert.Empty(coordinator.CompletedTrades);
        Assert.Single(coordinator.ActiveExecutions);
        var request = Assert.Single(simulator.Requests);
        Assert.Equal(delayedAvailability, request.Market.AvailableAtUtc);
        Assert.Equal(Start, Assert.Single(request.Commands).KnownAtUtc);

        coordinator.Complete();
        Assert.Single(coordinator.CompletedTrades);
    }

    [Fact]
    public void ProviderBarsBecomingAvailableOutOfEventTimeOrder_AreRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            new ChronologicalBacktestExecutionCoordinator(
                Portfolio(maxParticipationPct: 10m),
                [
                    Bar(0, 10m, 1_000m) with { KnownAtUtc = Start.AddMinutes(20) },
                    Bar(5, 11m, 1_000m) with { KnownAtUtc = Start.AddMinutes(10) }
                ]));

        Assert.Contains("out of event-time order", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SequentialBarsWithSameAvailability_AreProcessedInEventTimeOrder()
    {
        var sharedAvailability = Start.AddMinutes(10);
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            Portfolio(maxParticipationPct: 100m),
            [
                Bar(0, 10m, 1_000m) with { KnownAtUtc = sharedAvailability },
                Bar(5, 12m, 1_000m) with { KnownAtUtc = sharedAvailability }
            ]);
        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(Candidate("shared-availability", 0, 5), Strategy(), 100),
            out _));

        coordinator.Complete();

        var trade = Assert.Single(coordinator.CompletedTrades);
        Assert.Equal(10m, trade.EntryPrice);
        Assert.Equal(12m, trade.ExitPrice);
    }

    [Fact]
    public void ProtectiveTarget_IsInstalledAfterEntryFill_AndCannotUseEntryBarHigh()
    {
        var simulator = new RecordingExecutionSimulator();
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            Portfolio(maxParticipationPct: 100m),
            [
                RangeBar(0, 10m, 13m, 8m, 10m),
                RangeBar(5, 10m, 10.50m, 9.50m, 10m),
                RangeBar(10, 10m, 12.50m, 9.75m, 12m)
            ],
            simulator);

        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(Candidate("causal-target", 0, 10), Strategy(), 100),
            out _));

        coordinator.Complete();

        var trade = Assert.Single(coordinator.CompletedTrades);
        Assert.Empty(coordinator.Failures);
        Assert.Equal(12m, trade.ExitPrice);
        Assert.Equal(Start.AddMinutes(15), trade.ExitTimestamp);
        Assert.Equal("take_profit", trade.ExitReason);

        var targetCommand = simulator.Requests
            .SelectMany(request => request.Commands)
            .Single(command => command.ClientOrderId.StartsWith("target-", StringComparison.Ordinal));
        Assert.Equal(Start.AddMinutes(5), targetCommand.KnownAtUtc);
        Assert.Equal(SimulatedOrderType.Limit, targetCommand.OrderType);
        Assert.Equal(12m, targetCommand.LimitPrice);
        Assert.DoesNotContain(
            simulator.Requests.SelectMany(request => request.Commands),
            command => command.ClientOrderId.StartsWith("exit-", StringComparison.Ordinal));
    }

    [Fact]
    public void ProtectiveStop_FillsAtRestingStop_NotAtTriggerBarOpen()
    {
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            Portfolio(maxParticipationPct: 100m),
            [
                RangeBar(0, 10m, 10.25m, 9.75m, 10m),
                RangeBar(5, 10m, 10.25m, 8m, 8.50m)
            ]);
        var candidate = Candidate("causal-stop", 0, 5) with
        {
            ExitPrice = 9m,
            ExitReason = "stop_loss"
        };
        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(candidate, Strategy(), 100),
            out _));

        coordinator.Complete();

        var trade = Assert.Single(coordinator.CompletedTrades);
        Assert.Empty(coordinator.Failures);
        Assert.Equal(9m, trade.ExitPrice);
        Assert.Equal(Start.AddMinutes(10), trade.ExitTimestamp);
        Assert.Equal("stop_loss", trade.ExitReason);
    }

    [Fact]
    public void CompletedBarTouchingStopAndTarget_ResolvesAdverseStopFirst()
    {
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            Portfolio(maxParticipationPct: 100m),
            [
                RangeBar(0, 10m, 10.25m, 9.75m, 10m),
                RangeBar(5, 10m, 13m, 8m, 10m)
            ]);
        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(Candidate("ambiguous-path", 0, 5), Strategy(), 100),
            out _));

        coordinator.Complete();

        var trade = Assert.Single(coordinator.CompletedTrades);
        Assert.Equal(9m, trade.ExitPrice);
        Assert.Equal("stop_loss", trade.ExitReason);
    }

    [Fact]
    public void MixedStrategySlippagePolicies_FailClosedInsteadOfChangingExistingFills()
    {
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            Portfolio(maxParticipationPct: 100m),
            [Bar(0, 10m, 1_000m), Bar(5, 12m, 1_000m)]);
        var first = Strategy();
        var second = Strategy() with
        {
            StrategyId = "strategy.different-slippage",
            StrategyName = "Different slippage",
            Execution = new ExecutionRules("5m", 5m)
        };
        var secondCandidate = Candidate("different-slippage", 0, 5) with
        {
            StrategyId = second.StrategyId,
            StrategyName = second.StrategyName
        };

        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(Candidate("baseline", 0, 5), first, 100),
            out _));
        Assert.False(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(secondCandidate, second, 100),
            out var rejection));

        Assert.Equal("mixed_execution_slippage_policy_requires_separate_run", rejection);
        coordinator.Complete();
        var baselineTrade = Assert.Single(coordinator.CompletedTrades);
        Assert.Equal(10m, baselineTrade.EntryPrice);
        Assert.Equal(12m, baselineTrade.ExitPrice);
    }

    [Fact]
    public void DifferentSymbolsMayUseIndependentSlippagePoliciesWithoutCrossContamination()
    {
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            Portfolio(maxParticipationPct: 100m),
            [
                Bar(0, 10m, 1_000m),
                Bar(5, 12m, 1_000m),
                Bar(0, 10m, 1_000m) with { Ticker = "OTHER" },
                Bar(5, 12m, 1_000m) with { Ticker = "OTHER" }
            ]);
        var baseline = Strategy();
        var frictional = Strategy() with
        {
            StrategyId = "strategy.frictional",
            StrategyName = "Frictional",
            Execution = new ExecutionRules("5m", 5m)
        };
        var otherCandidate = Candidate("other", 0, 5) with
        {
            Ticker = "OTHER",
            StrategyId = frictional.StrategyId,
            StrategyName = frictional.StrategyName
        };

        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(Candidate("baseline-independent", 0, 5), baseline, 100),
            out _));
        Assert.True(coordinator.TrySchedule(
            new ChronologicalExecutionPlan(otherCandidate, frictional, 100),
            out _));

        coordinator.Complete();

        var baselineTrade = Assert.Single(coordinator.CompletedTrades, trade => trade.Ticker == "TEST");
        var frictionalTrade = Assert.Single(coordinator.CompletedTrades, trade => trade.Ticker == "OTHER");
        Assert.Equal(10m, baselineTrade.EntryPrice);
        Assert.True(frictionalTrade.EntryPrice > baselineTrade.EntryPrice);
    }

    private static IReadOnlyList<BacktestTrade> ExecuteInOrder(
        IReadOnlyCollection<OhlcvBar> bars,
        params string[] identities)
    {
        var coordinator = new ChronologicalBacktestExecutionCoordinator(
            Portfolio(maxParticipationPct: 10m),
            bars);
        foreach (var identity in identities)
        {
            Assert.True(coordinator.TrySchedule(
                new ChronologicalExecutionPlan(
                    Candidate(identity, 0, 10) with { StrategyName = $"Strategy {identity}" },
                    Strategy() with
                    {
                        StrategyId = $"strategy.{identity}",
                        StrategyName = $"Strategy {identity}"
                    },
                    80),
                out _));
        }

        coordinator.Complete();
        Assert.Empty(coordinator.Failures);
        return coordinator.CompletedTrades;
    }

    private static PortfolioConfig Portfolio(decimal maxParticipationPct) => new(
        StartingCapital: 100_000m,
        AccountRiskBudgetPct: 1m,
        MaxPositionNotionalPct: 100m,
        MaxConcurrentPositions: 10,
        FixedBuyFee: 0m,
        FixedSellFee: 0m,
        MaxOpenTradesPerTicker: 10,
        PreventOverlappingTickerPositions: false,
        MaxBarParticipationPct: maxParticipationPct);

    private static BacktestCandidateTrade Candidate(
        string identity,
        int entryMinute,
        int exitMinute) => new(
        "TEST",
        $"Strategy {identity}",
        "long",
        Start.AddMinutes(entryMinute),
        10m,
        9m,
        12m,
        Start.AddMinutes(exitMinute),
        12m,
        "take_profit",
        1m,
        StrategyId: $"strategy.{identity}",
        CandidateId: identity);

    private static OhlcvBar Bar(int minute, decimal price, decimal volume) => new(
        "TEST",
        Start.AddMinutes(minute),
        "5m",
        price,
        price,
        price,
        price,
        volume);

    private static OhlcvBar RangeBar(
        int minute,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal volume = 10_000m) => new(
        "TEST",
        Start.AddMinutes(minute),
        "5m",
        open,
        high,
        low,
        close,
        volume);

    private static StrategyDefinition Strategy() => new(
        "strategy.test",
        "Strategy test",
        "test",
        1,
        "5m",
        "long",
        new EntryRules(
            "test",
            0m,
            0m,
            100m,
            "none",
            "none",
            false,
            false,
            false,
            false,
            false,
            false,
            0m,
            0,
            0),
        new ConfluenceRules(false, "5m", 20, "none"),
        new ExitRules(1m, 2m, 1m, false, 1m, 1m, false, false, false, 0),
        new ExecutionRules("5m", 0m),
        new SessionRules("America/New_York", 0, 0, 0));

    private sealed class RecordingExecutionSimulator : IDeterministicExecutionSimulator
    {
        private readonly DeterministicExecutionSimulator _inner = new();

        public List<ExecutionStepRequest> Requests { get; } = [];

        public ExecutionStepResult Advance(ExecutionStepRequest request)
        {
            Requests.Add(request);
            return _inner.Advance(request);
        }
    }
}

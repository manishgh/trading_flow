using System.Reflection;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

/// <summary>
/// Characterization (freeze) tests for the sign-sensitive core of the long/short exit engine.
/// These lock the directional risk math so the two mirror implementations
/// (CreateCandidate long / CreateShortCandidate) can never silently diverge, and so any future
/// attempt to unify them is provably behavior-preserving. See docs/architecture-and-review.md
/// increment B: the exit loops are intentionally kept as mirrors; this is their guardrail.
/// </summary>
public sealed class BacktestSimulationCharacterizationTests
{
    private const decimal EntryPrice = 100m;

    [Fact]
    public void ResolveInitialRisk_LongAndShort_AreExactMirrorsAcrossEntry()
    {
        var strategy = AtrStopStrategy();
        var signal = LongSignal();
        var bars = new[] { Bar("2026-06-01T13:35:00Z", 100m, 101m, 99m, 100m) };
        var snapshots = new[] { Snapshot("2026-06-01T13:35:00Z") };

        var (stopLong, distanceLong) = InvokeResolveInitialRisk(strategy, signal, "long", bars, snapshots);
        var (stopShort, distanceShort) = InvokeResolveInitialRisk(strategy, signal, "short", bars, snapshots);

        // Same stop distance, mirrored around entry, correct sign for each side.
        Assert.Equal(distanceLong, distanceShort);
        Assert.True(distanceLong > 0m);
        Assert.Equal(EntryPrice - distanceLong, stopLong);
        Assert.Equal(EntryPrice + distanceLong, stopShort);
        Assert.True(stopLong < EntryPrice, "long stop must sit below entry");
        Assert.True(stopShort > EntryPrice, "short stop must sit above entry");
    }

    [Fact]
    public void ResolveTakeProfitPrice_LongAndShort_AreExactMirrorsAcrossEntry()
    {
        var strategy = AtrStopStrategy();
        const decimal stopDistance = 2m;
        var targetR = strategy.ExitRules.TargetRMultiple;

        var targetLong = InvokeResolveTakeProfit(strategy, "long", stopDistance);
        var targetShort = InvokeResolveTakeProfit(strategy, "short", stopDistance);

        Assert.Equal(EntryPrice + (stopDistance * targetR), targetLong);
        Assert.Equal(EntryPrice - (stopDistance * targetR), targetShort);
        Assert.True(targetLong > EntryPrice, "long target must sit above entry");
        Assert.True(targetShort < EntryPrice, "short target must sit below entry");
    }

    [Fact]
    public void LongCandidateWithoutAnEligibleExitBar_EmitsNoHypotheticalFill()
    {
        var strategy = AtrStopStrategy();
        var signal = LongSignal();
        var bars = new[]
        {
            Bar("2026-06-01T13:30:00Z", 100m, 101m, 99m, 100m),
            Bar("2026-06-01T13:35:00Z", 100m, 101m, 99m, 100m)
        };
        var snapshots = bars.Select(bar => Snapshot(bar.Timestamp.ToString("O"))).ToArray();
        var sink = new CollectingExecutionSink();
        var candidate = Assert.IsType<(BacktestCandidateTrade Candidate, int ExitIndex)>(InvokeCreateCandidate(
            strategy, signal, BuildOrderPlan(strategy, "long", 98m, 2m), bars, snapshots, sink));
        Assert.Null(candidate.Candidate.ExitTimestamp);
        Assert.Null(candidate.Candidate.ExitPrice);
        Assert.DoesNotContain(sink.Events, item => item.State == ExecutionState.OrderFilled);
    }

    [Fact]
    public void ShortCandidateEndOfData_CannotFabricateAClosedTrade()
    {
        var baseStrategy = AtrStopStrategy();
        var strategy = baseStrategy with
        {
            Direction = "short",
            EntryRules = baseStrategy.EntryRules with { EnableShort = true }
        };
        var signal = LongSignal();
        var bars = new[]
        {
            Bar("2026-06-01T13:30:00Z", 100m, 101m, 99m, 100m),
            Bar("2026-06-01T13:35:00Z", 100m, 101m, 99m, 100m),
            Bar("2026-06-01T13:40:00Z", 100m, 101m, 99m, 100m)
        };
        var snapshots = bars.Select(bar => Snapshot(bar.Timestamp.ToString("O"))).ToArray();
        var sink = new CollectingExecutionSink();
        var candidate=Assert.IsType<(BacktestCandidateTrade Candidate,int ExitIndex)>(InvokeCreateCandidate(
            strategy,signal,BuildOrderPlan(strategy,"short",102m,2m),bars,snapshots,sink));
        Assert.Null(candidate.Candidate.ExitTimestamp);
        Assert.Null(candidate.Candidate.ExitPrice);
        Assert.DoesNotContain(sink.Events,item=>item.State==ExecutionState.PositionClosed);
        Assert.All(sink.Events, item => Assert.Equal("candidate-lifecycle", item.ReferenceId));
        Assert.Equal(sink.Events.OrderBy(item => item.Timestamp), sink.Events);
    }

    // Published no-stop rules (Connors RSI-2, snapback): neither a deep fall nor a spike closes the position;
    // only the strategy's own exit does, and the recorded stop/target prices state that no such orders existed.
    [Fact]
    public void NoStopNoTarget_LongIgnoresPriceExtremesUntilItsOwnExit()
    {
        var original = AtrStopStrategy();
        var strategy = original with { ExitRules = original.ExitRules with {
            InitialStopMode = ExitRules.NoStop, ProfitTargetMode = ExitRules.NoProfitTarget, MaxHoldBars = 2 } };
        var bars = new[] { Bar("2026-06-01T13:30:00Z", 100, 101, 99, 100), Bar("2026-06-01T13:35:00Z", 100, 101, 99, 100),
            Bar("2026-06-01T13:40:00Z", 100, 200, 50, 100), Bar("2026-06-01T13:45:00Z", 100, 101, 99, 100),
            Bar("2026-06-01T13:50:00Z", 101, 102, 100, 101) };
        var sink = new CollectingExecutionSink();

        var result = Assert.IsType<(BacktestCandidateTrade Candidate, int ExitIndex)>(InvokeCreateCandidate(strategy, LongSignal(),
            BuildOrderPlan(strategy, "long", 0m, 100m), bars, bars.Select(bar => Snapshot(bar.Timestamp.ToString("O"))).ToArray(), sink));

        Assert.Equal(0m, result.Candidate.StopLossPrice);
        Assert.Equal(0m, result.Candidate.TakeProfitPrice);
        Assert.Equal(ExitRules.NoStop, result.Candidate.InitialStopKind);
        Assert.DoesNotContain(sink.Events, item => item.State == ExecutionState.PositionClosed && item.Timestamp == bars[2].Timestamp);
        Assert.Contains("max_hold_bars", Assert.Single(sink.Events, item => item.State == ExecutionState.PositionClosed).Message);
    }

    // Published no-stop short rules (Connors RSI-2 short, snapback short): a spike far above entry does not close the
    // position; the completed close below SMA5 covers it at the next bar's open, the same timing as the long SMA5 exit.
    [Fact]
    public void NoStopNoTarget_ShortIgnoresSpikeAndCoversAfterCloseBelowSma5()
    {
        var original = AtrStopStrategy();
        var strategy = original with { Direction = "short", EntryRules = original.EntryRules with { EnableShort = true },
            ExitRules = original.ExitRules with { InitialStopMode = ExitRules.NoStop, ProfitTargetMode = ExitRules.NoProfitTarget,
                EnableAtrTrailingStop = false, MaxHoldBars = null, ExitOnCloseBelowSma5 = true, MinHoldBarsBeforeTechnicalExit = 0 } };
        var bars = new[] { Bar("2026-06-01T13:30:00Z", 100, 101, 99, 100), Bar("2026-06-01T13:35:00Z", 100, 101, 99, 100),
            Bar("2026-06-01T13:40:00Z", 100, 200, 50, 100), Bar("2026-06-01T13:45:00Z", 99, 99, 97, 98),
            Bar("2026-06-01T13:50:00Z", 97, 98, 96, 97) };
        var snapshots = bars.Select(bar => Snapshot(bar.Timestamp.ToString("O")) with
            { CurrentPrice = bar.Close, Sma5 = bar.Close == 98m ? 99m : bar.Close - 1m }).ToArray();
        var sink = new CollectingExecutionSink();

        var result = Assert.IsType<(BacktestCandidateTrade Candidate, int ExitIndex)>(InvokeCreateCandidate(strategy, LongSignal(),
            BuildOrderPlan(strategy, "short", 0m, 100m), bars, snapshots, sink));

        Assert.Equal(0m, result.Candidate.StopLossPrice);
        Assert.Equal(0m, result.Candidate.TakeProfitPrice);
        Assert.Equal("technical_exit_below_sma5", result.Candidate.ExitReason);
        Assert.Equal(bars[4].Timestamp, result.Candidate.ExitTimestamp);
        Assert.True(result.Candidate.ExitPrice >= 97m);
        Assert.DoesNotContain(sink.Events, item => item.State == ExecutionState.PositionClosed && item.Timestamp == bars[2].Timestamp);
    }

    // check_exits_from_fill_day: the fill bar's own close (bar 1 here) is the first exit check, so an SMA5 exit seen there
    // fills at the next bar's open; without the setting the first check is the following bar's close.
    [Theory]
    [InlineData("long", true, 2)]
    [InlineData("long", false, 3)]
    [InlineData("short", true, 2)]
    [InlineData("short", false, 3)]
    public void FillDayCheck_ExitsOnTheFillBarsCloseOnlyWhenSet(string direction, bool fromFillDay, int exitBar)
    {
        var original = AtrStopStrategy();
        var strategy = original with { Direction = direction, EntryRules = original.EntryRules with { EnableShort = direction == "short" },
            ExitRules = original.ExitRules with { InitialStopMode = ExitRules.NoStop, ProfitTargetMode = ExitRules.NoProfitTarget,
                EnableAtrTrailingStop = false, MaxHoldBars = null, MinHoldBarsBeforeTechnicalExit = 1,
                ExitOnCloseAboveSma5 = direction == "long", ExitOnCloseBelowSma5 = direction == "short", CheckExitsFromFillDay = fromFillDay } };
        var bars = new[] { Bar("2026-06-01T13:30:00Z", 100, 101, 99, 100), Bar("2026-06-01T13:35:00Z", 100, 101, 99, 100),
            Bar("2026-06-01T13:40:00Z", 100, 101, 99, 100), Bar("2026-06-01T13:45:00Z", 100, 101, 99, 100) };
        // Every completed close is on the exit side of SMA5 (above it for the long, below it for the short).
        var snapshots = bars.Select(bar => Snapshot(bar.Timestamp.ToString("O")) with
            { CurrentPrice = 100m, Sma5 = direction == "long" ? 99m : 101m }).ToArray();

        var result = Assert.IsType<(BacktestCandidateTrade Candidate, int ExitIndex)>(InvokeCreateCandidate(strategy, LongSignal(),
            BuildOrderPlan(strategy, direction, 0m, 100m), bars, snapshots, new CollectingExecutionSink()));

        Assert.Equal(direction == "long" ? "technical_exit_above_sma5" : "technical_exit_below_sma5", result.Candidate.ExitReason);
        Assert.Equal(bars[exitBar].Timestamp, result.Candidate.ExitTimestamp);
    }

    // With the fill-day check the fill bar is bar 1 of max_hold_bars, so a one-bar limit sells at the open after the fill bar.
    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public void FillDayCheck_CountsTheFillBarAsTheFirstHeldBar(string direction)
    {
        var original = AtrStopStrategy();
        var strategy = original with { Direction = direction, EntryRules = original.EntryRules with { EnableShort = direction == "short" },
            ExitRules = original.ExitRules with { InitialStopMode = ExitRules.NoStop, ProfitTargetMode = ExitRules.NoProfitTarget,
                EnableAtrTrailingStop = false, MaxHoldBars = 1, CheckExitsFromFillDay = true } };
        var bars = new[] { Bar("2026-06-01T13:30:00Z", 100, 101, 99, 100), Bar("2026-06-01T13:35:00Z", 100, 101, 99, 100),
            Bar("2026-06-01T13:40:00Z", 100.5m, 101, 99, 100), Bar("2026-06-01T13:45:00Z", 100, 101, 99, 100) };

        var result = Assert.IsType<(BacktestCandidateTrade Candidate, int ExitIndex)>(InvokeCreateCandidate(strategy, LongSignal(),
            BuildOrderPlan(strategy, direction, 0m, 100m), bars, bars.Select(bar => Snapshot(bar.Timestamp.ToString("O"))).ToArray(),
            new CollectingExecutionSink()));

        Assert.Equal("max_hold_bars", result.Candidate.ExitReason);
        Assert.Equal(bars[2].Timestamp, result.Candidate.ExitTimestamp);
    }

    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public void CompletedBarTimeoutUsesLaterOpen_ForBothDirections(string direction)
    {
        var original=AtrStopStrategy();var strategy=original with {Direction=direction,
            EntryRules=original.EntryRules with {EnableShort=direction=="short"},
            ExitRules=original.ExitRules with {MaxHoldBars=1}};
        var bars=new[]{Bar("2026-06-01T13:30:00Z",100,101,99,100),Bar("2026-06-01T13:35:00Z",100,101,99,100),
            Bar("2026-06-01T13:40:00Z",100,101,99,100),Bar("2026-06-01T13:45:00Z",100.5m,101,99,100)};
        var snapshots=bars.Select(bar=>Snapshot(bar.Timestamp.ToString("O"))).ToArray();
        var sink=new CollectingExecutionSink();
        var result=InvokeCreateCandidate(strategy,LongSignal(),BuildOrderPlan(strategy,direction,direction=="long"?98m:102m,2m),bars,snapshots,sink);
        Assert.NotNull(result);
        var closed=Assert.Single(sink.Events,item=>item.State==ExecutionState.PositionClosed);
        Assert.Contains("max_hold_bars",closed.Message);Assert.Equal(bars[3].Timestamp,closed.Timestamp);
        Assert.DoesNotContain(sink.Events,item=>item.State==ExecutionState.PositionClosed&&item.Timestamp==bars[2].Timestamp);
    }

    [Theory]
    [InlineData("long", "2026-06-01T13:50:00Z", "confirmed_vwap_failure")]
    [InlineData("short", "2026-06-01T13:50:00Z", "confirmed_vwap_reclaim")]
    [InlineData("long", "2026-06-01T14:01:00Z", null)]
    [InlineData("short", "2026-06-01T14:01:00Z", null)]
    public void ConfirmedVwapExitWithoutLaterSlice_RetainsOnlyIntentionKnownByCutoff(string direction,string knownAt,string? reason)
    {
        var original=AtrStopStrategy();
        var strategy=original with {Direction=direction,Timeframe="5m",Session=new SessionRules("America/New_York",0,0,0),
            Execution=original.Execution with {Timeframe="5m"},
            EntryRules=original.EntryRules with {EnableShort=direction=="short"},
            ExitRules=original.ExitRules with {EnableAtrTrailingStop=false,EnableConfirmedVwapExit=true,
                ConfirmedVwapExitBars=1,MinHoldBarsBeforeTechnicalExit=0}};
        var bars=new[]{Bar("2026-06-01T13:30:00Z",100,101,99,100),Bar("2026-06-01T13:35:00Z",100,101,99,100),
            Bar("2026-06-01T13:40:00Z",100,101,99,100) with {KnownAtUtc=DateTimeOffset.Parse(knownAt)}};
        var snapshots=bars.Select(bar=>Snapshot(bar.Timestamp.ToString("O")) with
            {CurrentPrice=100,Vwap=direction=="long"?101:99,Atr=1}).ToArray();
        var candidate=Assert.IsType<(BacktestCandidateTrade Candidate,int ExitIndex)>(InvokeCreateCandidate(strategy,LongSignal(),
            BuildOrderPlan(strategy,direction,direction=="long"?98:102,2),bars,snapshots,new CollectingExecutionSink())).Candidate;
        Assert.Null(candidate.ExitTimestamp);Assert.Null(candidate.ExitPrice);
        Assert.Equal(reason,candidate.PendingExitDecision?.Reason);
        if(reason is not null)Assert.Equal(DateTimeOffset.Parse(knownAt),candidate.PendingExitDecision!.KnownAtUtc);
    }

    [Theory]
    [InlineData("long", 95, 98, 94)]
    [InlineData("short", 105, 102, 106)]
    public void OpeningGapBeyondStop_IsKeptAndExitsAtNextOpenWhenPolicyIsSet(string direction, double open, double stop, double nextOpen)
    {
        var strategy = GapStrategy(direction, ExecutionRules.KeepAndExitNextOpen);
        var bars = GapBars((decimal)open, (decimal)nextOpen);
        var sink = new CollectingExecutionSink();

        var result = Assert.IsType<(BacktestCandidateTrade Candidate, int ExitIndex)>(InvokeCreateCandidate(strategy, LongSignal(),
            BuildOrderPlan(strategy, direction, (decimal)stop, 2m), bars, bars.Select(bar => Snapshot(bar.Timestamp.ToString("O"))).ToArray(), sink));

        Assert.Equal("gap_fill_through_structural_stop", result.Candidate.ExitReason);
        Assert.Equal(bars[2].Timestamp, result.Candidate.ExitTimestamp);
        Assert.Equal(2, result.ExitIndex);
        // Sizing and risk come from the prior close, which was known before the open.
        Assert.Equal(100m, result.Candidate.PlanningReferencePrice);
        Assert.Equal(2m, result.Candidate.StopDistance);
    }

    // A kept-gap strategy without a stop (Connors RSI-2 8.0.0 and its short mirror) reports the prior close as its
    // planning price for portfolio admission; rebuilding it from a stop price of 0 would give a negative short price.
    [Theory]
    [InlineData("long")]
    [InlineData("short")]
    public void NoStopKeptGap_ReportsThePriorCloseAsPlanningPrice(string direction)
    {
        var gap = GapStrategy(direction, ExecutionRules.KeepAndExitNextOpen);
        var strategy = gap with { ExitRules = gap.ExitRules with { InitialStopMode = ExitRules.NoStop,
            ProfitTargetMode = ExitRules.NoProfitTarget, EnableAtrTrailingStop = false, MaxHoldBars = 1 } };
        var bars = GapBars(101m, 102m);

        var result = Assert.IsType<(BacktestCandidateTrade Candidate, int ExitIndex)>(InvokeCreateCandidate(strategy, LongSignal(),
            BuildOrderPlan(strategy, direction, 0m, 100m), bars, bars.Select(bar => Snapshot(bar.Timestamp.ToString("O"))).ToArray(),
            new CollectingExecutionSink()));

        Assert.Equal(bars[0].Close, result.Candidate.PlanningReferencePrice);
        Assert.Equal(bars[0].Close, result.Candidate.StopDistance);
    }

    [Theory]
    [InlineData("long", 97.95, 98)]
    [InlineData("short", 102.05, 102)]
    public void OpeningGap_IsJudgedOnTheMarketOpenNotTheSlippedFill(string direction, double open, double stop)
    {
        var original = GapStrategy(direction, ExecutionRules.KeepAndExitNextOpen);
        var strategy = original with { Execution = original.Execution with { SlippageBps = 10m } };
        var bars = GapBars((decimal)open, 100m);

        var candidate = Assert.IsType<(BacktestCandidateTrade Candidate, int ExitIndex)>(InvokeCreateCandidate(strategy, LongSignal(),
            BuildOrderPlan(strategy, direction, (decimal)stop, 2m), bars, bars.Select(bar => Snapshot(bar.Timestamp.ToString("O"))).ToArray(),
            new CollectingExecutionSink())).Candidate;

        Assert.Equal("gap_fill_through_structural_stop", candidate.ExitReason);
    }

    [Theory]
    [InlineData("long", 95, 98)]
    [InlineData("short", 105, 102)]
    public void OpeningGapBeyondStop_KeepsOriginalDeletionWithoutPolicy(string direction, double open, double stop)
    {
        var strategy = GapStrategy(direction, null);
        var bars = GapBars((decimal)open, (decimal)open);

        Assert.Null(InvokeCreateCandidate(strategy, LongSignal(), BuildOrderPlan(strategy, direction, (decimal)stop, 2m), bars,
            bars.Select(bar => Snapshot(bar.Timestamp.ToString("O"))).ToArray(), new CollectingExecutionSink()));
    }

    [Theory]
    [InlineData("long", 98)]
    [InlineData("short", 102)]
    public void OpeningWithinStop_UsesPriorCloseForPlanButStillFillsAtTheOpen(string direction, double stop)
    {
        var strategy = GapStrategy(direction, ExecutionRules.KeepAndExitNextOpen);
        var bars = GapBars(100.5m, 100.5m);

        var candidate = Assert.IsType<(BacktestCandidateTrade Candidate, int ExitIndex)>(InvokeCreateCandidate(strategy, LongSignal(),
            BuildOrderPlan(strategy, direction, (decimal)stop, 2m), bars, bars.Select(bar => Snapshot(bar.Timestamp.ToString("O"))).ToArray(),
            new CollectingExecutionSink())).Candidate;

        Assert.Equal(100m, candidate.PlanningReferencePrice);
        Assert.NotEqual("gap_fill_through_structural_stop", candidate.ExitReason);
        Assert.InRange(candidate.EntryPrice, 100.4m, 100.6m);
    }

    private static StrategyDefinition GapStrategy(string direction, string? policy)
    {
        var original = AtrStopStrategy();
        return original with
        {
            Direction = direction,
            EntryRules = original.EntryRules with { EnableShort = direction == "short" },
            Execution = original.Execution with { OpeningGapPolicy = policy, SlippageBps = 0m }
        };
    }

    // Bar 0 is the completed bar before the decision; bar 1 opens at the tested price.
    private static OhlcvBar[] GapBars(decimal open, decimal nextOpen) =>
    [
        Bar("2026-06-01T13:30:00Z", 100m, 101m, 99m, 100m),
        Bar("2026-06-01T13:35:00Z", open, Math.Max(open, nextOpen) + 0.1m, Math.Min(open, nextOpen) - 0.1m, nextOpen),
        Bar("2026-06-01T13:40:00Z", nextOpen, nextOpen + 0.1m, nextOpen - 0.1m, nextOpen),
        Bar("2026-06-01T13:45:00Z", nextOpen, nextOpen + 0.1m, nextOpen - 0.1m, nextOpen)
    ];

    private static object? InvokeCreateCandidate(
        StrategyDefinition strategy,
        TradeSignal signal,
        StrategyOrderPlan orderPlan,
        OhlcvBar[] bars,
        IndicatorSnapshot[] snapshots,
        IExecutionEventSink sink)
    {
        var method = typeof(BacktestRunner).GetMethod(
            "CreateCandidate",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var runner = CreateRunner();
        return method!.Invoke(runner,
        [
            "candidate-lifecycle",
            BuildRunConfig(),
            strategy,
            signal,
            orderPlan,
            bars,
            snapshots,
            new ExecutionAuditor(20, sink, "candidate_hypothesis"),
            CancellationToken.None,
            1
        ]);
    }

    private static StrategyOrderPlan BuildOrderPlan(
        StrategyDefinition strategy,
        string direction,
        decimal stopPrice,
        decimal stopDistance)
    {
        var observedAt = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
        return new StrategyOrderPlan(
            strategy.StrategyId,
            strategy.StrategyName,
            direction,
            observedAt,
            observedAt,
            strategy.Execution.Timeframe,
            0,
            EntryPrice,
            stopPrice,
            strategy.ExitRules.TargetRMultiple,
            strategy.ExitRules.ProfitTargetMode,
            null,
            strategy.Execution.SlippageBps,
            observedAt.AddMinutes(5),
            observedAt.AddMinutes(-5));
    }

    private static BacktestRunner CreateRunner()
    {
        var reader = new SimpleYamlReader();
        var root = TestRepository.FindRoot();
        return new BacktestRunner(
            reader,
            new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), reader));
    }

    private static BacktestRunConfig BuildRunConfig() => new(
        "candidate-lifecycle",
        "backtest",
        new EngineConfig("tpl", 1, 10, 1, 30, true),
        new TimeWindowConfig("fixed", 1, DateTimeOffset.Parse("2026-06-01T13:30:00Z"), DateTimeOffset.Parse("2026-06-01T14:00:00Z")),
        ["POET"],
        "csv",
        ["5m"],
        "raw",
        "normalized",
        "results",
        "use_cache",
        new DerivedTimeframeConfig("5m"),
        new ValidationConfig(
            new OutOfSampleConfig(false, 0m),
            new WalkForwardConfig(false, 0, 0),
            new BenchmarkConfig(false, "SPY"),
            new DataQualityConfig(false, 0, 0, 100m),
            new BiasRiskConfig("test", null, "adjusted")),
        new ProviderConfig(new AlpacaProviderConfig("sip")),
        new PortfolioConfig(100_000m, 1m, 100m, 5, 0m, 0m, 1, true),
        new SignalSourceConfig("internal_candles", false, String.Empty, 300, 60),
        new ExecutionConfig("simulation", "none", true, false, "market", "day", "market"),
        new NewsConfig(false, "none", 60, -0.5m),
        new ScreenerConfig(false, "finviz", []),
        new ArtifactRetentionConfig("full"),
        []);

    private sealed class CollectingExecutionSink : IExecutionEventSink
    {
        public List<ExecutionEvent> Events { get; } = [];

        public void Append(ExecutionEvent item) => Events.Add(item);
    }

    private static (decimal Stop, decimal Distance) InvokeResolveInitialRisk(
        StrategyDefinition strategy,
        TradeSignal signal,
        string direction,
        OhlcvBar[] bars,
        IndicatorSnapshot[] snapshots)
    {
        var side = direction.Equals("short", StringComparison.OrdinalIgnoreCase)
            ? PlannedOrderSide.Short
            : PlannedOrderSide.Long;
        var result = new StrategyInitialStopResolver().Resolve(
            new StrategyInitialStopRequest(
                strategy,
                signal,
                side,
                EntryPrice,
                0,
                bars,
                snapshots));

        Assert.True(result.IsResolved, result.RejectionReason);
        Assert.NotNull(result.StopPrice);
        Assert.NotNull(result.StopDistance);
        return (result.StopPrice.Value, result.StopDistance.Value);
    }

    private static decimal InvokeResolveTakeProfit(
        StrategyDefinition strategy,
        string direction,
        decimal stopDistance)
    {
        var method = typeof(BacktestRunner).GetMethod(
            "ResolveTakeProfitPrice",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var observedAt = DateTimeOffset.Parse(
            "2026-06-01T13:35:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        var canonicalOrderPlan = new StrategyOrderPlan(
            strategy.StrategyId,
            strategy.StrategyName,
            direction,
            observedAt,
            observedAt,
            strategy.Execution.Timeframe,
            0,
            EntryPrice,
            direction.Equals("short", StringComparison.OrdinalIgnoreCase)
                ? EntryPrice + stopDistance
                : EntryPrice - stopDistance,
            strategy.ExitRules.TargetRMultiple,
            strategy.ExitRules.ProfitTargetMode,
            null,
            strategy.Execution.SlippageBps,
            observedAt.AddMinutes(5),
            observedAt.AddMinutes(-5));

        var raw = method!.Invoke(null, [canonicalOrderPlan, EntryPrice, stopDistance]);
        Assert.NotNull(raw);
        return (decimal)raw!;
    }

    // Forces the ATR stop mode and a non-VWAP profit target so both directions take the
    // symmetric price-arithmetic path (rather than a snapshot-derived VWAP anchor).
    private static StrategyDefinition AtrStopStrategy()
    {
        var baseStrategy = BuildStrategy();
        return baseStrategy with
        {
            ExitRules = baseStrategy.ExitRules with
            {
                InitialStopMode = "atr",
                ProfitTargetMode = "fixed_r"
            }
        };
    }

    private static StrategyDefinition BuildStrategy()
    {
        return new StrategyDefinition(
            "test.characterization",
            "Characterization",
            "test",
            1,
            "1d",
            "long",
            new EntryRules(
                "swing_reclaim",
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
                3m,
                8,
                6),
            new ConfluenceRules(false, "15m", 50, "none"),
            new ExitRules(3m, 3.5m, 6.5m, true, 3m, 2m, false, false, false, 2),
            new ExecutionRules("1h", 10m),
            new SessionRules("America/New_York", 0, 30, 30));
    }

    private static TradeSignal LongSignal()
    {
        return new TradeSignal(
            "POET",
            DateTimeOffset.Parse("2026-06-01T13:30:00Z", System.Globalization.CultureInfo.InvariantCulture),
            "1h",
            EntryPrice,
            100_000m,
            60m,
            1.0m,
            true,
            false,
            true,
            false,
            false,
            true,
            false,
            true,
            true,
            false,
            false,
            0.5m,
            true,
            true,
            true);
    }

    private static IndicatorSnapshot Snapshot(string timestamp)
    {
        return new IndicatorSnapshot(
            "POET",
            DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture),
            "5m",
            EntryPrice,
            1000m,
            EntryPrice,
            55m,
            1.0m,
            EntryPrice,
            EntryPrice,
            null,
            null,
            null,
            null,
            1.2m,
            0.1m,
            0.05m,
            0.05m);
    }

    private static OhlcvBar Bar(string timestamp, decimal open, decimal high, decimal low, decimal close)
    {
        return new OhlcvBar(
            "POET",
            DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture),
            "5m",
            open,
            high,
            low,
            close,
            100_000m);
    }
}

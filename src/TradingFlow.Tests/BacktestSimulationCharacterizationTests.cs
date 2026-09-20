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
        var result = InvokeCreateCandidate(
            strategy,
            signal,
            BuildOrderPlan(strategy, "long", 98m, 2m),
            bars,
            snapshots,
            sink);

        Assert.Null(result);
        Assert.DoesNotContain(sink.Events, item => item.State == ExecutionState.OrderFilled);
    }

    [Fact]
    public void ShortCandidateEndOfData_EmitsMatchingFillAndCloseLifecycle()
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
        var result = InvokeCreateCandidate(
            strategy,
            signal,
            BuildOrderPlan(strategy, "short", 102m, 2m),
            bars,
            snapshots,
            sink);

        Assert.NotNull(result);
        Assert.Single(sink.Events, item => item.State == ExecutionState.OrderFilled);
        var closed = Assert.Single(sink.Events, item => item.State == ExecutionState.PositionClosed);
        Assert.Contains("end_of_data", closed.Message, StringComparison.Ordinal);
        Assert.All(sink.Events, item => Assert.Equal("candidate-lifecycle", item.ReferenceId));
        Assert.Equal(sink.Events.OrderBy(item => item.Timestamp), sink.Events);
    }

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
            observedAt.AddMinutes(5));
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
            observedAt.AddMinutes(5));

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

using System.Reflection;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Risk;

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
        var snapshot = Snapshot("2026-06-01T13:35:00Z");
        const decimal stopDistance = 2m;
        var targetR = strategy.ExitRules.TargetRMultiple;

        var targetLong = InvokeResolveTakeProfit(strategy, "long", stopDistance, snapshot);
        var targetShort = InvokeResolveTakeProfit(strategy, "short", stopDistance, snapshot);

        Assert.Equal(EntryPrice + (stopDistance * targetR), targetLong);
        Assert.Equal(EntryPrice - (stopDistance * targetR), targetShort);
        Assert.True(targetLong > EntryPrice, "long target must sit above entry");
        Assert.True(targetShort < EntryPrice, "short target must sit below entry");
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
        decimal stopDistance,
        IndicatorSnapshot snapshot)
    {
        var method = typeof(BacktestRunner).GetMethod(
            "ResolveTakeProfitPrice",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var raw = method!.Invoke(null, [strategy, direction, EntryPrice, stopDistance, snapshot]);
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
            "5m",
            "long",
            new EntryRules(
                "ross_gap_go_bull_flag",
                2m,
                0m,
                100m,
                "vwap",
                "not_bearish",
                false,
                false,
                true,
                true,
                false,
                false,
                3m,
                5,
                8,
                6),
            new ConfluenceRules(false, "15m", 50, "none"),
            new ExitRules(3m, 3.5m, 6.5m, true, 3m, 2m, false, false, false, 2),
            new ExecutionRules("5m", 10m),
            new SessionRules("America/New_York", 1, 30, 30));
    }

    private static TradeSignal LongSignal()
    {
        return new TradeSignal(
            "POET",
            DateTimeOffset.Parse("2026-06-01T13:30:00Z", System.Globalization.CultureInfo.InvariantCulture),
            "5m",
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

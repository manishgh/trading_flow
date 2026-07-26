using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class CompletedBarExecutionPlannerTests
{
    private readonly CompletedBarExecutionPlanner planner = new();

    [Fact]
    public void Plan_BacktestConfirmation_UsesNextBarAsFill()
    {
        var strategy = CreateStrategy(
            new ExecutionConfirmationRules(
                true,
                3,
                ExecutionConfirmationRules.CloseAboveSetupClose,
                ExecutionConfirmationRules.Ema10AboveEma20,
                ExecutionConfirmationRules.MacdHistogramPositive,
                MinCloseLocationValue: 0.60m));
        var signal = CreateSignal("2026-06-01T13:30:00Z", 100m);
        var bars = new[]
        {
            Bar("2026-06-01T13:35:00Z", 100m, 103m, 99m, 102m),
            Bar("2026-06-01T13:40:00Z", 102m, 104m, 101m, 103m)
        };
        var snapshots = new[]
        {
            Snapshot(bars[0], ema10: 101m, ema20: 100m, histogram: 0.2m),
            Snapshot(bars[1], ema10: 102m, ema20: 100m, histogram: 0.3m)
        };

        var result = planner.Plan(Request(strategy, signal, bars, snapshots, true));

        Assert.True(result.IsReady);
        Assert.Equal(0, result.ConfirmationIndex);
        Assert.Equal(0, result.StopContextIndex);
        Assert.Equal(1, result.EntryIndex);
    }

    [Fact]
    public void Plan_ConfirmationOutsideBoundedWindow_IsRejected()
    {
        var strategy = CreateStrategy(
            new ExecutionConfirmationRules(
                true,
                1,
                ExecutionConfirmationRules.CloseAboveSetupClose,
                ExecutionConfirmationRules.NoFilter,
                ExecutionConfirmationRules.NoFilter));
        var signal = CreateSignal("2026-06-01T13:30:00Z", 100m);
        var bars = new[]
        {
            Bar("2026-06-01T13:35:00Z", 100m, 101m, 98m, 99m),
            Bar("2026-06-01T13:40:00Z", 99m, 103m, 99m, 102m),
            Bar("2026-06-01T13:45:00Z", 102m, 104m, 101m, 103m)
        };
        var snapshots = bars
            .Select(bar => Snapshot(bar, 101m, 100m, 0.2m))
            .ToArray();

        var result = planner.Plan(Request(strategy, signal, bars, snapshots, true));

        Assert.False(result.IsReady);
        Assert.StartsWith(
            "execution_confirmation_not_triggered",
            result.RejectionReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_LiveConfirmation_IsReadyWithoutFutureFillBar()
    {
        var strategy = CreateStrategy(
            new ExecutionConfirmationRules(
                true,
                2,
                ExecutionConfirmationRules.CloseAboveSetupClose,
                ExecutionConfirmationRules.NoFilter,
                ExecutionConfirmationRules.MacdHistogramPositive));
        var signal = CreateSignal("2026-06-01T13:30:00Z", 100m);
        var bars = new[]
        {
            Bar("2026-06-01T13:35:00Z", 100m, 103m, 99m, 102m)
        };
        var snapshots = new[]
        {
            Snapshot(bars[0], 101m, 100m, 0.2m)
        };

        var result = planner.Plan(Request(strategy, signal, bars, snapshots, false));

        Assert.True(result.IsReady);
        Assert.Equal(0, result.ConfirmationIndex);
        Assert.Equal(0, result.StopContextIndex);
        Assert.Null(result.EntryIndex);
    }

    [Fact]
    public void Plan_UnsupportedRule_FailsClosed()
    {
        var strategy = CreateStrategy(
            new ExecutionConfirmationRules(
                true,
                2,
                "guess_the_breakout",
                ExecutionConfirmationRules.NoFilter,
                ExecutionConfirmationRules.NoFilter));
        var signal = CreateSignal("2026-06-01T13:30:00Z", 100m);
        var bars = new[]
        {
            Bar("2026-06-01T13:35:00Z", 100m, 103m, 99m, 102m)
        };
        var snapshots = new[]
        {
            Snapshot(bars[0], 101m, 100m, 0.2m)
        };

        var result = planner.Plan(Request(strategy, signal, bars, snapshots, false));

        Assert.False(result.IsReady);
        Assert.Equal(
            "unsupported_execution_confirmation_price_filter (guess_the_breakout)",
            result.RejectionReason);
    }

    [Fact]
    public void Plan_NoConfirmation_StopContextSkipsIneligiblePremarketBar()
    {
        var strategy = CreateStrategy(ExecutionConfirmationRules.Disabled);
        var signal = CreateSignal("2026-06-01T13:30:00Z", 100m);
        var bars = new[]
        {
            Bar("2026-06-01T13:30:00Z", 99m, 101m, 98m, 100m),
            Bar("2026-06-01T13:34:00Z", 100m, 101m, 99m, 100m),
            Bar("2026-06-01T13:35:00Z", 100m, 102m, 99m, 101m)
        };
        var snapshots = bars
            .Select(bar => Snapshot(bar, 101m, 100m, 0.2m))
            .ToArray();

        var result = planner.Plan(
            new CompletedBarExecutionRequest(
                strategy,
                signal,
                PlannedOrderSide.Long,
                bars,
                snapshots,
                timestamp => timestamp != bars[1].Timestamp,
                RequireKnownFillBar: true));

        Assert.True(result.IsReady);
        Assert.Equal(0, result.StopContextIndex);
        Assert.Equal(2, result.EntryIndex);
    }

    private static CompletedBarExecutionRequest Request(
        StrategyDefinition strategy,
        TradeSignal signal,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        bool requireKnownFillBar) =>
        new(
            strategy,
            signal,
            PlannedOrderSide.Long,
            bars,
            snapshots,
            _ => true,
            requireKnownFillBar);

    private static StrategyDefinition CreateStrategy(
        ExecutionConfirmationRules confirmation) =>
        new(
            "test.completed-bar-execution",
            "Completed Bar Execution",
            "unit-test",
            1,
            "5m",
            "long",
            new EntryRules(
                "indicator_stack",
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
                null,
                0,
                1,
                1),
            new ConfluenceRules(false, "5m", 20, "none"),
            new ExitRules(
                1m,
                2m,
                8m,
                false,
                1m,
                1m,
                false,
                false,
                false,
                1),
            new ExecutionRules("5m", 5m, confirmation),
            new SessionRules("America/New_York", 0, 0, 0, false, true));

    private static TradeSignal CreateSignal(string timestamp, decimal close) =>
        new(
            Ticker: "TEST",
            Timestamp: DateTimeOffset.Parse(timestamp),
            Timeframe: "5m",
            CurrentPrice: close,
            CurrentVolume: 10_000m,
            CurrentRsi: 50m,
            CurrentAtr: 1m,
            IsAboveVwap: true,
            IsVwapPullback: false,
            IsVwapReclaim: false,
            IsVwapRejection: false,
            IsEma20Pullback: false,
            IsOpeningRangeBreakout: false,
            IsOpeningRangeBreakdown: false,
            IsRecentHighBreakout: false,
            IsRecentLowBreakdown: false,
            IsVolatilityContraction: false,
            IsPriceAboveEma20: true,
            IsPriceAboveEma50: true,
            IsEma20AboveEma50: true,
            VwapExtensionAtr: 0m,
            IsAboveBollingerMiddle: true,
            IsMacdHistogramPositive: true,
            IsMacdNotBearish: true);

    private static OhlcvBar Bar(
        string timestamp,
        decimal open,
        decimal high,
        decimal low,
        decimal close) =>
        new(
            "TEST",
            DateTimeOffset.Parse(timestamp),
            "5m",
            open,
            high,
            low,
            close,
            10_000m);

    private static IndicatorSnapshot Snapshot(
        OhlcvBar bar,
        decimal ema10,
        decimal ema20,
        decimal histogram) =>
        new(
            bar.Ticker,
            bar.Timestamp,
            bar.Timeframe,
            bar.Close,
            bar.Volume,
            100m,
            50m,
            1m,
            ema20,
            99m,
            95m,
            100m,
            103m,
            97m,
            1m,
            histogram,
            0m,
            histogram,
            Ema10: ema10);
}

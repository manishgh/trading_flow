using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class StrategyOrderPlannerTests
{
    [Fact]
    public void Plan_WideAtrStop_PreservesStopAndReducesQuantity()
    {
        var strategy = CreateStrategy(stopAtrMultiple: 2m);
        var signal = CreateSignal(price: 100m, atr: 1m);
        var context = CreateMarketContext();

        var result = new StrategyOrderPlanner().Plan(
            new StrategyOrderPlanningRequest(
                strategy,
                signal,
                signal,
                PlannedOrderSide.Long,
                100_000m,
                new OrderPlanningRiskLimits(1m, 100m),
                0m,
                0m,
                0,
                context.Bars,
                context.Snapshots));

        Assert.True(result.IsAccepted);
        Assert.Equal(500, result.Order?.ShareQuantity);
        Assert.Equal(98m, result.Order?.StopLossPrice);
        Assert.Equal(106m, result.Order?.TakeProfitPrice);
    }

    [Fact]
    public void Plan_ValidAtrStop_BuildsRiskSizedBracket()
    {
        var strategy = CreateStrategy(stopAtrMultiple: 1m);
        var signal = CreateSignal(price: 100m, atr: 1m);
        var context = CreateMarketContext();

        var result = new StrategyOrderPlanner().Plan(
            new StrategyOrderPlanningRequest(
                strategy,
                signal,
                signal,
                PlannedOrderSide.Long,
                100_000m,
                new OrderPlanningRiskLimits(1m, 100m),
                0m,
                0m,
                0,
                context.Bars,
                context.Snapshots));

        Assert.True(result.IsAccepted);
        Assert.Equal(1_000, result.Order?.ShareQuantity);
        Assert.Equal(99m, result.Order?.StopLossPrice);
        Assert.Equal(103m, result.Order?.TakeProfitPrice);
    }

    private static StrategyDefinition CreateStrategy(decimal stopAtrMultiple) =>
        new(
            "strategy.order-plan",
            "Order Plan",
            "unit-test",
            1,
            "5m",
            "long",
            new EntryRules(
                "indicator_stack",
                1m,
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
                5,
                8,
                10),
            new ConfluenceRules(false, "5m", 50, "none"),
            new ExitRules(
                stopAtrMultiple,
                3m,
                2m,
                false,
                1m,
                1m,
                false,
                false,
                false,
                1,
                InitialStopMode: "atr"),
            new ExecutionRules("5m", 0m),
            new SessionRules("America/New_York", 0, 0, 0, false, true));

    private static TradeSignal CreateSignal(decimal price, decimal atr) =>
        new(
            Ticker: "TEST",
            Timestamp: DateTimeOffset.Parse("2026-06-08T13:30:00Z"),
            Timeframe: "5m",
            CurrentPrice: price,
            CurrentVolume: 10_000m,
            CurrentRsi: 50m,
            CurrentAtr: atr,
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

    private static (
        IReadOnlyList<OhlcvBar> Bars,
        IReadOnlyList<IndicatorSnapshot> Snapshots) CreateMarketContext()
    {
        var bar = new OhlcvBar(
            "TEST",
            DateTimeOffset.Parse("2026-06-08T13:30:00Z"),
            "5m",
            100m,
            101m,
            99m,
            100m,
            10_000m);
        var snapshot = new IndicatorSnapshot(
            "TEST",
            bar.Timestamp,
            "5m",
            100m,
            10_000m,
            100m,
            50m,
            1m,
            100m,
            99m,
            95m,
            100m,
            102m,
            98m,
            1m,
            1m,
            0.5m,
            0.5m);
        return (new[] { bar }, new[] { snapshot });
    }
}

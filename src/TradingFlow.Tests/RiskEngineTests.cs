using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class RiskEngineTests
{
    [Fact]
    public void CreateLongBracketOrderWithPositionRisk_CapsLossToOnePercentOfDeployedCapital()
    {
        var strategy = CreateStrategy();
        var signal = CreateSignal(price: 100m, atr: 5m);

        var order = new RiskEngine().CreateLongBracketOrderWithPositionRisk(
            strategy,
            signal,
            accountEquity: 100_000m,
            riskPerTradePct: 1.0m,
            maxPositionValuePct: 20.0m);

        Assert.NotNull(order);
        var positionValue = order!.ShareQuantity * order.LimitPrice;
        var maxLoss = order.ShareQuantity * (order.LimitPrice - order.StopLossPrice);

        Assert.Equal(20_000m, Decimal.Round(positionValue, 0));
        Assert.Equal(200m, Decimal.Round(maxLoss, 0));
        Assert.Equal(103m, order.TakeProfitPrice);
    }

    private static StrategyDefinition CreateStrategy()
    {
        return new StrategyDefinition(
            "risk-test",
            "Risk Test",
            "test",
            1,
            "1m",
            "long",
            EntryRules: null!,
            Confluence: null!,
            ExitRules: new ExitRules(
                StopAtrMultiple: 2m,
                TargetRMultiple: 3m,
                MaxHoldHours: 1m,
                EnableAtrTrailingStop: true,
                TrailingStopAtrMultiple: 2m,
                TrailingActivationR: 1m,
                ExitOnCloseBelowEma20: false,
                ExitOnCloseBelowVwap: false,
                ExitOnMacdHistogramNegative: false,
                MinHoldBarsBeforeTechnicalExit: 1),
            Execution: new ExecutionRules("1m", 0m),
            Session: null!);
    }

    private static TradeSignal CreateSignal(decimal price, decimal atr)
    {
        return new TradeSignal(
            Ticker: "TEST",
            Timestamp: DateTimeOffset.UtcNow,
            Timeframe: "1m",
            CurrentPrice: price,
            CurrentVolume: 10_000m,
            CurrentRsi: 55m,
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
            VwapExtensionAtr: 0.2m,
            IsAboveBollingerMiddle: true,
            IsMacdHistogramPositive: true,
            IsMacdNotBearish: true);
    }
}

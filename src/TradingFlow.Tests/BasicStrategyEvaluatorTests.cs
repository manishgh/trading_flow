using System;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;
using Xunit;

namespace TradingFlow.Tests;

public class BasicStrategyEvaluatorTests
{
    private readonly BasicStrategyEvaluator _evaluator;

    public BasicStrategyEvaluatorTests()
    {
        _evaluator = new BasicStrategyEvaluator();
    }

    private StrategyDefinition CreateBaseStrategy()
    {
        return new StrategyDefinition(
            StrategyId: "test_strat",
            StrategyName: "Test Strat",
            Source: "test",
            Version: 1,
            Timeframe: "15m",
            Direction: "long",
            EntryRules: new EntryRules(
                SetupType: "momentum",
                MinVolumeSpike: 1.5m,
                MinEntryRsi: 40.0m,
                MaxEntryRsi: 70.0m,
                TrendFilter: "none",
                MacdFilter: "none",
                RequirePriceAboveBollingerMiddle: false,
                RequireMacdHistogramPositive: false,
                RequirePriceAboveVwap: false,
                RequirePriceAboveEma20: false,
                RequirePriceAboveEma50: false,
                RequireEma20AboveEma50: false,
                MaxVwapExtensionAtr: null,
                OpeningRangeMinutes: 0,
                RecentHighLookbackBars: 20,
                VolatilityContractionLookbackBars: 10
            ),
            Confluence: null!,
            ExitRules: null!,
            Execution: null!,
            Session: null!
        );
    }

    private TradeSignal CreateBaseSignal(decimal rsi = 50m)
    {
        return new TradeSignal(
            Ticker: "AAPL",
            Timestamp: DateTimeOffset.UtcNow,
            Timeframe: "15m",
            CurrentPrice: 150m,
            CurrentVolume: 10000m,
            CurrentRsi: rsi,
            CurrentAtr: 2.5m,
            IsAboveVwap: true,
            IsVwapPullback: false,
            IsVwapReclaim: false,
            IsEma20Pullback: false,
            IsOpeningRangeBreakout: false,
            IsRecentHighBreakout: false,
            IsVolatilityContraction: false,
            IsPriceAboveEma20: true,
            IsPriceAboveEma50: true,
            IsEma20AboveEma50: true,
            VwapExtensionAtr: null,
            IsAboveBollingerMiddle: true,
            IsMacdHistogramPositive: true,
            IsMacdNotBearish: true
        );
    }

    [Fact]
    public void GetLongEntryRejection_WhenValid_ReturnsNull()
    {
        var strategy = CreateBaseStrategy();
        var signal = CreateBaseSignal();

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 2.0m);

        Assert.Null(rejection);
        Assert.True(_evaluator.IsLongEntryCandidate(strategy, signal, 2.0m));
    }

    [Fact]
    public void GetLongEntryRejection_WhenVolumeTooLow_ReturnsFormattedString()
    {
        var strategy = CreateBaseStrategy();
        var signal = CreateBaseSignal();

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 1.0m);

        Assert.NotNull(rejection);
        Assert.Equal("relative_volume_below_minimum (Actual: 1.00, Required: 1.50)", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenRsiTooLow_ReturnsFormattedString()
    {
        var strategy = CreateBaseStrategy();
        var signal = CreateBaseSignal(rsi: 30.0m);

        var rejection = _evaluator.GetLongEntryRejection(strategy, signal, relativeVolume: 2.0m);

        Assert.NotNull(rejection);
        Assert.Equal("rsi_outside_range (Actual: 30.00, Required: 40.00-70.00)", rejection);
    }
}

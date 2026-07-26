using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Tests;

public sealed class StrategyInitialStopResolverTests
{
    private readonly StrategyInitialStopResolver resolver = new();

    [Fact]
    public void Resolve_AtrStop_DoesNotApplyPortfolioPercentageCap()
    {
        var strategy = CreateStrategy() with
        {
            ExitRules = CreateStrategy().ExitRules with
            {
                InitialStopMode = "atr",
                StopAtrMultiple = 2m
            }
        };
        var request = CreateRequest(strategy, PlannedOrderSide.Long, entryPrice: 100m, atr: 2m);

        var result = resolver.Resolve(request);

        Assert.True(result.IsResolved);
        Assert.Equal(96m, result.StopPrice);
        Assert.Equal(4m, result.StopDistance);
        Assert.Equal(PlannedStopKind.Atr, result.Stop?.Kind);
    }

    [Fact]
    public void Resolve_SwingLow_UsesSignalStructuralLow()
    {
        var strategy = CreateStrategy() with
        {
            ExitRules = CreateStrategy().ExitRules with
            {
                InitialStopMode = "swing_low",
                StopTickBuffer = 0.01m
            }
        };
        var request = CreateRequest(
            strategy,
            PlannedOrderSide.Long,
            entryPrice: 100m,
            atr: 2m,
            reversionStretchLow: 97m);

        var result = resolver.Resolve(request);

        Assert.True(result.IsResolved);
        Assert.Equal(96.99m, result.StopPrice);
        Assert.Equal(3.01m, result.StopDistance);
        Assert.Equal(PlannedStopKind.Structural, result.Stop?.Kind);
    }

    [Fact]
    public void Resolve_SwingLow_PrefersSetupStructuralStopPrice()
    {
        var strategy = CreateStrategy() with
        {
            ExitRules = CreateStrategy().ExitRules with
            {
                InitialStopMode = "swing_low",
                StopTickBuffer = 0.01m
            }
        };
        var request = CreateRequest(
            strategy,
            PlannedOrderSide.Long,
            entryPrice: 100m,
            atr: 2m,
            reversionStretchLow: 95m,
            setupStructuralStopPrice: 98m);

        var result = resolver.Resolve(request);

        Assert.True(result.IsResolved);
        Assert.Equal(97.99m, result.StopPrice);
        Assert.Equal(2.01m, result.StopDistance);
    }

    [Fact]
    public void Resolve_OpeningRangeShort_UsesRangeHigh()
    {
        var strategy = CreateStrategy() with
        {
            EntryRules = CreateStrategy().EntryRules with
            {
                OpeningRangeMinutes = 15
            },
            ExitRules = CreateStrategy().ExitRules with
            {
                InitialStopMode = "opening_range_opposite"
            }
        };
        var bars = new[]
        {
            Bar("2026-06-08T13:30:00Z", 100m, 103m, 99m, 102m),
            Bar("2026-06-08T13:35:00Z", 102m, 104m, 101m, 103m),
            Bar("2026-06-08T13:45:00Z", 101m, 102m, 98m, 99m)
        };
        var request = CreateRequest(
            strategy,
            PlannedOrderSide.Short,
            entryPrice: 99m,
            atr: 1m,
            bars: bars,
            entryIndex: 2);

        var result = resolver.Resolve(request);

        Assert.True(result.IsResolved);
        Assert.Equal(104m, result.StopPrice);
        Assert.Equal(5m, result.StopDistance);
    }

    [Fact]
    public void Resolve_SwingLow_DoesNotReadFutureFillBar()
    {
        var strategy = CreateStrategy() with
        {
            ExitRules = CreateStrategy().ExitRules with
            {
                InitialStopMode = "swing_low",
                StopTickBuffer = 0.01m
            }
        };
        var bars = new[]
        {
            Bar("2026-06-08T13:30:00Z", 100m, 102m, 95m, 101m),
            Bar("2026-06-08T13:35:00Z", 101m, 103m, 1m, 102m)
        };
        var request = CreateRequest(
            strategy,
            PlannedOrderSide.Long,
            entryPrice: 102m,
            atr: 1m,
            bars: bars,
            entryIndex: 0);

        var result = resolver.Resolve(request);

        Assert.True(result.IsResolved);
        Assert.Equal(94.99m, result.StopPrice);
    }

    private static StrategyInitialStopRequest CreateRequest(
        StrategyDefinition strategy,
        PlannedOrderSide side,
        decimal entryPrice,
        decimal atr,
        decimal? reversionStretchLow = null,
        decimal? setupStructuralStopPrice = null,
        IReadOnlyList<OhlcvBar>? bars = null,
        int entryIndex = 0)
    {
        bars ??= new[] { Bar("2026-06-08T13:30:00Z", 100m, 101m, 99m, 100m) };
        var snapshots = bars.Select(bar => new IndicatorSnapshot(
            Ticker: bar.Ticker,
            Timestamp: bar.Timestamp,
            Timeframe: bar.Timeframe,
            CurrentPrice: bar.Close,
            CurrentVolume: bar.Volume,
            Vwap: 100m,
            Rsi: 50m,
            Atr: atr,
            Ema20: 100m,
            Ema50: 99m,
            Ema200: 95m,
            BollingerMiddle: 100m,
            BollingerUpper: 102m,
            BollingerLower: 98m,
            RelativeVolume: 1m,
            MacdLine: 1m,
            MacdSignal: 0.5m,
            MacdHistogram: 0.5m)).ToArray();
        var signal = new TradeSignal(
            Ticker: bars[entryIndex].Ticker,
            Timestamp: bars[entryIndex].Timestamp,
            Timeframe: bars[entryIndex].Timeframe,
            CurrentPrice: entryPrice,
            CurrentVolume: bars[entryIndex].Volume,
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
            IsMacdNotBearish: true,
            ReversionStretchLow: reversionStretchLow,
            SetupStructuralStopPrice: setupStructuralStopPrice);

        return new StrategyInitialStopRequest(
            strategy,
            signal,
            side,
            entryPrice,
            entryIndex,
            bars,
            snapshots);
    }

    private static OhlcvBar Bar(
        string timestamp,
        decimal open,
        decimal high,
        decimal low,
        decimal close) =>
        new(
            Ticker: "TEST",
            Timestamp: DateTimeOffset.Parse(timestamp),
            Timeframe: "5m",
            Open: open,
            High: high,
            Low: low,
            Close: close,
            Volume: 10_000m);

    private static StrategyDefinition CreateStrategy() =>
        new(
            "strategy.stop-test",
            "Stop Test",
            "unit-test",
            1,
            "5m",
            "long",
            new EntryRules(
                SetupType: "indicator_stack",
                MinVolumeSpike: 1m,
                MinEntryRsi: 0m,
                MaxEntryRsi: 100m,
                TrendFilter: "none",
                MacdFilter: "none",
                RequirePriceAboveBollingerMiddle: false,
                RequireMacdHistogramPositive: false,
                RequirePriceAboveVwap: false,
                RequirePriceAboveEma20: false,
                RequirePriceAboveEma50: false,
                RequireEma20AboveEma50: false,
                MaxVwapExtensionAtr: null,
                OpeningRangeMinutes: 5,
                RecentHighLookbackBars: 8,
                VolatilityContractionLookbackBars: 10),
            new ConfluenceRules(false, "5m", 50, "none"),
            new ExitRules(
                StopAtrMultiple: 1m,
                TargetRMultiple: 3m,
                MaxHoldHours: 2m,
                EnableAtrTrailingStop: false,
                TrailingStopAtrMultiple: 1m,
                TrailingActivationR: 1m,
                ExitOnCloseBelowEma20: false,
                ExitOnCloseBelowVwap: false,
                ExitOnMacdHistogramNegative: false,
                MinHoldBarsBeforeTechnicalExit: 1),
            new ExecutionRules("5m", 0m),
            new SessionRules("America/New_York", 0, 0, 0, false, true));
}

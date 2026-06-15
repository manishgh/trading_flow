using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;
using Xunit;

namespace TradingFlow.Tests;

public sealed class SignalGeneratorStepBreakoutTests
{
    [Fact]
    public void CreateTradeSignal_WhenPriorMoveBaseAndBreakoutAligns_SetsStepBreakoutMetrics()
    {
        var bars = BuildStepBreakoutBars();
        var snapshots = new IndicatorEngine().Compute(bars);
        var strategy = CreateStrategy(direction: "long", setupType: "step_breakout");
        var index = bars.Count - 1;

        var signal = new SignalGenerator().CreateTradeSignal(strategy, bars, snapshots, index);

        Assert.NotNull(signal);
        Assert.True(signal!.IsStepBreakout);
        Assert.False(signal.IsStepBreakdown);
        Assert.NotNull(signal.StepPriorMovePct);
        Assert.True(signal.StepPriorMovePct >= strategy.EntryRules.MinStepPriorMovePct);
        Assert.NotNull(signal.StepBaseDepthPct);
        Assert.True(signal.StepBaseDepthPct <= strategy.EntryRules.MaxStepBaseDepthPct);
        Assert.NotNull(signal.StepBaseVolumeRatio);
        Assert.True(signal.StepBaseVolumeRatio <= strategy.EntryRules.MaxStepBaseVolumeRatio);
        Assert.NotNull(signal.StepBreakoutVolumeRatio);
        Assert.True(signal.StepBreakoutVolumeRatio >= strategy.EntryRules.MinStepBreakoutVolumeRatio);

        var rejection = new BasicStrategyEvaluator().GetLongEntryRejection(strategy, signal, relativeVolume: 0m);
        Assert.Null(rejection);
    }

    [Fact]
    public void GetShortEntryRejection_WhenStepBreakdownAligns_ReturnsNull()
    {
        var strategy = CreateStrategy(direction: "short", setupType: "step_breakout") with
        {
            EntryRules = CreateStrategy(direction: "short", setupType: "step_breakout").EntryRules with
            {
                EnableShort = true,
                ShortSetupType = "step_breakdown",
                RequirePriceBelowVwapForShort = false,
                RequireMacdBearishForShort = true,
                RequirePriceBelowShortAnchoredVwap = true
            }
        };
        var signal = new TradeSignal(
            Ticker: "TEST",
            Timestamp: DateTimeOffset.UtcNow,
            Timeframe: "1d",
            CurrentPrice: 80m,
            CurrentVolume: 1000m,
            CurrentRsi: 45m,
            CurrentAtr: 2m,
            IsAboveVwap: false,
            IsVwapPullback: false,
            IsVwapReclaim: false,
            IsVwapRejection: false,
            IsEma20Pullback: false,
            IsOpeningRangeBreakout: false,
            IsOpeningRangeBreakdown: false,
            IsRecentHighBreakout: false,
            IsRecentLowBreakdown: true,
            IsVolatilityContraction: false,
            IsPriceAboveEma20: false,
            IsPriceAboveEma50: false,
            IsEma20AboveEma50: false,
            VwapExtensionAtr: null,
            IsAboveBollingerMiddle: false,
            IsMacdHistogramPositive: false,
            IsMacdNotBearish: false,
            IsStepBreakdown: true,
            IsBelowShortAnchoredVwap: true);

        var rejection = new BasicStrategyEvaluator().GetShortEntryRejection(strategy, signal, relativeVolume: 0m);

        Assert.Null(rejection);
    }


    [Fact]
    public void CreateTradeSignal_WhenPullbackReclaimsSma10AndSma20_SetsSwingReclaim()
    {
        var bars = BuildSwingReclaimBars();
        var snapshots = new IndicatorEngine().Compute(bars);
        var strategy = CreateStrategy(direction: "long", setupType: "swing_reclaim") with
        {
            EntryRules = CreateStrategy(direction: "long", setupType: "swing_reclaim").EntryRules with
            {
                ReclaimLookbackBars = 20,
                MinReclaimPullbackDepthPct = 8m,
                MaxReclaimPullbackDepthPct = 35m,
                RequireReclaimLowAboveSma50 = true,
                RequireReclaimCloseAboveSma10 = true,
                RequireReclaimCloseAboveSma20 = true,
                RequirePriceAboveSma10 = true,
                RequirePriceAboveSma20 = true,
                RequirePriceAboveSma50 = true,
                RequireSma10AboveSma20 = true,
                RequireSma20AboveSma50 = true,
                TrendFilter = "sma_stack_10_20_50"
            }
        };
        var index = bars.Count - 1;

        var signal = new SignalGenerator().CreateTradeSignal(strategy, bars, snapshots, index);

        Assert.NotNull(signal);
        Assert.True(signal!.IsSwingReclaim);
        Assert.NotNull(signal.ReclaimPullbackDepthPct);
        Assert.InRange(signal.ReclaimPullbackDepthPct!.Value, 8m, 35m);

        var rejection = new BasicStrategyEvaluator().GetLongEntryRejection(strategy, signal, relativeVolume: 0m);
        Assert.Null(rejection);
    }

    [Fact]
    public void CreateTradeSignal_WhenRossSetupUsesSwingShapedBars_DoesNotSetSwingFlags()
    {
        var bars = BuildSwingReclaimBars();
        var snapshots = new IndicatorEngine().Compute(bars);
        var strategy = CreateStrategy(direction: "long", setupType: "ross_gap_go_bull_flag");
        var index = bars.Count - 1;

        var signal = new SignalGenerator().CreateTradeSignal(strategy, bars, snapshots, index);

        Assert.NotNull(signal);
        Assert.False(signal!.IsSwingReclaim);
        Assert.False(signal.IsSwingRollover);
    }

    [Fact]
    public void CreateTradeSignal_WhenIndicatorStackDisablesLookbacks_DoesNotThrowOnZeroWindows()
    {
        var bars = BuildIntradayIndicatorStackBars();
        var snapshots = new IndicatorEngine().Compute(bars);
        var baseStrategy = CreateStrategy(direction: "long", setupType: "indicator_stack");
        var strategy = baseStrategy with
        {
            Timeframe = "5m",
            EntryRules = baseStrategy.EntryRules with
            {
                OpeningRangeMinutes = 0,
                RecentHighLookbackBars = 0,
                VolatilityContractionLookbackBars = 0,
                RequirePriceAboveVwap = true,
                RequirePriceAboveEma10 = true,
                RequirePriceAboveEma20 = true,
                RequireEma10AboveEma20 = true,
                RequireMacdHistogramPositive = true,
                MacdFilter = "not_bearish"
            },
            Execution = new ExecutionRules("5m", 10m),
            Session = new SessionRules("America/New_York", 1, 30, 30)
        };

        var generator = new SignalGenerator();
        var exception = Record.Exception(() => generator.CreateTradeSignal(strategy, bars, snapshots, bars.Count - 1));

        Assert.Null(exception);
    }

    [Fact]
    public void CreateTradeSignal_WhenPriorAdvanceRollsBelowSma10AndSma20_SetsSwingRollover()
    {
        var bars = BuildSwingRolloverBars();
        var snapshots = new IndicatorEngine().Compute(bars);
        var baseStrategy = CreateStrategy(direction: "short", setupType: "step_breakout");
        var strategy = baseStrategy with
        {
            Direction = "short",
            EntryRules = baseStrategy.EntryRules with
            {
                EnableShort = true,
                ShortSetupType = "swing_rollover",
                RequireMacdBearishForShort = false,
                RequirePriceBelowShortAnchoredVwap = false,
                MaxShortCloseLocationValue = 0.70m,
                RolloverLookbackBars = 20,
                MinRolloverAdvancePct = 12m,
                MinRolloverDropFromHighPct = 3m,
                MaxRolloverDropFromHighPct = 25m,
                RequireRolloverCloseBelowSma10 = true,
                RequireRolloverCloseBelowSma20 = true
            }
        };
        var index = bars.Count - 1;

        var signal = new SignalGenerator().CreateTradeSignal(strategy, bars, snapshots, index);

        Assert.NotNull(signal);
        Assert.True(signal!.IsSwingRollover);
        Assert.NotNull(signal.RolloverAdvancePct);
        Assert.NotNull(signal.RolloverDropFromHighPct);
        Assert.True(signal.RolloverAdvancePct >= strategy.EntryRules.MinRolloverAdvancePct);
        Assert.InRange(signal.RolloverDropFromHighPct!.Value, 3m, 25m);

        var rejection = new BasicStrategyEvaluator().GetShortEntryRejection(strategy, signal, relativeVolume: 0m);
        Assert.Null(rejection);
    }

    [Fact]
    public void CreateTradeSignal_WhenRolloverRequiresMoreLowerClosesThanAvailable_DoesNotSetSwingRollover()
    {
        var bars = BuildSwingRolloverBars();
        var snapshots = new IndicatorEngine().Compute(bars);
        var baseStrategy = CreateStrategy(direction: "short", setupType: "step_breakout");
        var strategy = baseStrategy with
        {
            Direction = "short",
            EntryRules = baseStrategy.EntryRules with
            {
                EnableShort = true,
                ShortSetupType = "swing_rollover",
                RequireMacdBearishForShort = false,
                RequirePriceBelowShortAnchoredVwap = false,
                MaxShortCloseLocationValue = 0.70m,
                RolloverLookbackBars = 20,
                MinRolloverAdvancePct = 12m,
                MinRolloverDropFromHighPct = 3m,
                MaxRolloverDropFromHighPct = 25m,
                RequireRolloverCloseBelowSma10 = true,
                RequireRolloverCloseBelowSma20 = true,
                RolloverConsecutiveLowerCloseBars = 5
            }
        };
        var index = bars.Count - 1;

        var signal = new SignalGenerator().CreateTradeSignal(strategy, bars, snapshots, index);

        Assert.NotNull(signal);
        Assert.False(signal!.IsSwingRollover);
    }

    [Fact]
    public void CreateTradeSignal_WhenRolloverRequiresPriorLowBreakAndItBreaks_SetsSwingRollover()
    {
        var bars = BuildSwingRolloverBars();
        var snapshots = new IndicatorEngine().Compute(bars);
        var baseStrategy = CreateStrategy(direction: "short", setupType: "step_breakout");
        var strategy = baseStrategy with
        {
            Direction = "short",
            EntryRules = baseStrategy.EntryRules with
            {
                EnableShort = true,
                ShortSetupType = "swing_rollover",
                RequireMacdBearishForShort = false,
                RequirePriceBelowShortAnchoredVwap = false,
                MaxShortCloseLocationValue = 0.70m,
                RolloverLookbackBars = 20,
                MinRolloverAdvancePct = 12m,
                MinRolloverDropFromHighPct = 3m,
                MaxRolloverDropFromHighPct = 25m,
                RequireRolloverCloseBelowSma10 = true,
                RequireRolloverCloseBelowSma20 = true,
                RolloverConsecutiveLowerCloseBars = 2,
                RequireRolloverCloseBelowPriorLow = true
            }
        };
        var index = bars.Count - 1;

        var signal = new SignalGenerator().CreateTradeSignal(strategy, bars, snapshots, index);

        Assert.NotNull(signal);
        Assert.True(signal!.IsSwingRollover);
    }

    private static StrategyDefinition CreateStrategy(string direction, string setupType)
    {
        return new StrategyDefinition(
            "test.step",
            "Test Step",
            "test",
            1,
            "1d",
            direction,
            new EntryRules(
                SetupType: setupType,
                MinVolumeSpike: 0m,
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
                OpeningRangeMinutes: 60,
                RecentHighLookbackBars: 20,
                VolatilityContractionLookbackBars: 10,
                ShortSetupType: "step_breakdown",
                RequirePriceBelowVwapForShort: false,
                RequireMacdBearishForShort: true,
                AnchoredVwapMode: "lookback_low",
                AnchoredVwapLookbackBars: 30,
                ShortAnchoredVwapMode: "lookback_high",
                ShortAnchoredVwapLookbackBars: 30,
                RequirePriceBelowShortAnchoredVwap: false,
                StepPriorMoveLookbackBars: 60,
                StepConsolidationMinBars: 10,
                StepConsolidationMaxBars: 20,
                MinStepPriorMovePct: 25m,
                MinStepPriorDeclinePct: 15m,
                MaxStepBaseDepthPct: 20m,
                MaxStepBaseVolumeRatio: 1.10m,
                MinStepBreakoutVolumeRatio: 0.90m,
                RequireStepHigherLows: true,
                RequireStepLowerHighsForShort: true),
            new ConfluenceRules(false, "1d", 50, "none"),
            new ExitRules(1.5m, 4m, 720m, true, 2m, 3m, false, false, false, 1),
            new ExecutionRules("1d", 15m),
            new SessionRules("America/New_York", 0, 0, 0));
    }

    private static IReadOnlyList<OhlcvBar> BuildStepBreakoutBars()
    {
        var start = new DateTimeOffset(2025, 10, 1, 4, 0, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();

        for (var i = 0; i < 60; i++)
        {
            var close = 100m + (i * 0.8m);
            bars.Add(CreateBar(start.AddDays(i), close, close + 1m, close - 1m, close, 1_000_000m));
        }

        for (var i = 0; i < 15; i++)
        {
            var low = 142m + (i * 0.25m);
            var high = 156m - (i * 0.10m);
            var close = low + ((high - low) * 0.65m);
            bars.Add(CreateBar(start.AddDays(60 + i), close, high, low, close, 750_000m));
        }

        bars.Add(CreateBar(start.AddDays(75), 160m, 162m, 154m, 160m, 900_000m));
        return bars;
    }

    private static IReadOnlyList<OhlcvBar> BuildIntradayIndicatorStackBars()
    {
        var start = new DateTimeOffset(2026, 6, 11, 13, 30, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();

        for (var i = 0; i < 80; i++)
        {
            var close = 10m + (i * 0.08m);
            bars.Add(new OhlcvBar(
                "TEST",
                start.AddMinutes(i * 5),
                "5m",
                close - 0.04m,
                close + 0.12m,
                close - 0.10m,
                close,
                100_000m + (i * 500m)));
        }

        return bars;
    }



    private static IReadOnlyList<OhlcvBar> BuildSwingRolloverBars()
    {
        var start = new DateTimeOffset(2025, 10, 1, 4, 0, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();

        for (var i = 0; i < 60; i++)
        {
            var close = 100m + (i * 0.9m);
            bars.Add(CreateBar(start.AddDays(i), close - 0.5m, close + 1.5m, close - 1.5m, close, 1_000_000m));
        }

        var closes = new[] { 154m, 158m, 162m, 166m, 170m, 174m, 176m, 172m, 168m, 164m, 150m };
        for (var i = 0; i < closes.Length; i++)
        {
            var close = closes[i];
            var high = i == 6 ? 180m : close + 2m;
            bars.Add(CreateBar(start.AddDays(60 + i), close + 1m, high, close - 4m, close, 1_100_000m));
        }

        return bars;
    }    private static IReadOnlyList<OhlcvBar> BuildSwingReclaimBars()
    {
        var start = new DateTimeOffset(2025, 10, 1, 4, 0, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();

        for (var i = 0; i < 60; i++)
        {
            var close = 100m + (i * 0.8m);
            bars.Add(CreateBar(start.AddDays(i), close, close + 1.5m, close - 1.5m, close, 1_000_000m));
        }

        for (var i = 0; i < 15; i++)
        {
            var close = 150m + (i * 0.65m);
            bars.Add(CreateBar(start.AddDays(60 + i), close - 0.5m, close + 2m, close - 2m, close, 1_100_000m));
        }

        bars.Add(CreateBar(start.AddDays(75), 157m, 160m, 155m, 156m, 950_000m));
        bars.Add(CreateBar(start.AddDays(76), 155m, 157m, 150m, 152m, 900_000m));
        bars.Add(CreateBar(start.AddDays(77), 151m, 153m, 146m, 148m, 850_000m));
        bars.Add(CreateBar(start.AddDays(78), 148m, 149m, 144m, 146m, 820_000m));
        bars.Add(CreateBar(start.AddDays(79), 148m, 164m, 147m, 162m, 1_400_000m));

        return bars;
    }    private static OhlcvBar CreateBar(DateTimeOffset timestamp, decimal open, decimal high, decimal low, decimal close, decimal volume)
    {
        return new OhlcvBar("TEST", timestamp, "1d", open, high, low, close, volume);
    }
}

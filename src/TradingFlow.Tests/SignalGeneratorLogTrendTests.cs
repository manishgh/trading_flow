using System;
using System.Collections.Generic;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;
using Xunit;

namespace TradingFlow.Tests;

public class SignalGeneratorLogTrendTests
{
    [Fact]
    public void CreateTradeSignal_WhenPriceAndVolumeRise_ComputesPositiveLogTrends()
    {
        var strategy = CreateStrategy();
        var start = new DateTimeOffset(2026, 6, 9, 13, 30, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();
        var snapshots = new List<IndicatorSnapshot>();

        for (var i = 0; i < 15; i++)
        {
            var timestamp = start.AddMinutes(i * 5);
            var close = 100m + i;
            var volume = 1_000m + i * 250m;
            bars.Add(new OhlcvBar("TEST", timestamp, "5m", close - 0.5m, close + 0.5m, close - 1m, close, volume));
            snapshots.Add(new IndicatorSnapshot(
                "TEST",
                timestamp,
                "5m",
                close,
                volume,
                Vwap: close - 1m,
                Rsi: 60m,
                Atr: 1m,
                Ema20: close - 1m,
                Ema50: close - 2m,
                Ema200: close - 3m,
                BollingerMiddle: close - 1m,
                BollingerUpper: close + 2m,
                BollingerLower: close - 2m,
                RelativeVolume: 1.2m,
                MacdLine: 1m,
                MacdSignal: 0.5m,
                MacdHistogram: 0.2m));
        }

        var signal = new SignalGenerator().CreateTradeSignal(strategy, bars, snapshots, index: 14);

        Assert.NotNull(signal);
        Assert.True(signal.PriceLogSlope > 0m);
        Assert.True(signal.VolumeLogSlope > 0m);
        Assert.True(signal.PriceLogR2 > 0.9m);
        Assert.True(signal.VolumeLogR2 > 0.9m);
        Assert.True(signal.IsOpeningDriveContinuation);
        Assert.True(signal.IsAboveSessionOpen);
        Assert.True(signal.CloseLocationValue > 0.6m);
        Assert.True(signal.SessionGainPct > 0m);
    }

    private static StrategyDefinition CreateStrategy()
    {
        return new StrategyDefinition(
            StrategyId: "test.log-breakout",
            StrategyName: "Test Log Breakout",
            Source: "test",
            Version: 1,
            Timeframe: "5m",
            Direction: "long",
            EntryRules: new EntryRules(
                SetupType: "log_breakout",
                MinVolumeSpike: 0.5m,
                MinEntryRsi: 40m,
                MaxEntryRsi: 80m,
                TrendFilter: "none",
                MacdFilter: "not_bearish",
                RequirePriceAboveBollingerMiddle: false,
                RequireMacdHistogramPositive: true,
                RequirePriceAboveVwap: true,
                RequirePriceAboveEma20: false,
                RequirePriceAboveEma50: false,
                RequireEma20AboveEma50: false,
                MaxVwapExtensionAtr: null,
                OpeningRangeMinutes: 15,
                RecentHighLookbackBars: 8,
                VolatilityContractionLookbackBars: 10,
                RequireLogPriceRising: true,
                RequireLogVolumeRising: true,
                LogPriceLookbackBars: 12,
                LogVolumeLookbackBars: 12,
                MinLogPriceSlope: 0m,
                MinLogVolumeSlope: 0m),
            Confluence: new ConfluenceRules(false, "5m", 50, "none"),
            ExitRules: new ExitRules(1.5m, 2.0m, 6.5m, true, 1.5m, 1m, true, false, true, 3),
            Execution: new ExecutionRules("5m", 8m),
            Session: new SessionRules("America/New_York", 15, 30, 30));
    }
}

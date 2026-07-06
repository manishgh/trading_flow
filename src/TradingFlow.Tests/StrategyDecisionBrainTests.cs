using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class StrategyDecisionBrainTests
{
    [Fact]
    public void ResolveEntryRelativeVolume_WhenConfiguredForFinvizStyle_UsesSessionRelativeVolume()
    {
        var brain = new StrategyDecisionBrain();
        var strategy = CreateStrategy() with
        {
            EntryRules = CreateStrategy().EntryRules with
            {
                MinVolumeSpikeSource = "finviz_style"
            }
        };
        var snapshot = CreateSnapshot() with
        {
            RelativeVolume = 0.10m,
            SlotRelativeVolume = 0.20m,
            SessionRelativeVolume = 2.50m
        };

        var value = brain.ResolveEntryRelativeVolume(strategy, snapshot);

        Assert.Equal(2.50m, value);
    }

    [Fact]
    public void GetLongEntryRejection_WhenVolumeBlocks_IncludesSourceAndBaselineDetails()
    {
        var brain = new StrategyDecisionBrain();
        var strategy = CreateStrategy() with
        {
            EntryRules = CreateStrategy().EntryRules with
            {
                MinVolumeSpike = 2.0m,
                MinVolumeSpikeSource = "cumulative_same_time",
                VolumeConfirmationMode = "hard_gate"
            }
        };
        var snapshot = CreateSnapshot() with
        {
            CurrentVolume = 1_250m,
            CumulativeAverageVolume = 10_000m,
            SlotAverageVolume = 600m,
            AverageSessionVolume = 100_000m,
            RelativeVolumeSampleCount = 60
        };

        var rejection = brain.GetLongEntryRejection(strategy, CreateSignal(), snapshot, 0.75m);

        Assert.NotNull(rejection);
        Assert.Contains("relative_volume_below_minimum", rejection);
        Assert.Contains("Source: cumulative_same_time", rejection);
        Assert.Contains("BarVolume: 1,250", rejection);
        Assert.Contains("SampleSessions: 60", rejection);
    }

    [Fact]
    public void GetLongEntryRejection_WhenVolumeModeIsSoftMarker_DoesNotBlockEntry()
    {
        var brain = new StrategyDecisionBrain();
        var strategy = CreateStrategy() with
        {
            EntryRules = CreateStrategy().EntryRules with
            {
                MinVolumeSpike = 2.0m,
                VolumeConfirmationMode = "soft_marker"
            }
        };

        var rejection = brain.GetLongEntryRejection(strategy, CreateSignal(), CreateSnapshot(), 0.10m);

        Assert.Null(rejection);
    }

    private static StrategyDefinition CreateStrategy()
    {
        return new StrategyDefinition(
            "strategy.test",
            "Test Strategy",
            "unit-test",
            1,
            "1m",
            "long",
            new EntryRules(
                SetupType: "momentum",
                MinVolumeSpike: 1.0m,
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
            new ConfluenceRules(false, "1m", 50, "none"),
            new ExitRules(1m, 3m, 2m, false, 1m, 1m, false, false, false, 1),
            new ExecutionRules("1m", 5m),
            new SessionRules("America/New_York", 0, 0, 0, false, true));
    }

    private static IndicatorSnapshot CreateSnapshot()
    {
        return new IndicatorSnapshot(
            "RGTI",
            DateTimeOffset.Parse("2026-06-11T13:35:00Z"),
            "1m",
            10m,
            1_000m,
            9.8m,
            55m,
            0.25m,
            9.7m,
            9.5m,
            null,
            null,
            null,
            null,
            0.5m,
            0.1m,
            0.0m,
            0.1m,
            SlotRelativeVolume: 0.4m,
            SessionRelativeVolume: 0.8m,
            SlotAverageVolume: 2_500m,
            CumulativeAverageVolume: 20_000m,
            AverageSessionVolume: 200_000m,
            RelativeVolumeSampleCount: 60);
    }

    private static TradeSignal CreateSignal()
    {
        return new TradeSignal(
            "RGTI",
            DateTimeOffset.Parse("2026-06-11T13:35:00Z"),
            "1m",
            10m,
            1_000m,
            55m,
            0.25m,
            true,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            false,
            true,
            true,
            true,
            null,
            true,
            true,
            true);
    }
}

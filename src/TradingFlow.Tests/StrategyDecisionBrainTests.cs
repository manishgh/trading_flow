using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class StrategyDecisionBrainTests
{
    [Fact]
    public void RelativeVolumeMeasureParser_WhenVendorOrLegacySourceIsConfigured_RejectsIt()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            RelativeVolumeMeasureParser.Parse("finviz_style"));

        Assert.Contains("Unsupported min_volume_spike_source", exception.Message);
    }

    [Fact]
    public void ResolveEntryRelativeVolume_WhenSlotBarIsConfigured_UsesSlotEvidence()
    {
        var strategy = CreateStrategy() with
        {
            EntryRules = CreateStrategy().EntryRules with
            {
                MinVolumeSpikeSource = RelativeVolumeMeasure.SlotBar
            }
        };
        var snapshot = CreateSnapshot() with
        {
            RelativeVolume = 0.10m,
            SlotRelativeVolume = 2.50m
        };

        Assert.Equal(2.50m, StrategyDecisionBrain.ResolveEntryRelativeVolume(strategy, snapshot));
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
                MinVolumeSpikeSource = RelativeVolumeMeasure.CumulativeSameTime,
                VolumeConfirmationMode = "hard_gate"
            }
        };
        var snapshot = CreateSnapshot() with
        {
            CurrentVolume = 1_250m,
            CumulativeSameTimeMedianVolume = 10_000m,
            SlotMedianVolume = 600m,
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

    [Fact]
    public void GetLongEntryRejection_WhenBaselineHasNineteenOfTwentySessions_FailsWithReadinessReason()
    {
        var brain = new StrategyDecisionBrain();
        var strategy = CreateStrategy() with
        {
            EntryRules = CreateStrategy().EntryRules with
            {
                MinVolumeSpike = 2.0m,
                MinVolumeSpikeSource = RelativeVolumeMeasure.CumulativeSameTime,
                VolumeConfirmationMode = "hard_gate"
            }
        };
        var snapshot = CreateSnapshot() with
        {
            RelativeVolume = null,
            RelativeVolumeSampleCount = 19,
            RelativeVolumeMinimumSamples = 20,
            DataFeed = "sip",
            MarketEvidenceReliability = "verified_same_feed"
        };

        var rejection = brain.GetLongEntryRejection(strategy, CreateSignal(), snapshot, null);

        Assert.NotNull(rejection);
        Assert.StartsWith("rvol_baseline_not_ready", rejection, StringComparison.Ordinal);
        Assert.Contains("Samples: 19", rejection);
        Assert.Contains("Required: 20", rejection);
        Assert.Contains("DataFeed: sip", rejection);
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
            SlotMedianVolume: 2_500m,
            CumulativeSameTimeMedianVolume: 20_000m,
            RelativeVolumeSampleCount: 60,
            SlotRelativeVolumeSampleCount: 60,
            MarketEvidenceProfileVersion: "test_rvol_v1",
            RelativeVolumeCohort: "regular",
            RelativeVolumeMinimumSamples: 20);
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

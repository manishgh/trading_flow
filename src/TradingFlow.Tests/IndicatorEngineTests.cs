using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Tests;

public class IndicatorEngineTests
{
    [Fact]
    public void Compute_ResetsVwapOnNewExchangeDate()
    {
        var engine = new IndicatorEngine();
        var bars = new[]
        {
            CreateFlatPriceBar(new DateTimeOffset(2026, 6, 1, 13, 30, 0, TimeSpan.Zero), 10m, 100m),
            CreateFlatPriceBar(new DateTimeOffset(2026, 6, 1, 13, 35, 0, TimeSpan.Zero), 20m, 100m),
            CreateFlatPriceBar(new DateTimeOffset(2026, 6, 2, 13, 30, 0, TimeSpan.Zero), 30m, 100m)
        };

        var snapshots = engine.Compute(bars);

        Assert.Equal(10m, snapshots[0].Vwap);
        Assert.Equal(15m, snapshots[1].Vwap);
        Assert.Equal(30m, snapshots[2].Vwap);
    }

    [Fact]
    public void Compute_UsesExchangeDateForVwapReset()
    {
        var engine = new IndicatorEngine();
        var bars = new[]
        {
            CreateFlatPriceBar(new DateTimeOffset(2026, 1, 5, 23, 55, 0, TimeSpan.Zero), 10m, 100m),
            CreateFlatPriceBar(new DateTimeOffset(2026, 1, 6, 0, 30, 0, TimeSpan.Zero), 20m, 100m),
            CreateFlatPriceBar(new DateTimeOffset(2026, 1, 6, 14, 30, 0, TimeSpan.Zero), 30m, 100m)
        };

        var snapshots = engine.Compute(bars);

        Assert.Equal(10m, snapshots[0].Vwap);
        Assert.Equal(15m, snapshots[1].Vwap);
        Assert.Equal(30m, snapshots[2].Vwap);
    }

    [Fact]
    public void Compute_UsesExchangeTimeSlotForRelativeVolumeAcrossDst()
    {
        var engine = CreateEvidenceEngine(5, 5);
        var bars = new List<OhlcvBar>
        {
            CreateFlatPriceBar(new DateTimeOffset(2026, 3, 2, 14, 30, 0, TimeSpan.Zero), 10m, 1000m),
            CreateFlatPriceBar(new DateTimeOffset(2026, 3, 3, 14, 30, 0, TimeSpan.Zero), 10m, 1000m),
            CreateFlatPriceBar(new DateTimeOffset(2026, 3, 4, 14, 30, 0, TimeSpan.Zero), 10m, 1000m),
            CreateFlatPriceBar(new DateTimeOffset(2026, 3, 5, 14, 30, 0, TimeSpan.Zero), 10m, 1000m),
            CreateFlatPriceBar(new DateTimeOffset(2026, 3, 6, 14, 30, 0, TimeSpan.Zero), 10m, 1000m),
            CreateFlatPriceBar(new DateTimeOffset(2026, 3, 9, 13, 30, 0, TimeSpan.Zero), 10m, 1000m),
            CreateFlatPriceBar(new DateTimeOffset(2026, 3, 10, 13, 30, 0, TimeSpan.Zero), 10m, 2000m)
        };

        var snapshots = engine.Compute(bars);

        Assert.Equal(2m, snapshots[^1].SlotRelativeVolume);
        Assert.Equal(1000m, snapshots[^1].SlotMedianVolume);
    }

    [Fact]
    public void Compute_WarmsUpRsiAndAtrWithWilderStyleValues()
    {
        var engine = new IndicatorEngine();
        var bars = Enumerable.Range(0, 20)
            .Select(i => CreateTrendingBar(
                new DateTimeOffset(2026, 6, 1, 13, 30, 0, TimeSpan.Zero).AddMinutes(i * 5),
                100m + i))
            .ToArray();

        var snapshots = engine.Compute(bars);

        Assert.Null(snapshots[13].Rsi);
        Assert.Null(snapshots[13].Atr);
        Assert.Equal(100m, snapshots[14].Rsi);
        Assert.Equal(2m, snapshots[14].Atr);
    }

    [Fact]
    public void Compute_WarmsUpMacdSignalAfterMacdLineExists()
    {
        var engine = new IndicatorEngine();
        var bars = Enumerable.Range(0, 40)
            .Select(i => CreateTrendingBar(
                new DateTimeOffset(2026, 6, 1, 13, 30, 0, TimeSpan.Zero).AddMinutes(i * 5),
                100m + i))
            .ToArray();

        var snapshots = engine.Compute(bars);

        Assert.Null(snapshots[24].MacdLine);
        Assert.NotNull(snapshots[25].MacdLine);
        Assert.Null(snapshots[32].MacdSignal);
        Assert.NotNull(snapshots[33].MacdSignal);
        Assert.NotNull(snapshots[33].MacdHistogram);
    }

    [Fact]
    public void Compute_WarmsUpSimpleMovingAverages()
    {
        var engine = new IndicatorEngine();
        var bars = Enumerable.Range(1, 60)
            .Select(i => CreateTrendingBar(
                new DateTimeOffset(2026, 6, 1, 13, 30, 0, TimeSpan.Zero).AddDays(i - 1),
                i))
            .ToArray();

        var snapshots = engine.Compute(bars);

        Assert.Null(snapshots[8].Sma10);
        Assert.Equal(5.5m, snapshots[9].Sma10);
        Assert.Null(snapshots[8].Ema10);
        Assert.Equal(5.5m, snapshots[9].Ema10);
        Assert.Null(snapshots[18].Sma20);
        Assert.Equal(10.5m, snapshots[19].Sma20);
        Assert.Null(snapshots[48].Sma50);
        Assert.Equal(25.5m, snapshots[49].Sma50);
    }

    [Fact]
    public void Compute_UsesComparableTimeSlot_ForSlotRelativeVolume()
    {
        var engine = CreateEvidenceEngine(6, 5);
        var start = new DateTimeOffset(2026, 5, 1, 13, 30, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();

        for (var day = 0; day < 6; day++)
        {
            var sessionDate = AddTradingDays(start, day);
            bars.Add(CreateBar(sessionDate, 100000m));
            bars.Add(CreateBar(sessionDate.AddHours(2).AddMinutes(45), 1000m));
        }

        bars.Add(CreateBar(AddTradingDays(start, 6), 100000m));
        bars.Add(CreateBar(AddTradingDays(start, 6).AddHours(2).AddMinutes(45), 2000m));

        var snapshots = engine.Compute(bars);

        Assert.Equal(2m, snapshots[^1].SlotRelativeVolume);
        Assert.Equal(1000m, snapshots[^1].SlotMedianVolume);
        Assert.Equal(6, snapshots[^1].RelativeVolumeSampleCount);
    }

    [Fact]
    public void Compute_UsesComparableCumulativeSessionVolumeForPrimaryRelativeVolume()
    {
        var engine = CreateEvidenceEngine(5, 5);
        var start = new DateTimeOffset(2026, 5, 1, 13, 30, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();

        for (var day = 0; day < 5; day++)
        {
            var sessionDate = AddTradingDays(start, day);
            bars.Add(CreateBar(sessionDate, 1000m));
            bars.Add(CreateBar(sessionDate.AddMinutes(5), 3000m));
        }

        bars.Add(CreateBar(AddTradingDays(start, 5), 5000m));

        var snapshots = engine.Compute(bars);

        Assert.Equal(5m, snapshots[^1].RelativeVolume);
        Assert.Equal(1000m, snapshots[^1].CumulativeSameTimeMedianVolume);
        Assert.Equal(5, snapshots[^1].RelativeVolumeSampleCount);
    }

    [Theory]
    [InlineData("1m", 1)]
    [InlineData("5m", 5)]
    public void Compute_UsesSameCompletedSlotAcrossTwentyPriorSessions(
        string timeframe,
        int timeframeMinutes)
    {
        var engine = CreateEvidenceEngine(20, 20);
        var start = new DateTimeOffset(2026, 4, 1, 13, 30, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();
        for (var day = 0; day < 20; day++)
        {
            var timestamp = AddTradingDays(start, day);
            bars.Add(new OhlcvBar(
                "OUST",
                timestamp,
                timeframe,
                10m,
                10.5m,
                9.5m,
                10m,
                1_000m,
                "sip",
                "all",
                CoverageVerifiedThroughUtc: timestamp.AddMinutes(timeframeMinutes)));
        }

        var current = AddTradingDays(start, 20);
        bars.Add(new OhlcvBar(
            "OUST",
            current,
            timeframe,
            10m,
            10.5m,
            9.5m,
            10m,
            2_000m,
            "sip",
            "all",
            CoverageVerifiedThroughUtc: current.AddMinutes(timeframeMinutes)));

        var snapshot = engine.Compute(bars)[^1];

        Assert.Equal(2m, snapshot.RelativeVolume);
        Assert.Equal(2m, snapshot.SlotRelativeVolume);
        Assert.Equal(20, snapshot.RelativeVolumeSampleCount);
        Assert.Equal(20, snapshot.SlotRelativeVolumeSampleCount);
    }

    [Fact]
    public void Compute_CumulativeSameTimeCarriesPriorVolumeAcrossMissingExactSlots()
    {
        var engine = CreateEvidenceEngine(5, 5);
        var start = new DateTimeOffset(2026, 5, 4, 13, 30, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();
        for (var day = 0; day < 5; day++)
        {
            bars.Add(CreateBar(AddTradingDays(start, day), 100m));
        }

        var current = AddTradingDays(start, 5);
        bars.Add(CreateBar(current, 100m));
        bars.Add(CreateBar(current.AddMinutes(5), 200m));

        var snapshot = engine.Compute(bars)[^1];

        Assert.Equal(3m, snapshot.RelativeVolume);
        Assert.Equal(100m, snapshot.CumulativeSameTimeMedianVolume);
        Assert.Equal(5, snapshot.RelativeVolumeSampleCount);
        Assert.Null(snapshot.SlotRelativeVolume);
        Assert.Equal(0, snapshot.SlotRelativeVolumeSampleCount);
    }

    [Fact]
    public void Compute_CumulativeSameTimeRejectsPriorSessionsWithoutVerifiedComparableCoverage()
    {
        var engine = CreateEvidenceEngine(5, 5);
        var start = new DateTimeOffset(2026, 5, 4, 13, 30, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();
        for (var day = 0; day < 5; day++)
        {
            var timestamp = AddTradingDays(start, day);
            bars.Add(CreateBar(timestamp, 100m) with
            {
                CoverageVerifiedThroughUtc = timestamp.AddMinutes(15)
            });
        }

        var current = AddTradingDays(start, 5);
        bars.Add(CreateBar(current, 100m));
        bars.Add(CreateBar(current.AddMinutes(15), 200m));

        var snapshot = engine.Compute(bars)[^1];

        Assert.Null(snapshot.RelativeVolume);
        Assert.Equal(0, snapshot.RelativeVolumeSampleCount);
    }

    [Fact]
    public void Compute_UsesMultiSessionAverageRatherThanYesterdayOnly_ForSlotRelativeVolume()
    {
        var engine = CreateEvidenceEngine(6, 5);
        var start = new DateTimeOffset(2026, 5, 4, 13, 30, 0, TimeSpan.Zero);
        var priorVolumes = new[] { 100m, 100m, 100m, 100m, 100m, 1000m };
        var bars = new List<OhlcvBar>();

        for (var day = 0; day < priorVolumes.Length; day++)
        {
            bars.Add(CreateBar(AddTradingDays(start, day), priorVolumes[day]));
        }

        bars.Add(CreateBar(AddTradingDays(start, 6), 200m));

        var snapshots = engine.Compute(bars);

        Assert.Equal(100m, snapshots[^1].SlotMedianVolume);
        Assert.Equal(2m, snapshots[^1].SlotRelativeVolume);
    }

    [Fact]
    public void Compute_SlotBaselineUsesOnlyConfiguredPriorExchangeSessions()
    {
        var engine = CreateEvidenceEngine(5, 5);
        var start = new DateTimeOffset(2026, 4, 20, 13, 30, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();
        for (var day = 0; day < 5; day++)
        {
            bars.Add(CreateBar(AddTradingDays(start, day), 100m));
        }

        for (var day = 5; day < 10; day++)
        {
            var session = AddTradingDays(start, day);
            bars.Add(CreateBar(session.AddMinutes(15), 100m));
        }

        bars.Add(CreateBar(AddTradingDays(start, 10), 200m));

        var snapshot = engine.Compute(bars)[^1];

        Assert.Null(snapshot.SlotRelativeVolume);
        Assert.Equal(0, snapshot.SlotRelativeVolumeSampleCount);
    }

    [Fact]
    public void Compute_IsolatesPremarketAndRegularCumulativeVolume()
    {
        var engine = CreateEvidenceEngine(5, 5);
        var start = new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();
        for (var day = 0; day < 5; day++)
        {
            var session = AddTradingDays(start, day);
            bars.Add(CreateBar(session, 100m));
            bars.Add(CreateBar(session.AddHours(1).AddMinutes(30), 1_000m));
        }

        var current = AddTradingDays(start, 5);
        bars.Add(CreateBar(current, 200m));
        bars.Add(CreateBar(current.AddHours(1).AddMinutes(30), 2_000m));

        var snapshots = engine.Compute(bars);

        Assert.Equal(2m, snapshots[^2].RelativeVolume);
        Assert.Equal("premarket", snapshots[^2].RelativeVolumeCohort);
        Assert.Equal(2m, snapshots[^1].RelativeVolume);
        Assert.Equal("regular", snapshots[^1].RelativeVolumeCohort);
    }

    [Fact]
    public void Compute_KeepsOvernightCumulativeVolumeContinuousAcrossMidnight()
    {
        var engine = CreateEvidenceEngine(5, 5);
        var firstEvening = new DateTimeOffset(2026, 5, 4, 0, 0, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();
        for (var day = 0; day < 5; day++)
        {
            var tradeDate = AddTradingDays(firstEvening, day);
            bars.Add(CreateBar(tradeDate.AddDays(-1).AddHours(24), 100m)); // 20:00 New York on the prior date.
            bars.Add(CreateBar(tradeDate.AddHours(5), 300m));             // 01:00 New York on the trade date.
        }

        var currentTradeDate = AddTradingDays(firstEvening, 5);
        bars.Add(CreateBar(currentTradeDate.AddDays(-1).AddHours(24), 200m));
        bars.Add(CreateBar(currentTradeDate.AddHours(5), 600m));

        var snapshot = engine.Compute(bars)[^1];

        Assert.Equal("overnight", snapshot.RelativeVolumeCohort);
        Assert.Equal(2m, snapshot.RelativeVolume);
        Assert.Equal(400m, snapshot.CumulativeSameTimeMedianVolume);
    }

    [Fact]
    public void Compute_EarlyClosePostmarketDoesNotCompareAgainstNormalCloseClock()
    {
        var currentTradeDate = new DateOnly(2026, 7, 3);
        var schedules = new Dictionary<DateOnly, MarketSessionSchedule>
        {
            [currentTradeDate] = new(currentTradeDate, true, new TimeOnly(9, 30), new TimeOnly(13, 0))
        };
        var engine = new IndicatorEngine(new MarketEvidenceProfile(
            "unit_test_early_close_v1",
            5,
            5,
            "America/New_York",
            schedules));
        var bars = new List<OhlcvBar>();
        var first = new DateTimeOffset(2026, 6, 26, 20, 0, 0, TimeSpan.Zero);
        for (var day = 0; day < 5; day++)
        {
            bars.Add(CreateBar(AddTradingDays(first, day), 100m));
        }

        bars.Add(CreateBar(new DateTimeOffset(2026, 7, 3, 17, 0, 0, TimeSpan.Zero), 200m));

        var snapshot = engine.Compute(bars)[^1];

        Assert.Equal("postmarket", snapshot.RelativeVolumeCohort);
        Assert.Null(snapshot.SlotRelativeVolume);
        Assert.Equal(0, snapshot.SlotRelativeVolumeSampleCount);
    }

    [Fact]
    public void Compute_WhenFeedsAreMixed_MarksVolumeEvidenceUnavailable()
    {
        var engine = CreateEvidenceEngine(2, 2);
        var start = new DateTimeOffset(2026, 8, 24, 13, 30, 0, TimeSpan.Zero);
        var bars = new[]
        {
            CreateBar(start, 100m) with { DataFeed = "sip" },
            CreateBar(start.AddDays(1), 100m) with { DataFeed = "iex" },
            CreateBar(start.AddDays(2), 200m) with { DataFeed = "sip" }
        };

        var snapshot = engine.Compute(bars)[^1];

        Assert.Null(snapshot.RelativeVolume);
        Assert.Null(snapshot.SlotRelativeVolume);
        Assert.Equal("mixed", snapshot.DataFeed);
        Assert.Equal("mixed_feed", snapshot.MarketEvidenceReliability);
    }

    [Fact]
    public void Compute_WhenAdjustmentPoliciesAreMixed_MarksVolumeEvidenceUnavailable()
    {
        var engine = CreateEvidenceEngine(2, 2);
        var start = new DateTimeOffset(2026, 8, 24, 13, 30, 0, TimeSpan.Zero);
        var bars = new[]
        {
            CreateBar(start, 100m),
            CreateBar(start.AddDays(1), 100m) with { AdjustmentPolicy = "raw" },
            CreateBar(start.AddDays(2), 200m)
        };

        var snapshot = engine.Compute(bars)[^1];

        Assert.Null(snapshot.RelativeVolume);
        Assert.Equal("mixed", snapshot.AdjustmentPolicy);
        Assert.Equal("mixed_adjustment_policy", snapshot.MarketEvidenceReliability);
    }

    [Fact]
    public void Compute_WhenFeedDoesNotMatchProfile_MarksVolumeEvidenceUnavailable()
    {
        var engine = CreateEvidenceEngine(2, 2);
        var start = new DateTimeOffset(2026, 8, 24, 13, 30, 0, TimeSpan.Zero);
        var bars = new[]
        {
            CreateBar(start, 100m) with { DataFeed = "iex" },
            CreateBar(start.AddDays(1), 100m) with { DataFeed = "iex" },
            CreateBar(start.AddDays(2), 200m) with { DataFeed = "iex" }
        };

        var snapshot = engine.Compute(bars)[^1];

        Assert.Null(snapshot.RelativeVolume);
        Assert.Equal("iex", snapshot.DataFeed);
        Assert.Equal("unexpected_feed", snapshot.MarketEvidenceReliability);
    }

    private static IndicatorEngine CreateEvidenceEngine(int lookback, int minimumSamples) =>
        new(new MarketEvidenceProfile(
            "unit_test_rvol_v1",
            lookback,
            minimumSamples,
            "America/New_York"));

    private static DateTimeOffset AddTradingDays(DateTimeOffset start, int tradingDays)
    {
        var current = start;
        var remaining = tradingDays;
        while (remaining > 0)
        {
            current = current.AddDays(1);
            if (current.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                remaining--;
            }
        }

        return current;
    }

    private static OhlcvBar CreateBar(DateTimeOffset timestamp, decimal volume)
    {
        return new OhlcvBar(
            "OUST",
            timestamp,
            "15m",
            10m,
            10.5m,
            9.5m,
            10m,
            volume,
            "sip",
            "all",
            CoverageVerifiedThroughUtc: timestamp.AddDays(1));
    }

    private static OhlcvBar CreateFlatPriceBar(DateTimeOffset timestamp, decimal price, decimal volume)
    {
        return new OhlcvBar(
            "OUST",
            timestamp,
            "5m",
            price,
            price,
            price,
            price,
            volume,
            "sip",
            "all",
            CoverageVerifiedThroughUtc: timestamp.AddDays(1));
    }

    private static OhlcvBar CreateTrendingBar(DateTimeOffset timestamp, decimal close)
    {
        return new OhlcvBar("OUST", timestamp, "5m", close, close + 1m, close - 1m, close, 1000m, "sip", "all");
    }
}

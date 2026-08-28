using TradingFlow.Domain.Market;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Indicators;
using Xunit;

namespace TradingFlow.Tests;

public sealed class TimeframeParserTests
{
    [Theory]
    [InlineData("1m", 1)]
    [InlineData("5m", 5)]
    [InlineData("1h", 60)]
    [InlineData("65m", 65)]
    [InlineData("1d", 1440)]
    public void Parse_SupportsIntradayAndDailyTimeframes(string timeframe, int expectedMinutes)
    {
        var duration = TimeframeParser.Parse(timeframe);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), duration);
    }

    [Fact]
    public void BarResampler_CanDeriveDailyBarsFromIntradayBars()
    {
        var sourceBars = new[]
        {
            new OhlcvBar("TEST", new DateTimeOffset(2026, 6, 8, 13, 30, 0, TimeSpan.Zero), "1h", 10m, 11m, 9m, 10.5m, 1000m),
            new OhlcvBar("TEST", new DateTimeOffset(2026, 6, 8, 14, 30, 0, TimeSpan.Zero), "1h", 10.5m, 12m, 10m, 11.5m, 1500m),
            new OhlcvBar("TEST", new DateTimeOffset(2026, 6, 9, 13, 30, 0, TimeSpan.Zero), "1h", 11.5m, 13m, 11m, 12.5m, 2000m)
        };

        var dailyBars = new BarResampler().Resample(sourceBars, "1d");

        Assert.Equal(2, dailyBars.Count);
        Assert.Equal(new DateTimeOffset(2026, 6, 8, 4, 0, 0, TimeSpan.Zero), dailyBars[0].Timestamp);
        Assert.Equal("1d", dailyBars[0].Timeframe);
        Assert.Equal(10m, dailyBars[0].Open);
        Assert.Equal(12m, dailyBars[0].High);
        Assert.Equal(9m, dailyBars[0].Low);
        Assert.Equal(11.5m, dailyBars[0].Close);
        Assert.Equal(2500m, dailyBars[0].Volume);
    }

    [Fact]
    public void BarResampler_CompleteModeExcludesBucketWithMissingMinute()
    {
        var start = new DateTimeOffset(2026, 8, 27, 13, 30, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 10)
            .Where(index => index != 2)
            .Select(index => new OhlcvBar(
                "AAPL",
                start.AddMinutes(index),
                "1m",
                100m,
                101m,
                99m,
                100m,
                1_000m))
            .ToArray();

        var result = new BarResampler().ResampleComplete(bars, "5m");

        var completed = Assert.Single(result);
        Assert.Equal(start.AddMinutes(5), completed.Timestamp);
        Assert.Equal(5_000m, completed.Volume);
    }

    [Fact]
    public void BarResampler_AuthoritativeSparseHistoryUsesRealBarsWithoutInventingVolume()
    {
        var start = new DateTimeOffset(2026, 8, 27, 13, 30, 0, TimeSpan.Zero);
        var bars = Enumerable.Range(0, 5)
            .Where(index => index != 2)
            .Select(index => new OhlcvBar(
                "AAPL",
                start.AddMinutes(index),
                "1m",
                100m + index,
                101m + index,
                99m + index,
                100.5m + index,
                1_000m))
            .ToArray();

        var result = new BarResampler().ResampleAuthoritativeSparseHistory(
            bars,
            "5m",
            start.AddMinutes(5));

        var completed = Assert.Single(result);
        Assert.Equal(start, completed.Timestamp);
        Assert.Equal(4_000m, completed.Volume);
        Assert.Equal(bars[0].Open, completed.Open);
        Assert.Equal(bars[^1].Close, completed.Close);
    }

    [Fact]
    public void BarResampler_UsesAuthoritativeEarlyCloseBoundary()
    {
        var tradeDate = new DateOnly(2026, 7, 3);
        var profile = new MarketEvidenceProfile(
            "unit_test_calendar_v1",
            20,
            20,
            "America/New_York",
            new Dictionary<DateOnly, MarketSessionSchedule>
            {
                [tradeDate] = new(tradeDate, true, new TimeOnly(9, 30), new TimeOnly(13, 0))
            },
            allowStandardWeekdayFallback: false);
        var resampler = new BarResampler(profile);

        var window = resampler.GetBucketWindow(
            new DateTimeOffset(2026, 7, 3, 17, 0, 0, TimeSpan.Zero),
            "1h");

        Assert.Equal("postmarket", window.SessionSegment);
        Assert.Equal(new DateTimeOffset(2026, 7, 3, 17, 0, 0, TimeSpan.Zero), window.StartUtc);
    }
}

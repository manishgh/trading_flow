using TradingFlow.Domain.Market;
using TradingFlow.Engine.Market;
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
}

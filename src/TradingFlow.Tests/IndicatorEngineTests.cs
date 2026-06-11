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
        var engine = new IndicatorEngine();
        var bars = new List<OhlcvBar>
        {
            CreateFlatPriceBar(new DateTimeOffset(2026, 3, 2, 14, 30, 0, TimeSpan.Zero), 10m, 1000m),
            CreateFlatPriceBar(new DateTimeOffset(2026, 3, 3, 14, 30, 0, TimeSpan.Zero), 10m, 1000m),
            CreateFlatPriceBar(new DateTimeOffset(2026, 3, 4, 14, 30, 0, TimeSpan.Zero), 10m, 1000m),
            CreateFlatPriceBar(new DateTimeOffset(2026, 3, 5, 14, 30, 0, TimeSpan.Zero), 10m, 1000m),
            CreateFlatPriceBar(new DateTimeOffset(2026, 3, 6, 14, 30, 0, TimeSpan.Zero), 10m, 1000m),
            CreateFlatPriceBar(new DateTimeOffset(2026, 3, 10, 13, 30, 0, TimeSpan.Zero), 10m, 2000m)
        };

        var snapshots = engine.Compute(bars);

        Assert.Equal(2m, snapshots[^1].RelativeVolume);
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
        Assert.Null(snapshots[18].Sma20);
        Assert.Equal(10.5m, snapshots[19].Sma20);
        Assert.Null(snapshots[48].Sma50);
        Assert.Equal(25.5m, snapshots[49].Sma50);
    }

    [Fact]
    public void Compute_UsesComparableTimeSlot_ForRelativeVolume()
    {
        var engine = new IndicatorEngine();
        var start = new DateTimeOffset(2026, 5, 1, 13, 30, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();

        for (var day = 0; day < 6; day++)
        {
            var sessionDate = start.AddDays(day);
            bars.Add(CreateBar(sessionDate, 100000m));
            bars.Add(CreateBar(sessionDate.AddHours(2).AddMinutes(45), 1000m));
        }

        bars.Add(CreateBar(start.AddDays(6), 100000m));
        bars.Add(CreateBar(start.AddDays(6).AddHours(2).AddMinutes(45), 2000m));

        var snapshots = engine.Compute(bars);

        Assert.Equal(2m, snapshots[^1].RelativeVolume);
    }

    private static OhlcvBar CreateBar(DateTimeOffset timestamp, decimal volume)
    {
        return new OhlcvBar("OUST", timestamp, "15m", 10m, 10.5m, 9.5m, 10m, volume);
    }

    private static OhlcvBar CreateFlatPriceBar(DateTimeOffset timestamp, decimal price, decimal volume)
    {
        return new OhlcvBar("OUST", timestamp, "5m", price, price, price, price, volume);
    }

    private static OhlcvBar CreateTrendingBar(DateTimeOffset timestamp, decimal close)
    {
        return new OhlcvBar("OUST", timestamp, "5m", close, close + 1m, close - 1m, close, 1000m);
    }
}

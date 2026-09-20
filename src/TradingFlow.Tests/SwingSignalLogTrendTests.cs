using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Tests;

public sealed class SwingSignalLogTrendTests
{
    [Fact]
    public void DailySwingSignal_WhenPriceAndVolumeRise_ComputesPositiveLogTrends()
    {
        var (strategy, bars, snapshots) = Fixture();

        var signal = new SignalGenerator().CreateTradeSignal(
            strategy, bars, snapshots, bars.Count - 1);

        Assert.NotNull(signal);
        Assert.True(signal.PriceLogSlope > 0m);
        Assert.True(signal.VolumeLogSlope > 0m);
        Assert.True(signal.PriceLogR2 > 0.9m);
        Assert.True(signal.VolumeLogR2 > 0.9m);
    }

    [Fact]
    public void DailySwingLogTrends_DoNotReadBeyondCompletedSignalBar()
    {
        var (strategy, bars, snapshots) = Fixture();
        var generator = new SignalGenerator();
        var index = bars.Count - 2;
        var expected = generator.CreateTradeSignal(strategy, bars, snapshots, index);
        var future = bars[^1];
        bars[^1] = future with { Close = 1m, Volume = 1_000_000_000m };

        var actual = generator.CreateTradeSignal(strategy, bars, snapshots, index);

        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected.PriceLogSlope, actual.PriceLogSlope);
        Assert.Equal(expected.VolumeLogSlope, actual.VolumeLogSlope);
        Assert.Equal(expected.PriceLogR2, actual.PriceLogR2);
        Assert.Equal(expected.VolumeLogR2, actual.VolumeLogR2);
    }

    private static (StrategyDefinition Strategy, List<OhlcvBar> Bars,
        List<IndicatorSnapshot> Snapshots) Fixture()
    {
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(
            TestRepository.FindRoot(), "configs", "strategies",
            "minervini-trend-template-vcp.v4-trend-rider.yaml"));
        var start = new DateTimeOffset(2025, 11, 3, 5, 0, 0, TimeSpan.Zero);
        var bars = new List<OhlcvBar>();
        var snapshots = new List<IndicatorSnapshot>();

        // More than six months of deterministic daily inputs; no provider access.
        for (var i = 0; i < 180; i++)
        {
            var timestamp = start.AddDays(i + (i / 5 * 2));
            var close = 100m + i;
            var volume = 1_000m + i * 250m;
            bars.Add(new OhlcvBar("TEST", timestamp, "1d",
                close - 0.5m, close + 0.5m, close - 1m, close, volume));
            snapshots.Add(new IndicatorSnapshot(
                "TEST", timestamp, "1d", close, volume,
                Vwap: close - 1m, Rsi: 60m, Atr: 1m,
                Ema20: close - 1m, Ema50: close - 2m, Ema200: close - 3m,
                BollingerMiddle: close - 1m, BollingerUpper: close + 2m,
                BollingerLower: close - 2m, RelativeVolume: 1.2m,
                MacdLine: 1m, MacdSignal: 0.5m, MacdHistogram: 0.2m));
        }

        return (strategy, bars, snapshots);
    }
}

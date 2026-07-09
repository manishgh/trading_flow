using System.Runtime.CompilerServices;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Regime;

namespace TradingFlow.Tests;

public sealed class RegimeGateServiceTests
{
    private static readonly RegimeRules SpyAboveSma3 = new("SPY", RegimeRules.PriceAboveSma, 3);
    private static readonly DateTimeOffset Now = new(2026, 6, 10, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IsRegimeOnAsync_WithNativeDaily_ReturnsRegimeAndCachesForTheDay()
    {
        // Rising daily closes -> latest close above the 3-day SMA -> regime on.
        var provider = new FakeProvider(new Dictionary<string, IReadOnlyList<OhlcvBar>>
        {
            ["1d"] = RisingDaily("2026-06-01", 9, tf: "1d"),
        });
        var service = new RegimeGateService();

        var first = await service.IsRegimeOnAsync(SpyAboveSma3, provider, new[] { "1d" }, Now, CancellationToken.None);
        var second = await service.IsRegimeOnAsync(SpyAboveSma3, provider, new[] { "1d" }, Now, CancellationToken.None);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(1, provider.CallCount); // second call served from the per-day cache
    }

    [Fact]
    public async Task IsRegimeOnAsync_WhenBenchmarkHasOnlyIntraday_ResamplesToDaily()
    {
        // No native 1d bars; only 5m. The service must resample to daily and still decide the regime.
        var provider = new FakeProvider(new Dictionary<string, IReadOnlyList<OhlcvBar>>
        {
            ["5m"] = RisingIntraday("2026-06-01", 9),
        });
        var service = new RegimeGateService();

        var on = await service.IsRegimeOnAsync(SpyAboveSma3, provider, new[] { "1d", "5m" }, Now, CancellationToken.None);

        Assert.True(on);
    }

    [Fact]
    public async Task IsRegimeOnAsync_WhenBenchmarkBelowSma_ReturnsOff()
    {
        // Falling closes -> latest close below the 3-day SMA -> regime off.
        var provider = new FakeProvider(new Dictionary<string, IReadOnlyList<OhlcvBar>>
        {
            ["1d"] = FallingDaily("2026-06-01", 9),
        });
        var service = new RegimeGateService();

        var on = await service.IsRegimeOnAsync(SpyAboveSma3, provider, new[] { "1d" }, Now, CancellationToken.None);

        Assert.False(on);
    }

    [Fact]
    public async Task IsRegimeOnAsync_WhenBenchmarkDataMissing_FailsClosedAndDoesNotCache()
    {
        var provider = new FakeProvider(new Dictionary<string, IReadOnlyList<OhlcvBar>>());
        var service = new RegimeGateService();

        var first = await service.IsRegimeOnAsync(SpyAboveSma3, provider, new[] { "1d" }, Now, CancellationToken.None);
        var second = await service.IsRegimeOnAsync(SpyAboveSma3, provider, new[] { "1d" }, Now, CancellationToken.None);

        Assert.False(first);
        Assert.False(second);
        // Not cached, so it retried (each call attempts the single "1d" timeframe once).
        Assert.Equal(2, provider.CallCount);
    }

    private static IReadOnlyList<OhlcvBar> RisingDaily(string startDate, int days, string tf)
    {
        var day = DateOnly.Parse(startDate, System.Globalization.CultureInfo.InvariantCulture);
        var bars = new List<OhlcvBar>();
        for (var i = 0; i < days; i++)
        {
            var close = 10m + i;
            var ts = new DateTimeOffset(day.AddDays(i), new TimeOnly(20, 0), TimeSpan.Zero);
            bars.Add(new OhlcvBar("SPY", ts, tf, close, close, close, close, 1_000_000m));
        }

        return bars;
    }

    private static IReadOnlyList<OhlcvBar> FallingDaily(string startDate, int days)
    {
        var day = DateOnly.Parse(startDate, System.Globalization.CultureInfo.InvariantCulture);
        var bars = new List<OhlcvBar>();
        for (var i = 0; i < days; i++)
        {
            var close = 30m - i;
            var ts = new DateTimeOffset(day.AddDays(i), new TimeOnly(20, 0), TimeSpan.Zero);
            bars.Add(new OhlcvBar("SPY", ts, "1d", close, close, close, close, 1_000_000m));
        }

        return bars;
    }

    private static IReadOnlyList<OhlcvBar> RisingIntraday(string startDate, int days)
    {
        var day = DateOnly.Parse(startDate, System.Globalization.CultureInfo.InvariantCulture);
        var bars = new List<OhlcvBar>();
        for (var i = 0; i < days; i++)
        {
            var close = 10m + i;
            // Two 5m bars within the same UTC date; the last is the session close.
            bars.Add(new OhlcvBar("SPY", new DateTimeOffset(day.AddDays(i), new TimeOnly(14, 0), TimeSpan.Zero), "5m", close - 0.5m, close, close - 1m, close - 0.3m, 10_000m));
            bars.Add(new OhlcvBar("SPY", new DateTimeOffset(day.AddDays(i), new TimeOnly(20, 0), TimeSpan.Zero), "5m", close - 0.3m, close + 0.2m, close - 0.4m, close, 10_000m));
        }

        return bars;
    }

    private sealed class FakeProvider(IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> barsByTimeframe) : IMarketDataProvider
    {
        public int CallCount { get; private set; }

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            CallCount++;
            var timeframe = timeframes.First();
            if (barsByTimeframe.TryGetValue(timeframe, out var bars))
            {
                foreach (var bar in bars)
                {
                    if (bar.Timestamp >= start && bar.Timestamp <= end)
                    {
                        yield return bar;
                    }
                }
            }

            await Task.CompletedTask;
        }
    }
}

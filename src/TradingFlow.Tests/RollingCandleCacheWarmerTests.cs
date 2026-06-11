using TradingFlow.Data.Csv;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Tests;

public sealed class RollingCandleCacheWarmerTests
{
    [Fact]
    public async Task WarmAsync_WritesRollingWindowCsvsAndManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "trading-flow-warm-cache-tests", Guid.NewGuid().ToString("N"));
        var end = new DateTimeOffset(2026, 6, 9, 20, 0, 0, TimeSpan.Zero);
        var provider = new FakeMarketDataProvider(end);
        var warmer = new RollingCandleCacheWarmer();

        try
        {
            var result = await warmer.WarmAsync(
                provider,
                new RollingCandleWarmRequest(
                    ["poet"],
                    ["5m"],
                    root,
                    LookbackDays: 60,
                    End: end),
                CancellationToken.None);

            var path = Path.Combine(root, "POET", "bars_5m.csv");
            var indicatorsPath = Path.Combine(root, "POET", "indicators_5m.csv");
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(indicatorsPath));
            Assert.True(File.Exists(Path.Combine(root, "_warmup_manifest.json")));
            Assert.Equal(2, result.BarCount);

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal("ticker,timestamp,open,high,low,close,volume", lines[0]);
            Assert.Equal(3, lines.Length);
            Assert.Contains("2026-06-08T14:30:00.0000000+00:00", lines[1]);
            Assert.Contains("2026-06-09T14:30:00.0000000+00:00", lines[2]);
            Assert.DoesNotContain(lines, line => line.Contains("2026-03-31", StringComparison.OrdinalIgnoreCase));

            var indicatorLines = await File.ReadAllLinesAsync(indicatorsPath);
            Assert.StartsWith("ticker,timestamp,timeframe,close,volume,vwap,rsi,atr", indicatorLines[0]);
            Assert.Equal(3, indicatorLines.Length);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FakeMarketDataProvider(DateTimeOffset end) : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset requestEnd,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Assert.Equal(end.AddDays(-60), start);
            Assert.Equal(end, requestEnd);
            Assert.Equal(["POET"], tickers.ToArray());
            Assert.Equal(["5m"], timeframes.ToArray());

            await Task.Yield();
            yield return CreateBar(end.AddDays(-70), 1m);
            yield return CreateBar(new DateTimeOffset(2026, 6, 8, 14, 30, 0, TimeSpan.Zero), 10m);
            yield return CreateBar(new DateTimeOffset(2026, 6, 9, 14, 30, 0, TimeSpan.Zero), 11m);
        }

        private static OhlcvBar CreateBar(DateTimeOffset timestamp, decimal close)
        {
            return new OhlcvBar(
                "POET",
                timestamp,
                "5m",
                close - 0.1m,
                close + 0.1m,
                close - 0.2m,
                close,
                1000m);
        }
    }
}

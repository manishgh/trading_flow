using TradingFlow.Data.Candles;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Tests;

public sealed class LocalFileCandleStoreTests
{
    [Fact]
    public async Task UpsertBarsAsync_ReplacesDuplicateTimestamp_AndWritesArchiveManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "trading-flow-candles", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileCandleStore(root);
            var context = new CandleStoreContext("paper", "paper_run_1", "alpaca");
            var firstTimestamp = new DateTimeOffset(2026, 6, 8, 13, 30, 0, TimeSpan.Zero);
            var secondTimestamp = firstTimestamp.AddMinutes(1);

            await store.UpsertBarsAsync(
                new CandleStoreWriteRequest(context, "provider", [CreateBar("AMD", "1m", firstTimestamp, 10m)]),
                CancellationToken.None);

            await store.UpsertBarsAsync(
                new CandleStoreWriteRequest(
                    context,
                    "provider",
                    [
                        CreateBar("AMD", "1m", firstTimestamp, 11m),
                        CreateBar("AMD", "1m", secondTimestamp, 12m)
                    ]),
                CancellationToken.None);

            var bars = await store.ReadBarsAsync(
                new CandleStoreReadRequest(
                    "paper",
                    "paper_run_1",
                    "alpaca",
                    "AMD",
                    "1m",
                    firstTimestamp.AddMinutes(-1),
                    secondTimestamp.AddMinutes(1)),
                CancellationToken.None);

            Assert.Equal(2, bars.Count);
            Assert.Equal(11m, bars[0].Close);
            Assert.Equal(12m, bars[1].Close);
            Assert.Contains(
                Directory.EnumerateFiles(Path.Combine(root, "_archive-pending"), "*.json"),
                path => File.ReadAllText(path).Contains("paper_run_1", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static OhlcvBar CreateBar(string ticker, string timeframe, DateTimeOffset timestamp, decimal close)
    {
        return new OhlcvBar(
            ticker,
            timestamp,
            timeframe,
            close - 0.1m,
            close + 0.1m,
            close - 0.2m,
            close,
            1000m);
    }
}

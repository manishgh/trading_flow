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
            var manifests = Directory
                .EnumerateFiles(Path.Combine(root, "_archive-pending"), "*.json")
                .ToArray();
            var manifest = Assert.Single(manifests);
            Assert.Contains("paper_run_1", File.ReadAllText(manifest), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFiles(
                Path.Combine(root, "_archive-preparing"),
                "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UpsertBarsAsync_RejectsWriterBelowDurableStreamFence()
    {
        var root = Path.Combine(Path.GetTempPath(), "trading-flow-candles", Guid.NewGuid().ToString("N"));
        try
        {
            var firstProcess = new LocalFileCandleStore(root);
            var restartedProcess = new LocalFileCandleStore(root);
            var context = new CandleStoreContext("paper", "market-state", "alpaca-sip");
            var timestamp = new DateTimeOffset(2026, 8, 27, 13, 30, 0, TimeSpan.Zero);

            await restartedProcess.UpsertBarsAsync(
                new CandleStoreWriteRequest(
                    context,
                    "stream",
                    [CreateBar("AAPL", "1m", timestamp, 101m)],
                    FencingToken: 8),
                default);

            var tokenPath = Path.Combine(
                root,
                "paper",
                "market-state",
                "alpaca-sip",
                "stream",
                "_stream-fence.token");
            Assert.Equal("8", File.ReadAllText(tokenPath));
            File.WriteAllText($"{tokenPath}.interrupted.tmp", String.Empty);

            await Assert.ThrowsAsync<StaleCandleStoreWriteException>(() =>
                firstProcess.UpsertBarsAsync(
                    new CandleStoreWriteRequest(
                        context,
                        "stream",
                        [CreateBar("AAPL", "1m", timestamp, 99m)],
                        FencingToken: 7),
                    default));

            var bars = await restartedProcess.ReadBarsAsync(
                new CandleStoreReadRequest(
                    "paper",
                    "market-state",
                    "alpaca-sip",
                    "AAPL",
                    "1m",
                    timestamp,
                    timestamp,
                    "stream"),
                default);
            Assert.Equal(101m, Assert.Single(bars).Close);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UpsertBarsAsync_RejectsUnfencedStreamWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "trading-flow-candles", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileCandleStore(root);
            var request = new CandleStoreWriteRequest(
                new CandleStoreContext("paper", "market-state", "alpaca-sip"),
                "stream",
                [CreateBar(
                    "AAPL",
                    "1m",
                    new DateTimeOffset(2026, 8, 27, 13, 30, 0, TimeSpan.Zero),
                    101m)]);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.UpsertBarsAsync(request, default));

            Assert.Contains("fencing token", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ArchiveIntentExistsEvenWhenCandleWriteFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "trading-flow-candles", Guid.NewGuid().ToString("N"));
        try
        {
            var timestamp = new DateTimeOffset(2026, 8, 27, 13, 30, 0, TimeSpan.Zero);
            var blockedFilePath = Path.Combine(
                root,
                "paper",
                "paper-run",
                "alpaca",
                "provider",
                "1m",
                "aapl",
                "2026-08-27.jsonl");
            Directory.CreateDirectory(blockedFilePath);
            var store = new LocalFileCandleStore(root);

            await Assert.ThrowsAnyAsync<IOException>(() => store.UpsertBarsAsync(
                new CandleStoreWriteRequest(
                    new CandleStoreContext("paper", "paper-run", "alpaca"),
                    "provider",
                    [CreateBar("AAPL", "1m", timestamp, 101m)]),
                default));

            var manifest = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(root, "_archive-preparing"),
                "*.json"));
            var manifestJson = File.ReadAllText(manifest);
            Assert.Contains("paper-run", manifestJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("alpaca", manifestJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2026-08-27.jsonl", manifestJson, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(root, "_archive-pending")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StreamJournal_IgnoresAndRepairsIncompleteFinalRecord()
    {
        var root = Path.Combine(Path.GetTempPath(), "trading-flow-candles", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileCandleStore(root);
            var context = new CandleStoreContext("paper", "market-state", "alpaca-sip");
            var timestamp = new DateTimeOffset(2026, 8, 27, 13, 30, 0, TimeSpan.Zero);
            await store.UpsertBarsAsync(
                new CandleStoreWriteRequest(
                    context,
                    "stream",
                    [CreateBar("AAPL", "1m", timestamp, 101m)],
                    FencingToken: 8),
                default);
            var journal = Path.Combine(
                root, "paper", "market-state", "alpaca-sip", "stream", "1m", "aapl", "2026-08-27.jsonl");
            await File.AppendAllTextAsync(journal, "{\"ticker\":\"AAPL\"");

            var beforeRepair = await store.ReadBarsAsync(
                new CandleStoreReadRequest(
                    "paper", "market-state", "alpaca-sip", "AAPL", "1m",
                    timestamp, timestamp.AddMinutes(2), "stream"),
                default);
            Assert.Equal(101m, Assert.Single(beforeRepair).Close);

            await store.UpsertBarsAsync(
                new CandleStoreWriteRequest(
                    context,
                    "stream",
                    [CreateBar("AAPL", "1m", timestamp.AddMinutes(1), 102m)],
                    FencingToken: 8),
                default);
            var afterRepair = await store.ReadBarsAsync(
                new CandleStoreReadRequest(
                    "paper", "market-state", "alpaca-sip", "AAPL", "1m",
                    timestamp, timestamp.AddMinutes(2), "stream"),
                default);

            Assert.Equal([101m, 102m], afterRepair.Select(bar => bar.Close));

            await File.AppendAllTextAsync(journal, "{not-json}\n");
            await store.UpsertBarsAsync(
                new CandleStoreWriteRequest(
                    context,
                    "stream",
                    [CreateBar("AAPL", "1m", timestamp.AddMinutes(2), 103m)],
                    FencingToken: 8),
                default);
            var afterInvalidCompleteTail = await store.ReadBarsAsync(
                new CandleStoreReadRequest(
                    "paper", "market-state", "alpaca-sip", "AAPL", "1m",
                    timestamp, timestamp.AddMinutes(3), "stream"),
                default);
            Assert.Equal([101m, 102m, 103m], afterInvalidCompleteTail.Select(bar => bar.Close));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReadBarsAsync_UsesExplicitSourcePrecedence()
    {
        var root = Path.Combine(Path.GetTempPath(), "trading-flow-candles", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileCandleStore(root);
            var context = new CandleStoreContext("paper", "market-state", "alpaca-sip");
            var timestamp = new DateTimeOffset(2026, 8, 27, 13, 30, 0, TimeSpan.Zero);
            await store.UpsertBarsAsync(
                new CandleStoreWriteRequest(context, "derived", [CreateBar("AAPL", "1m", timestamp, 100m)]),
                default);
            await store.UpsertBarsAsync(
                new CandleStoreWriteRequest(context, "provider", [CreateBar("AAPL", "1m", timestamp, 101m)]),
                default);
            await store.UpsertBarsAsync(
                new CandleStoreWriteRequest(
                    context,
                    "stream",
                    [CreateBar("AAPL", "1m", timestamp, 102m)],
                    FencingToken: 8),
                default);

            var bars = await store.ReadBarsAsync(
                new CandleStoreReadRequest(
                    "paper", "market-state", "alpaca-sip", "AAPL", "1m", timestamp, timestamp),
                default);

            Assert.Equal(102m, Assert.Single(bars).Close);
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

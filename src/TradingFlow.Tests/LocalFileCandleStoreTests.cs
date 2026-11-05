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

    /// <summary>
    /// One request now updates several candle files at the same time. Every file must still end up with exactly
    /// its own merged bars, and the archive manifest must list every file, only after all of them were written.
    /// </summary>
    [Fact]
    public async Task UpsertBarsAsync_WritesManyFilesInOneRequestEachWithItsOwnBars()
    {
        var root = Path.Combine(Path.GetTempPath(), "trading-flow-candles", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileCandleStore(root);
            var context = new CandleStoreContext("paper", "earnings-monitor", "alpaca");
            var day = new DateTimeOffset(2026, 11, 4, 14, 30, 0, TimeSpan.Zero);
            var tickers = Enumerable.Range(0, 40).Select(index => $"T{index:00}").ToArray();
            await store.UpsertBarsAsync(
                new CandleStoreWriteRequest(context, "provider", tickers.Select(ticker => CreateBar(ticker, "5m", day, 1m)).ToArray()),
                CancellationToken.None);

            // Second request: replaces the first bar and adds one on the next UTC day, so each stock touches two files.
            await store.UpsertBarsAsync(
                new CandleStoreWriteRequest(
                    context,
                    "provider",
                    tickers.SelectMany(ticker => new[]
                    {
                        CreateBar(ticker, "5m", day, Int32.Parse(ticker[1..])),
                        CreateBar(ticker, "5m", day.AddDays(1), 100m + Int32.Parse(ticker[1..]))
                    }).ToArray()),
                CancellationToken.None);

            foreach (var ticker in tickers)
            {
                var bars = await store.ReadBarsAsync(
                    new CandleStoreReadRequest("paper", "earnings-monitor", "alpaca", ticker, "5m", day.AddHours(-1), day.AddDays(2)),
                    CancellationToken.None);
                Assert.Equal([Int32.Parse(ticker[1..]), 100m + Int32.Parse(ticker[1..])], bars.Select(bar => bar.Close));
            }

            var manifests = Directory.EnumerateFiles(Path.Combine(root, "_archive-pending"), "*.json").ToArray();
            // Manifests are named by the minute they were written, so both requests may share one.
            var listed = String.Join("\n", manifests.Select(File.ReadAllText)).Replace("\\\\", "/").Replace('\\', '/');
            Assert.All(tickers, ticker => Assert.Contains($"/{ticker.ToLowerInvariant()}/2026-11-05.jsonl", listed, StringComparison.Ordinal));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "_archive-preparing"), "*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// The stream journal appends instead of merging. A fenced write touching many files at once must still
    /// append each stock's own lines, in time order, to that stock's own day file.
    /// </summary>
    [Fact]
    public async Task UpsertBarsAsync_StreamAppendsToManyFilesInOneRequestEachWithItsOwnLines()
    {
        var root = Path.Combine(Path.GetTempPath(), "trading-flow-candles", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileCandleStore(root);
            var context = new CandleStoreContext("paper", "market-state", "alpaca-sip");
            var day = new DateTimeOffset(2026, 11, 4, 14, 30, 0, TimeSpan.Zero);
            var tickers = Enumerable.Range(0, 30).Select(index => $"S{index:00}").ToArray();
            var bars = tickers.SelectMany(ticker => Enumerable.Range(0, 3).SelectMany(minute => new[]
            {
                CreateBar(ticker, "1m", day.AddMinutes(minute), Int32.Parse(ticker[1..]) + minute),
                CreateBar(ticker, "1m", day.AddDays(1).AddMinutes(minute), 100m + Int32.Parse(ticker[1..]) + minute)
            })).Reverse().ToArray();

            await store.UpsertBarsAsync(new CandleStoreWriteRequest(context, "stream", bars, FencingToken: 1), CancellationToken.None);

            foreach (var ticker in tickers)
            {
                foreach (var (date, offset) in new[] { ("2026-11-04", 0m), ("2026-11-05", 100m) })
                {
                    var lines = File.ReadAllLines(Path.Combine(root, "paper", "market-state", "alpaca-sip", "stream", "1m", ticker.ToLowerInvariant(), $"{date}.jsonl"));
                    Assert.Equal(3, lines.Length);
                    Assert.All(lines, line => Assert.Contains($"\"ticker\":\"{ticker}\"", line, StringComparison.Ordinal));
                    var read = await store.ReadBarsAsync(
                        new CandleStoreReadRequest("paper", "market-state", "alpaca-sip", ticker, "1m",
                            DateTimeOffset.Parse($"{date}T00:00:00Z"), DateTimeOffset.Parse($"{date}T23:59:00Z"), "stream"),
                        CancellationToken.None);
                    Assert.Equal(
                        Enumerable.Range(0, 3).Select(minute => offset + Int32.Parse(ticker[1..]) + minute),
                        read.Select(bar => bar.Close));
                }
            }

            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(root, "_archive-pending"), "*.json"));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "_archive-preparing"), "*.json"));
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

    [Fact]
    public async Task ReadBarsAsync_SelectsLatestRevisionKnownByRequestedAsOfTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "trading-flow-candles", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileCandleStore(root);
            var context = new CandleStoreContext("paper", "market-state", "alpaca-sip");
            var timestamp = new DateTimeOffset(2026, 8, 27, 14, 30, 0, TimeSpan.Zero);
            var original = CreateBar("AAPL", "1m", timestamp, 100m) with
            {
                DataFeed = "sip",
                AdjustmentPolicy = "all",
                KnownAtUtc = timestamp.AddMinutes(1)
            };
            var revision = original with
            {
                Close = 101m,
                KnownAtUtc = timestamp.AddMinutes(2)
            };

            await store.UpsertBarsAsync(
                new CandleStoreWriteRequest(context, "stream", [original], FencingToken: 8),
                default);
            await store.UpsertBarsAsync(
                new CandleStoreWriteRequest(context, "stream", [revision], FencingToken: 8),
                default);

            var beforeRevision = await store.ReadBarsAsync(
                new CandleStoreReadRequest(
                    "paper", "market-state", "alpaca-sip", "AAPL", "1m",
                    timestamp, timestamp.AddMinutes(3), "stream",
                    AsOfUtc: timestamp.AddMinutes(1).AddSeconds(30)),
                default);
            var afterRevision = await store.ReadBarsAsync(
                new CandleStoreReadRequest(
                    "paper", "market-state", "alpaca-sip", "AAPL", "1m",
                    timestamp, timestamp.AddMinutes(3), "stream",
                    AsOfUtc: timestamp.AddMinutes(3)),
                default);
            var allVersions = await store.ReadBarsAsync(
                new CandleStoreReadRequest(
                    "paper", "market-state", "alpaca-sip", "AAPL", "1m",
                    timestamp, timestamp.AddMinutes(3), "stream",
                    ReadMode: CandleStoreReadMode.AllVersions),
                default);

            Assert.Equal(100m, Assert.Single(beforeRevision).Close);
            Assert.Equal(101m, Assert.Single(afterRevision).Close);
            Assert.Collection(
                allVersions,
                value => Assert.Equal(100m, value.Close),
                value => Assert.Equal(101m, value.Close));
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

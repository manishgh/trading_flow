using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingFlow.Data.Candles;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.WarmupService.Models;
using TradingFlow.WarmupService.Services;

namespace TradingFlow.Tests;

public sealed class WarmupServiceTests : IDisposable
{
    private readonly List<string> roots = [];

    /// <summary>Verifies atomic watchlist edits using only disposable state.</summary>
    [Fact]
    public async Task Watchlist_store_upserts_and_removes_tickers_atomically()
    {
        var root = CreateTempRoot();
        var store = new WarmupRequestStore(Options.Create(new WarmupOptions
        {
            DataRoot = root,
            DefaultTimeframes = ["1m", "5m"],
            DefaultWarmupDays = 60
        }));

        var upserted = await store.UpsertAsync(
            new WarmupWatchRequest([" poet ", "POET", "rgti"], Reason: "tomorrow trend", RequestedBy: "test"),
            CancellationToken.None);

        Assert.Equal(["POET", "RGTI"], upserted.Select(x => x.Ticker).OrderBy(x => x).ToArray());
        var stored = await store.ReadAsync(CancellationToken.None);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, item => Assert.True(item.Active));

        Assert.True(await store.RemoveAsync("poet", CancellationToken.None));
        Assert.False(await store.RemoveAsync("missing", CancellationToken.None));
        stored = await store.ReadAsync(CancellationToken.None);
        Assert.Single(stored);
        Assert.Equal("RGTI", stored[0].Ticker);
    }

    /// <summary>Verifies cached provider data, captured artifacts and completed run history remain distinct from archive delivery.</summary>
    [Fact]
    public async Task Coordinator_warms_candles_news_artifacts_and_run_history()
    {
        var root = CreateTempRoot();
        var options = Options.Create(new WarmupOptions
        {
            DataRoot = root,
            CacheRoot = Path.Combine(root, "cache"),
            ArchiveRoot = Path.Combine(root, "archive"),
            ProviderName = "test",
            DefaultTimeframes = ["1m", "5m"],
            DefaultWarmupDays = 2,
            DefaultNewsLookbackDays = 2,
            MaxParallelTickers = 2
        });
        var requestStore = new WarmupRequestStore(options);
        var runStore = new WarmupRunStore(options);
        await requestStore.UpsertAsync(
            new WarmupWatchRequest(["POET"], WarmupDays: 2, NewsLookbackDays: 2, Timeframes: ["1m", "5m"], IncludeNews: true),
            CancellationToken.None);

        var candleStore = new LocalFileCandleStore(Path.Combine(root, "candle-store"));
        var coordinator = new WarmupCoordinator(
            new FakeMarketDataProvider(),
            new FakeCatalystProvider(),
            candleStore,
            new WarmupArtifactWriter(options),
            new LocalWarmupArchiveSink(Path.Combine(root, "archive")),
            requestStore,
            runStore,
            options,
            NullLogger<WarmupCoordinator>.Instance);

        var run = await coordinator.RunAsync(
            new WarmupJobRequest("run-1", "test-run", ["POET"], DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal("succeeded", run.Status);
        var ticker = Assert.Single(run.Tickers);
        Assert.True(ticker.Succeeded);
        Assert.True(ticker.BarCount >= 4);
        Assert.Equal(1, ticker.CatalystCount);
        Assert.Contains(ticker.ArtifactPaths, path => path.EndsWith("bars.jsonl", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ticker.ArtifactPaths, path => path.EndsWith("indicators.jsonl", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ticker.ArtifactPaths, path => path.EndsWith("catalysts.jsonl", StringComparison.OrdinalIgnoreCase));
        Assert.All(ticker.ArtifactPaths, path => Assert.True(File.Exists(path), path));

        var cached = await candleStore.ReadBarsAsync(
            new CandleStoreReadRequest("warmup", "POET", "test", "POET", "1m", DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(1)),
            CancellationToken.None);
        Assert.NotEmpty(cached);

        var storedRuns = await runStore.ReadAsync(CancellationToken.None);
        Assert.Single(storedRuns);
        var watchlist = await requestStore.ReadAsync(CancellationToken.None);
        Assert.Equal("warmed", Assert.Single(watchlist).LastStatus);
    }

    /// <summary>Allocates a tracked temporary directory rather than allowing tests to use application data roots.</summary>
    private string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "tradingflow-warmup-tests", Guid.NewGuid().ToString("N"));
        roots.Add(path);
        return path;
    }

    /// <summary>Removes every test-created database/artifact directory after verifying its exact temporary parent.</summary>
    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tradingflow-warmup-tests"));
        foreach (var root in roots)
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != parent) throw new InvalidOperationException("Warmup test cleanup escaped its temporary parent.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class FakeMarketDataProvider : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var ticker in tickers)
            {
                foreach (var timeframe in timeframes)
                {
                    for (var i = 0; i < 3; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return new OhlcvBar(
                            ticker.ToUpperInvariant(),
                            end.AddMinutes(-10 + i),
                            timeframe,
                            10 + i,
                            11 + i,
                            9 + i,
                            10.5m + i,
                            1000 + i);
                    }
                }
            }

            await Task.CompletedTask;
        }
    }

    private sealed class FakeCatalystProvider : ICatalystProvider
    {
        public string ProviderName => "fake";

        public Task<IReadOnlyList<CatalystEvent>> GetCatalystsAsync(
            string ticker,
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<CatalystEvent> events =
            [
                new CatalystEvent(
                    ticker.ToUpperInvariant(),
                    windowEnd.AddHours(-1),
                    CatalystType.NewsReport,
                    "Positive contract catalyst",
                    0.75m,
                    ProviderName,
                    "fake-1",
                    "summary",
                    "test",
                    "https://example.test/news")
            ];
            return Task.FromResult(events);
        }
    }
}

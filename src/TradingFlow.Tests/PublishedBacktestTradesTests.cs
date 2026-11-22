using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class PublishedBacktestTradesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"published-trades-{Guid.NewGuid():N}");
    private readonly ProjectPaths paths;
    private readonly PublishedBacktestTrades reader;

    // A repository whose data folder was moved elsewhere after the runs were recorded.
    public PublishedBacktestTradesTests()
    {
        paths = new ProjectPaths(Path.Combine(root, "repo"), Path.Combine(root, "data"), Path.Combine(root, "cache"));
        reader = new PublishedBacktestTrades(paths, NullLogger<PublishedBacktestTrades>.Instance);
    }

    // A result recorded under the old data folder is found under the new one, verified, read, and then kept in memory.
    [Fact]
    public async Task VerifiedResult_IsReadFromTheMovedDataFolderAndKeptInMemory()
    {
        var job = await PublishAsync("run-1");
        var first = await reader.ReadAsync(job, CancellationToken.None);
        Assert.Null(first.UnavailableReason);
        // The trade list holds every trade even though the result's own summary list is empty.
        Assert.Equal(["AAA", "BBB"], first.Trades!.Select(trade => trade.Ticker));
        Assert.Empty(job.Result!.CompletedTrades);

        Directory.Delete(Path.Combine(paths.DataRoot, "run-1"), recursive: true);
        Assert.Same(first, await reader.ReadAsync(job, CancellationToken.None));
    }

    // Only the most recently used jobs stay in memory; a dropped job is read (and here refused) again.
    [Fact]
    public async Task OnlyTheLastJobsAreKept()
    {
        var oldest = await PublishAsync("run-0");
        await reader.ReadAsync(oldest, CancellationToken.None);
        for (var run = 1; run <= PublishedBacktestTrades.CachedJobs; run++)
            await reader.ReadAsync(await PublishAsync($"run-{run}"), CancellationToken.None);

        Directory.Delete(Path.Combine(paths.DataRoot, "run-0"), recursive: true);
        Assert.Null((await reader.ReadAsync(oldest, CancellationToken.None)).Trades);
    }

    // A changed file is refused with its reason, not remembered, and accepted again once the original bytes are back.
    [Fact]
    public async Task ChangedResult_IsRefusedAndTriedAgain()
    {
        var job = await PublishAsync("run-1");
        var file = Path.Combine(paths.DataRoot, "run-1", "unified-completed-trades.json");
        var original = await File.ReadAllBytesAsync(file);
        await File.WriteAllTextAsync(file, "[]");
        var refused = await reader.ReadAsync(job, CancellationToken.None);
        Assert.Null(refused.Trades);
        Assert.Equal("The published result did not pass its integrity check.", refused.UnavailableReason);

        await File.WriteAllBytesAsync(file, original);
        Assert.NotNull((await reader.ReadAsync(job, CancellationToken.None)).Trades);
    }

    // Concurrent readers share one read, and a caller that stops waiting does not cancel it for the others.
    [Fact]
    public async Task ConcurrentReaders_ShareOneReadThatACancelledWaiterDoesNotStop()
    {
        var job = await PublishAsync("run-1");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(job, cancelled.Token));
        var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => reader.ReadAsync(job, CancellationToken.None)));
        Assert.All(reads, read => Assert.Same(reads[0], read));
        Assert.Equal(2, reads[0].Trades!.Count);
    }

    // A manifest that belongs to another result is refused even when its own files are intact.
    [Fact]
    public async Task ManifestOfAnotherResult_IsRefused()
    {
        var first = await PublishAsync("run-1");
        var second = await PublishAsync("run-2");
        var mixed = first with { Result = first.Result! with { ArtifactManifestPath = second.Result!.ArtifactManifestPath } };
        Assert.Equal("The published result did not pass its integrity check.", (await reader.ReadAsync(mixed, CancellationToken.None)).UnavailableReason);
    }

    [Fact]
    public async Task MissingFileOrManifest_IsReported()
    {
        var job = await PublishAsync("run-1");
        var noManifest = job with { Result = job.Result! with { ArtifactManifestPath = null } };
        Assert.Contains("publication manifest", (await reader.ReadAsync(noManifest, CancellationToken.None)).UnavailableReason);

        var incomplete = job with { Result = job.Result! with { EconomicResultsComplete = false } };
        Assert.Contains("did not complete", (await reader.ReadAsync(incomplete, CancellationToken.None)).UnavailableReason);

        var missing = job with { JobId = Guid.NewGuid(), Result = job.Result! with { ResultPath = Path.Combine(paths.RepositoryRoot, "data", "gone", "result.json") } };
        Assert.Contains("not accessible", (await reader.ReadAsync(missing, CancellationToken.None)).UnavailableReason);
    }

    // Publishes a valid result in the moved data folder and returns a completed job that recorded the old location.
    private async Task<BacktestJobSnapshot> PublishAsync(string run)
    {
        var folder = Path.Combine(paths.DataRoot, run);
        Directory.CreateDirectory(folder);
        var exit = new DateTimeOffset(2026, 6, 10, 20, 0, 0, TimeSpan.Zero);
        var trades = new[] { "AAA", "BBB" }.Select(ticker => new BacktestTrade(ticker, "s", "long", exit.AddDays(-3), exit, 1, 10m, 11m, 9m, 12m,
            "target", 1m, 0m, 1m, StrategyId: "research.x.bt-v1"));
        var (manifest, _) = await BacktestArtifactIntegrityTests.WriteValidManifestAsync(folder, System.Text.Json.JsonSerializer.Serialize(trades));
        var recorded = Path.Combine(paths.RepositoryRoot, "data", run);
        var now = DateTimeOffset.UtcNow;
        var result = new BacktestResult(run, Path.Combine(recorded, "result.json"), now, now, 10_000m, 10_000m, 0m, 0m, 0m, 0, 0m,
            null, 0, 0, 0, 0, 0, 0, [], [], null!, [], [], [], [], ArtifactManifestPath: Path.Combine(recorded, Path.GetFileName(manifest)));
        return new BacktestJobSnapshot(Guid.NewGuid(), run, "config.yaml", "completed", now, now, now, null, "completed", 1, 1, [], result);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

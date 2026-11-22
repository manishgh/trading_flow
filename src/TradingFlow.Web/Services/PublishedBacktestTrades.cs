using System.Text.Json;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

/// <summary>A job's complete trade list from its published result, or a plain reason it could not be used.</summary>
public sealed record PublishedTrades(IReadOnlyList<BacktestTrade>? Trades, string? UnavailableReason);

/// <summary>
/// Reads a finished job's complete trades from the trade list published beside its result. Only the two files used are
/// checked against the publication manifest's recorded hashes, because a full publication check can hash more than a
/// gigabyte of decision records. Read-only: published files are never changed. The trades of the last few jobs are kept in
/// memory; an unusable result is reported with its reason and tried again on the next request.
/// </summary>
public sealed class PublishedBacktestTrades(ProjectPaths paths, ILogger<PublishedBacktestTrades> logger)
{
    internal const int CachedJobs = 8;
    private readonly SingleFlight<Guid, PublishedTrades> reads = new();
    private readonly object gate = new();
    private readonly LinkedList<(Guid JobId, PublishedTrades Trades)> recent = new();

    /// <summary>Returns the job's published trades; the caller's token only stops this caller's wait, never a shared read.</summary>
    public async Task<PublishedTrades> ReadAsync(BacktestJobSnapshot job, CancellationToken token)
    {
        if (TryGetRecent(job.JobId) is { } hit) return hit;
        return await reads.RunAsync(job.JobId, () => LoadAsync(job)).WaitAsync(token);
    }

    // Locates, checks and reads one job's published trades, and keeps a successful read.
    private async Task<PublishedTrades> LoadAsync(BacktestJobSnapshot job)
    {
        if (TryGetRecent(job.JobId) is { } hit) return hit;
        if (BacktestResultAvailability.Observe(job, paths) is { State: not "present" } availability)
            return new(null, availability.Detail);
        if (!job.Result!.EconomicResultsComplete)
            return new(null, "The run did not complete its economic results, so its trades are not shown.");
        if (job.Result.ArtifactManifestPath is not { } manifest)
            return new(null, "The saved result names no publication manifest, so its files cannot be checked.");
        try
        {
            var trades = new PublishedTrades(
                await ReadCheckedTradesAsync(paths.LocateStored(manifest), paths.LocateStored(job.Result.ResultPath)), null);
            Remember(job.JobId, trades);
            return trades;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Published trades of backtest job {JobId} could not be used ({ErrorType}).", job.JobId, exception.GetType().Name);
            return new(null, exception is InvalidDataException
                ? "The published result did not pass its integrity check."
                : "The published result could not be read.");
        }
    }

    // Checks the result and its trade list against the manifest's hashes, confirms the manifest owns this result, and reads
    // the trade list. The trade list, not the result's summary, holds every completed trade whatever the retention mode.
    private static async Task<IReadOnlyList<BacktestTrade>> ReadCheckedTradesAsync(string manifestPath, string resultPath)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var manifest = JsonSerializer.Deserialize<BacktestArtifactManifest>(await File.ReadAllBytesAsync(manifestPath));
        if (manifest is not { PublicationComplete: true, SchemaVersion: 1 })
            throw new InvalidDataException("The publication manifest is empty, incomplete or of an unsupported version.");
        var result = SingleArtifact(manifest, "backtest_result");
        if (!BacktestArtifactIntegrity.ResolveArtifactPath(root, result.Path).Equals(Path.GetFullPath(resultPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The publication manifest belongs to a different result.");
        var trades = SingleArtifact(manifest, "unified_completed_trades");
        await BacktestArtifactIntegrity.VerifyAsync(root, result);
        await BacktestArtifactIntegrity.VerifyAsync(root, trades);
        var list = JsonSerializer.Deserialize<BacktestTrade[]>(
            await File.ReadAllBytesAsync(BacktestArtifactIntegrity.ResolveArtifactPath(root, trades.Path)));
        return list ?? throw new InvalidDataException("The published trade list is empty.");
    }

    // The manifest's one artifact of a kind; a manifest with none or several is not usable.
    private static BacktestArtifactReference SingleArtifact(BacktestArtifactManifest manifest, string kind)
    {
        var matches = manifest.Artifacts.Where(reference => reference.Kind == kind).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException($"The publication manifest has {matches.Length} '{kind}' artifacts.");
    }

    // The remembered trades of one job, moved to the front as most recently used; null when not held.
    private PublishedTrades? TryGetRecent(Guid jobId)
    {
        lock (gate)
        {
            if (Find(jobId) is not { } node) return null;
            recent.Remove(node);
            recent.AddFirst(node);
            return node.Value.Trades;
        }
    }

    // Remembers one job's trades once, dropping the least recently used beyond the limit.
    private void Remember(Guid jobId, PublishedTrades trades)
    {
        lock (gate)
        {
            if (Find(jobId) is { } node) recent.Remove(node);
            recent.AddFirst((jobId, trades));
            while (recent.Count > CachedJobs) recent.RemoveLast();
        }
    }

    // The job's remembered entry, or null. Call under the lock.
    private LinkedListNode<(Guid JobId, PublishedTrades Trades)>? Find(Guid jobId)
    {
        var node = recent.First;
        while (node is not null && node.Value.JobId != jobId) node = node.Next;
        return node;
    }
}

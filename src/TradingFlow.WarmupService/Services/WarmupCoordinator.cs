using Microsoft.Extensions.Options;
using TradingFlow.Engine.Abstractions;
using TradingFlow.WarmupService.Models;
using System.Collections.Concurrent;

namespace TradingFlow.WarmupService.Services;

public sealed class WarmupCoordinator(
    IMarketDataProvider marketDataProvider,
    ICatalystProvider catalystProvider,
    ICandleStore candleStore,
    WarmupArtifactWriter artifactWriter,
    IWarmupArchiveSink archiveSink,
    WarmupRequestStore requestStore,
    WarmupRunStore runStore,
    IOptions<WarmupOptions> options,
    ILogger<WarmupCoordinator> logger)
{
    private readonly WarmupOptions options = options.Value;

    /// <summary>Runs frozen accepted inputs and preserves a completed run when a worker resumes after publication.</summary>
    public async Task<WarmupRunRecord> RunAsync(WarmupJobRequest job, CancellationToken cancellationToken)
    {
        var prior = (await runStore.ReadAsync(cancellationToken)).FirstOrDefault(run => run.RunId == job.RunId);
        if (prior is not null) return prior;
        var started = DateTimeOffset.UtcNow;
        var intents = job.FrozenIntents ?? await requestStore.ResolveIntentsAsync(job, cancellationToken);
        WarmupWorkLimits.Validate(job with { FrozenIntents = intents }, options);
        logger.LogInformation("Warmup run {RunId} started for {TickerCount} ticker(s). Reason={Reason}", job.RunId, intents.Count, job.Reason);

        if (options.MaxParallelTickers <= 0) throw new InvalidOperationException("Warmup parallel ticker count must be positive.");
        var completed = new ConcurrentBag<WarmupTickerResult>();
        long receivedBars = 0;
        // Bounds the actual collected rows even if a provider returns duplicate or unexpected extra records.
        void RecordBar()
        {
            if (Interlocked.Increment(ref receivedBars) > options.MaximumBarsPerJob)
                throw new InvalidOperationException("Warmup returned more bars than the configured per-job work budget.");
        }
        await Parallel.ForEachAsync(intents, new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelTickers,
            CancellationToken = cancellationToken }, async (intent, token) =>
        {
            completed.Add(await WarmTickerAsync(job.RunId, intent, job.QueuedAtUtc, RecordBar, token));
        });
        var results = completed.OrderBy(result => result.Ticker, StringComparer.OrdinalIgnoreCase).ToArray();

        await requestStore.MarkResultsAsync(results, intents, cancellationToken);
        var run = new WarmupRunRecord(
            job.RunId,
            job.Reason,
            started,
            DateTimeOffset.UtcNow,
            results.All(x => x.Succeeded) ? "succeeded" : results.Any(x => x.Succeeded) ? "partial" : "failed",
            intents.Count,
            results.Count(x => x.Succeeded),
            results.Count(x => !x.Succeeded),
            results);
        await runStore.AddAsync(run, cancellationToken);
        logger.LogInformation(
            "Warmup run {RunId} finished. Status={Status}, Succeeded={Succeeded}, Failed={Failed}",
            run.RunId,
            run.Status,
            run.SucceededTickerCount,
            run.FailedTickerCount);
        return run;
    }

    /// <summary>Fills the cache and records local/archive delivery independently using the accepted data cutoff.</summary>
    private async Task<WarmupTickerResult> WarmTickerAsync(
        string runId,
        WarmupTickerIntent intent,
        DateTimeOffset end,
        Action recordBar,
        CancellationToken cancellationToken)
    {
        var start = end.AddDays(-intent.WarmupDays);
        try
        {
            var prior = await runStore.ReadTickerCaptureAsync(runId, intent.Ticker, cancellationToken);
            if (prior is not null)
            {
                if (prior.StartUtc != start || prior.EndUtc != end || !prior.Timeframes.SequenceEqual(intent.Timeframes, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("Recovered warmup capture differs from its accepted dates or intervals.");
                return await PublishCaptureAsync(runId, prior, cancellationToken);
            }
            logger.LogInformation(
                "Warming {Ticker}: days={WarmupDays}, timeframes={Timeframes}, includeNews={IncludeNews}",
                intent.Ticker,
                intent.WarmupDays,
                String.Join(",", intent.Timeframes),
                intent.IncludeNews);

            var bars = new List<TradingFlow.Domain.Market.OhlcvBar>();
            await foreach (var bar in marketDataProvider.GetBarsAsync([intent.Ticker], intent.Timeframes, start, end, cancellationToken))
            {
                recordBar();
                bars.Add(bar);
            }

            await candleStore.UpsertBarsAsync(
                new CandleStoreWriteRequest(
                    new CandleStoreContext("warmup", intent.Ticker, options.ProviderName),
                    "provider",
                    bars),
                cancellationToken);

            IReadOnlyList<TradingFlow.Domain.Market.CatalystEvent> catalysts = Array.Empty<TradingFlow.Domain.Market.CatalystEvent>();
            if (intent.IncludeNews && intent.NewsLookbackDays > 0)
            {
                catalysts = await catalystProvider.GetCatalystsAsync(
                    intent.Ticker,
                    end.AddDays(-intent.NewsLookbackDays),
                    end,
                    cancellationToken);
            }

            var artifacts = await artifactWriter.WriteAsync(runId,
                new WarmupTickerPayload(intent, bars, catalysts, start, end),
                cancellationToken);
            var capture = new WarmupTickerResult(
                intent.Ticker,
                Succeeded: true,
                bars.Count,
                catalysts.Count,
                start,
                end,
                intent.Timeframes,
                artifacts.Paths.Select(Path.GetFullPath).ToArray(),
                Error: null,
                CacheCompleted: true);
            await runStore.SaveTickerCaptureAsync(runId, capture, cancellationToken);
            return await PublishCaptureAsync(runId, capture, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Warmup failed for {Ticker}", intent.Ticker);
            return new WarmupTickerResult(
                intent.Ticker,
                Succeeded: false,
                BarCount: 0,
                CatalystCount: 0,
                start,
                end,
                intent.Timeframes,
                Array.Empty<string>(),
                exception.Message);
        }
    }

    /// <summary>Publishes the saved capture and updates delivery facts without changing its original counts or inputs.</summary>
    private async Task<WarmupTickerResult> PublishCaptureAsync(string runId, WarmupTickerResult capture, CancellationToken token)
    {
        WarmupArchivePublication archive;
        try { archive = await archiveSink.PublishAsync(runId, capture.Ticker, capture.ArtifactPaths, token); }
        catch (Exception error) when (error is not OperationCanceledException)
        { archive = new("local_failed", null, [], error.Message); }
        return capture with { ArchiveStatus = archive.Status, ArchiveManifestPath = archive.ManifestPath, ArchiveError = archive.Error };
    }

    /// <summary>Retries archive delivery from the original captured files without redownloading or replacing the run's data.</summary>
    public async Task<WarmupRunRecord?> RetryArchivesAsync(string runId, CancellationToken token)
    {
        var run = (await runStore.ReadAsync(token)).FirstOrDefault(item => item.RunId == runId);
        if (run is null) return null;
        var updated = new List<WarmupTickerResult>();
        foreach (var ticker in run.Tickers)
        {
            if (ticker.CacheCompleted && ticker.ArchiveStatus is "remote_pending" or "local_failed")
            {
                var publication = await archiveSink.PublishAsync(runId, ticker.Ticker, ticker.ArtifactPaths, token);
                updated.Add(ticker with { ArchiveStatus = publication.Status, ArchiveManifestPath = publication.ManifestPath, ArchiveError = publication.Error });
            }
            else updated.Add(ticker);
        }
        var result = run with { Tickers = updated };
        await runStore.AddAsync(result, token);
        return result;
    }
}

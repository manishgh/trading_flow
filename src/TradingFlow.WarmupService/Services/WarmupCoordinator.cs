using Microsoft.Extensions.Options;
using TradingFlow.Engine.Abstractions;
using TradingFlow.WarmupService.Models;

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

    public async Task<WarmupRunRecord> RunAsync(WarmupJobRequest job, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var intents = await ResolveIntentsAsync(job, cancellationToken);
        logger.LogInformation("Warmup run {RunId} started for {TickerCount} ticker(s). Reason={Reason}", job.RunId, intents.Count, job.Reason);

        var results = new List<WarmupTickerResult>();
        using var throttler = new SemaphoreSlim(Math.Max(1, options.MaxParallelTickers));
        var tasks = intents.Select(async intent =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                return await WarmTickerAsync(job.RunId, intent, cancellationToken);
            }
            finally
            {
                throttler.Release();
            }
        }).ToArray();

        foreach (var task in tasks)
        {
            results.Add(await task);
        }

        await requestStore.MarkResultsAsync(results, cancellationToken);
        var run = new WarmupRunRecord(
            job.RunId,
            job.Reason,
            started,
            DateTimeOffset.UtcNow,
            results.All(x => x.Succeeded) ? "succeeded" : results.Any(x => x.Succeeded) ? "partial" : "failed",
            intents.Count,
            results.Count(x => x.Succeeded),
            results.Count(x => !x.Succeeded),
            results.OrderBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase).ToArray());
        await runStore.AddAsync(run, cancellationToken);
        logger.LogInformation(
            "Warmup run {RunId} finished. Status={Status}, Succeeded={Succeeded}, Failed={Failed}",
            run.RunId,
            run.Status,
            run.SucceededTickerCount,
            run.FailedTickerCount);
        return run;
    }

    private async Task<IReadOnlyList<WarmupTickerIntent>> ResolveIntentsAsync(
        WarmupJobRequest job,
        CancellationToken cancellationToken)
    {
        var all = (await requestStore.ReadAsync(cancellationToken))
            .Where(x => x.Active)
            .ToArray();
        if (job.Tickers is null || job.Tickers.Count == 0)
        {
            return all;
        }

        var requested = job.Tickers.Select(WarmupText.NormalizeTicker).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = all.Where(x => requested.Contains(x.Ticker)).ToList();
        var missing = requested
            .Except(existing.Select(x => x.Ticker), StringComparer.OrdinalIgnoreCase)
            .Select(ticker => new WarmupTickerIntent(
                ticker,
                job.Reason,
                "run-now",
                DateTimeOffset.UtcNow,
                Math.Max(1, options.DefaultWarmupDays),
                Math.Max(0, options.DefaultNewsLookbackDays),
                options.DefaultTimeframes.Select(x => x.ToLowerInvariant()).ToArray(),
                options.IncludeNewsByDefault,
                Active: true));
        existing.AddRange(missing);
        return existing.OrderBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<WarmupTickerResult> WarmTickerAsync(
        string runId,
        WarmupTickerIntent intent,
        CancellationToken cancellationToken)
    {
        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-Math.Max(1, intent.WarmupDays));
        try
        {
            logger.LogInformation(
                "Warming {Ticker}: days={WarmupDays}, timeframes={Timeframes}, includeNews={IncludeNews}",
                intent.Ticker,
                intent.WarmupDays,
                String.Join(",", intent.Timeframes),
                intent.IncludeNews);

            var bars = new List<TradingFlow.Domain.Market.OhlcvBar>();
            await foreach (var bar in marketDataProvider.GetBarsAsync([intent.Ticker], intent.Timeframes, start, end, cancellationToken))
            {
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

            var artifacts = await artifactWriter.WriteAsync(
                new WarmupTickerPayload(intent, bars, catalysts, start, end),
                cancellationToken);
            await archiveSink.PublishAsync(runId, intent.Ticker, artifacts.Paths, cancellationToken);

            return new WarmupTickerResult(
                intent.Ticker,
                Succeeded: true,
                bars.Count,
                catalysts.Count,
                start,
                end,
                intent.Timeframes,
                artifacts.Paths.Select(Path.GetFullPath).ToArray(),
                Error: null);
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
}

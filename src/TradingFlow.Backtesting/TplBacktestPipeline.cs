using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Backtesting;

public sealed class TplBacktestPipeline
{
    public async Task<IReadOnlyList<T>> RunByTickerAsync<T>(
        BacktestRunConfig config,
        Func<string, CancellationToken, Task<T>> processTickerAsync,
        CancellationToken cancellationToken,
        Func<string, Exception, T>? createFailureResult = null)
    {
        var maxDegreeOfParallelism = ResolveWorkerCount(config.Engine.WorkerCount);
        var tickerTimeout = TimeSpan.FromSeconds(config.Engine.TickerTimeoutSeconds <= 0
            ? 120
            : config.Engine.TickerTimeoutSeconds);
        var results = new List<T>();
        var resultsLock = new object();

        await Parallel.ForEachAsync(
            config.Tickers,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegreeOfParallelism,
                CancellationToken = cancellationToken
            },
            async (ticker, token) =>
            {
                try
                {
                    using var tickerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    tickerCancellation.CancelAfter(tickerTimeout);
                    var result = await processTickerAsync(ticker, tickerCancellation.Token);
                    lock (resultsLock)
                    {
                        results.Add(result);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (!config.Engine.FailFast)
                {
                    if (createFailureResult is not null)
                    {
                        lock (resultsLock)
                        {
                            results.Add(createFailureResult(ticker, exception));
                        }
                    }
                }
            });

        return results.ToArray();
    }

    private static int ResolveWorkerCount(int configuredWorkerCount)
    {
        if (configuredWorkerCount > 0)
        {
            return configuredWorkerCount;
        }

        return Math.Max(1, Environment.ProcessorCount - 1);
    }
}

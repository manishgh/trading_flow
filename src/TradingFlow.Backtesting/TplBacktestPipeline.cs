using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Backtesting;

public sealed class TplBacktestPipeline
{
    public async Task<IReadOnlyList<T>> RunByTickerAsync<T>(
        BacktestRunConfig config,
        Func<string, CancellationToken, Task<T>> processTickerAsync,
        CancellationToken cancellationToken)
    {
        var maxDegreeOfParallelism = ResolveWorkerCount(config.Engine.WorkerCount);
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
                    var result = await processTickerAsync(ticker, token);
                    lock (resultsLock)
                    {
                        results.Add(result);
                    }
                }
                catch (Exception) when (!config.Engine.FailFast)
                {
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

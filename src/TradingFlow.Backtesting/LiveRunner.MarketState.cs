using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Pipeline;

namespace TradingFlow.Backtesting;

public sealed partial class LiveRunner
{
    // Prefer each streamed state, then isolate failures in the historical fallback
    // so symbols with usable state can still reach their exit checks.
    private async Task<MarketStatePreparation>
        PrepareMarketStatesAsync(
            BacktestRunConfig run, StrategyDefinition[] strategies, IReadOnlyCollection<string> tickers,
            DateTimeOffset start, DateTimeOffset end, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var requiredTimeframes = ResolveRequiredTimeframes(run, strategies);
        var prepared = new ConcurrentDictionary<string, TickerMarketState>(StringComparer.OrdinalIgnoreCase);
        var needsFallback = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(
            tickers,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = ResolveWorkerCount(run.Engine.WorkerCount),
                CancellationToken = cancellationToken
            },
            async (ticker, token) =>
            {
                var streamed = await TryLoadStreamStateAsync(ticker, requiredTimeframes, run.Engine.IndicatorWarmupBars, end, token);
                if (streamed is null)
                {
                    needsFallback.Add(ticker);
                }
                else
                {
                    prepared[ticker] = streamed;
                }
            });

        if (needsFallback.IsEmpty)
        {
            return new MarketStatePreparation(prepared, new Dictionary<string, string>());
        }

        var unresolved = needsFallback.ToArray();
        var requestedIntervals = run.Intervals
            .Where(interval => !String.IsNullOrWhiteSpace(interval))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        try
        {
            var fallback = await _candlePipeline.RunAsync(
                new CandlePipelineRequest(
                    unresolved,
                    requestedIntervals.Length == 0 ? requiredTimeframes : requestedIntervals,
                    requiredTimeframes,
                    run.DerivedTimeframes.Source,
                    start,
                    end,
                    run.Engine.BoundedCapacity,
                    run.Engine.WorkerCount,
                    IncludeExtendedHours: true,
                    ResolveExchangeTimezone(strategies),
                    CreateCandleStoreContext(run)),
                provider,
                cancellationToken,
                progress);
            foreach (var (ticker, state) in fallback.TickerStates)
            {
                prepared[ticker] = state;
            }
            return new MarketStatePreparation(prepared, fallback.Failures, fallback.SharedProviderFailure);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Historical fallback failed for {RunName}; retaining available streamed market state.", run.RunName);
            return new MarketStatePreparation(prepared, new Dictionary<string, string>(),
                $"Historical market data preparation: {LivePipelineHealth.DescribeFailure(exception)}");
        }
    }

    private sealed record MarketStatePreparation(
        IReadOnlyDictionary<string, TickerMarketState> States,
        IReadOnlyDictionary<string, string> TickerFailures,
        string? SharedFailure = null);

    // A broken stream read for one ticker still permits its normal historical fallback.
    private async Task<TickerMarketState?> TryLoadStreamStateAsync(
        string ticker, IReadOnlyCollection<string> timeframes, int warmupBars,
        DateTimeOffset asOfUtc, CancellationToken cancellationToken)
    {
        if (_marketStateSnapshots is null)
        {
            return null;
        }

        try
        {
            return await _marketStateSnapshots.GetTickerStateAsync(ticker, timeframes, warmupBars, asOfUtc, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Market state stream read failed for {Ticker}; trying historical data.", ticker);
            return null;
        }
    }
}

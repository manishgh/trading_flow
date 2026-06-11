using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;

namespace TradingFlow.Engine.Pipeline;

public sealed record CandlePipelineRequest(
    IReadOnlyCollection<string> Tickers,
    IReadOnlyCollection<string> DownloadTimeframes,
    IReadOnlyCollection<string> RequiredTimeframes,
    string DeriveFromTimeframe,
    DateTimeOffset Start,
    DateTimeOffset End,
    int BoundedCapacity,
    int WorkerCount,
    bool IncludeExtendedHours = true,
    string ExchangeTimezone = "America/New_York");

public sealed record CandleEvent(
    string Ticker,
    string Timeframe,
    OhlcvBar Bar);

public sealed record NormalizedCandleEvent(
    string Ticker,
    string Timeframe,
    OhlcvBar Bar);

public sealed record IndicatorSnapshotEvent(
    string Ticker,
    string Timeframe,
    IReadOnlyList<IndicatorSnapshot> Snapshots);

public sealed record CandlePipelineMetrics(
    int ReadCount,
    int NormalizedCount,
    int GroupedCount,
    int DerivedTimeframeCount,
    int IndicatorWorkItemCount,
    int IndicatorSnapshotSetCount,
    int FailureCount,
    int SessionFilteredCount,
    int MaxReadBufferDepth,
    int MaxNormalizeInputDepth,
    int MaxIndicatorInputDepth);

public sealed record TickerMarketState(
    string Ticker,
    IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> BarsByTimeframe,
    IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> SnapshotsByTimeframe)
{
    public IReadOnlyList<OhlcvBar> AllBars => BarsByTimeframe.Values.SelectMany(x => x).ToArray();
}

public sealed record CandlePipelineResult(
    IReadOnlyDictionary<string, TickerMarketState> TickerStates,
    IReadOnlyDictionary<string, string> Failures,
    CandlePipelineMetrics Metrics)
{
    public IReadOnlyList<OhlcvBar> AllBars => TickerStates.Values.SelectMany(x => x.AllBars).ToArray();
}

internal sealed record TickerTimeframeBars(
    string Ticker,
    string Timeframe,
    IReadOnlyList<OhlcvBar> Bars);

internal sealed record IndicatorComputeResult(
    string Ticker,
    string Timeframe,
    IReadOnlyList<IndicatorSnapshot> Snapshots);

public sealed class CandlePipelineEngine
{
    private const int NormalizedProgressEventInterval = 25_000;
    private static readonly TimeSpan NormalizedProgressMinimumInterval = TimeSpan.FromSeconds(2);

    private readonly IndicatorEngine indicatorEngine = new();
    private readonly BarResampler barResampler = new();

    public async Task<CandlePipelineResult> RunAsync(
        CandlePipelineRequest request,
        IMarketDataProvider provider,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        var workerCount = ResolveWorkerCount(request.WorkerCount);
        var capacity = request.BoundedCapacity <= 0 ? 1000 : request.BoundedCapacity;
        var barsByKey = new ConcurrentDictionary<TickerTimeframeKey, ConcurrentBag<OhlcvBar>>();
        var failures = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metrics = new MutablePipelineMetrics();
        var normalizedProgress = new ThrottledCounterProgress(
            progress,
            NormalizedProgressEventInterval,
            NormalizedProgressMinimumInterval,
            count => $"Normalized {count:N0} candle event(s).");
        var linkOptions = new DataflowLinkOptions
        {
            PropagateCompletion = true
        };

        var readBuffer = new BufferBlock<CandleEvent>(new DataflowBlockOptions
        {
            BoundedCapacity = capacity,
            CancellationToken = cancellationToken
        });

        TransformBlock<CandleEvent, NormalizedCandleEvent?>? normalizeBlock = null;
        normalizeBlock = new TransformBlock<CandleEvent, NormalizedCandleEvent?>(
            candle =>
            {
                try
                {
                    metrics.ObserveNormalizeInputDepth(normalizeBlock!.InputCount);
                    var normalized = Normalize(candle);
                    var count = metrics.IncrementNormalized();
                    normalizedProgress.Report(count);

                    return normalized;
                }
                catch (Exception exception)
                {
                    failures[NormalizeTickerForFailure(candle.Ticker)] = exception.Message;
                    return null;
                }
            },
            new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = capacity,
                MaxDegreeOfParallelism = workerCount,
                EnsureOrdered = false,
                CancellationToken = cancellationToken
            });

        var groupBlock = new ActionBlock<NormalizedCandleEvent?>(
            normalized =>
            {
                if (normalized is null)
                {
                    return;
                }

                if (!request.IncludeExtendedHours && !IsRegularSessionBar(normalized.Bar.Timestamp, normalized.Timeframe, request.ExchangeTimezone))
                {
                    metrics.IncrementSessionFiltered();
                    return;
                }

                var key = new TickerTimeframeKey(normalized.Ticker, normalized.Timeframe);
                var bag = barsByKey.GetOrAdd(key, _ => new ConcurrentBag<OhlcvBar>());
                bag.Add(normalized.Bar);
                metrics.IncrementGrouped();
            },
            new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = capacity,
                MaxDegreeOfParallelism = workerCount,
                EnsureOrdered = false,
                CancellationToken = cancellationToken
            });

        readBuffer.LinkTo(normalizeBlock, linkOptions);
        normalizeBlock.LinkTo(groupBlock, linkOptions);

        var downloadedTimeframes = request.DownloadTimeframes.Count == 0
            ? request.RequiredTimeframes
            : request.DownloadTimeframes;

        progress?.Report($"Reading market data for {request.Tickers.Count} ticker(s), {downloadedTimeframes.Count} timeframe(s).");
        try
        {
            await foreach (var bar in provider.GetBarsAsync(request.Tickers, downloadedTimeframes, request.Start, request.End, cancellationToken))
            {
                metrics.IncrementRead();
                await readBuffer.SendAsync(new CandleEvent(bar.Ticker, bar.Timeframe, bar), cancellationToken);
                metrics.ObserveReadBufferDepth(readBuffer.Count);
            }
        }
        finally
        {
            readBuffer.Complete();
        }

        await groupBlock.Completion;
        normalizedProgress.Flush(metrics.NormalizedCount);

        var grouped = barsByKey
            .GroupBy(x => x.Key.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(
                    item => item.Key.Timeframe,
                    item => (IReadOnlyList<OhlcvBar>)item.Value.OrderBy(x => x.Timestamp).ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        foreach (var ticker in request.Tickers)
        {
            if (!grouped.ContainsKey(ticker) && !failures.ContainsKey(ticker))
            {
                failures[ticker] = $"No market data bars were returned for ticker {ticker}.";
            }
        }

        DeriveMissingTimeframes(request, grouped, failures, metrics);

        var snapshotsByTicker = new ConcurrentDictionary<string, ConcurrentDictionary<string, IReadOnlyList<IndicatorSnapshot>>>(StringComparer.OrdinalIgnoreCase);
        await ComputeIndicatorsAsync(grouped, snapshotsByTicker, workerCount, capacity, metrics, cancellationToken);

        var states = new ConcurrentDictionary<string, TickerMarketState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ticker, barsByTimeframe) in grouped)
        {
            states[ticker] = new TickerMarketState(
                ticker,
                barsByTimeframe,
                snapshotsByTicker.TryGetValue(ticker, out var snapshots)
                    ? snapshots.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, IReadOnlyList<IndicatorSnapshot>>(StringComparer.OrdinalIgnoreCase));
        }

        progress?.Report($"Built candle pipeline state for {states.Count}/{request.Tickers.Count} ticker(s).");
        return new CandlePipelineResult(states, failures, metrics.ToImmutable(failures.Count));
    }

    private async Task ComputeIndicatorsAsync(
        IReadOnlyDictionary<string, Dictionary<string, IReadOnlyList<OhlcvBar>>> grouped,
        ConcurrentDictionary<string, ConcurrentDictionary<string, IReadOnlyList<IndicatorSnapshot>>> snapshotsByTicker,
        int workerCount,
        int capacity,
        MutablePipelineMetrics metrics,
        CancellationToken cancellationToken)
    {
        var linkOptions = new DataflowLinkOptions
        {
            PropagateCompletion = true
        };

        TransformBlock<TickerTimeframeBars, IndicatorComputeResult>? indicatorComputeBlock = null;
        indicatorComputeBlock = new TransformBlock<TickerTimeframeBars, IndicatorComputeResult>(
            item =>
            {
                metrics.ObserveIndicatorInputDepth(indicatorComputeBlock!.InputCount);
                metrics.IncrementIndicatorWorkItem();
                var snapshots = indicatorEngine.Compute(item.Bars);
                return new IndicatorComputeResult(item.Ticker, item.Timeframe, snapshots);
            },
            new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = capacity,
                MaxDegreeOfParallelism = workerCount,
                EnsureOrdered = false,
                CancellationToken = cancellationToken
            });

        var indicatorMaterializeBlock = new ActionBlock<IndicatorComputeResult>(
            result =>
            {
                var tickerSnapshots = snapshotsByTicker.GetOrAdd(
                    result.Ticker,
                    _ => new ConcurrentDictionary<string, IReadOnlyList<IndicatorSnapshot>>(StringComparer.OrdinalIgnoreCase));
                tickerSnapshots[result.Timeframe] = result.Snapshots;
                metrics.IncrementIndicatorSnapshotSet();
            },
            new ExecutionDataflowBlockOptions
            {
                BoundedCapacity = capacity,
                MaxDegreeOfParallelism = workerCount,
                EnsureOrdered = false,
                CancellationToken = cancellationToken
            });

        indicatorComputeBlock.LinkTo(indicatorMaterializeBlock, linkOptions);
        foreach (var item in grouped.SelectMany(tickerGroup => tickerGroup.Value.Select(timeframeGroup => new TickerTimeframeBars(
                     tickerGroup.Key,
                     timeframeGroup.Key,
                     timeframeGroup.Value))))
        {
            await indicatorComputeBlock.SendAsync(item, cancellationToken);
            metrics.ObserveIndicatorInputDepth(indicatorComputeBlock.InputCount);
        }

        indicatorComputeBlock.Complete();
        await indicatorMaterializeBlock.Completion;
    }

    private void DeriveMissingTimeframes(
        CandlePipelineRequest request,
        IDictionary<string, Dictionary<string, IReadOnlyList<OhlcvBar>>> grouped,
        IDictionary<string, string> failures,
        MutablePipelineMetrics metrics)
    {
        foreach (var (ticker, barsByTimeframe) in grouped)
        {
            var missing = request.RequiredTimeframes
                .Where(timeframe => !barsByTimeframe.ContainsKey(timeframe))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (missing.Length == 0)
            {
                continue;
            }

            if (!barsByTimeframe.TryGetValue(request.DeriveFromTimeframe, out var sourceBars))
            {
                failures[ticker] =
                    $"Cannot derive required timeframes [{String.Join(", ", missing)}] because source timeframe {request.DeriveFromTimeframe} was not loaded.";
                continue;
            }

            foreach (var target in missing)
            {
                barsByTimeframe[target] = barResampler.Resample(sourceBars, target);
                metrics.IncrementDerivedTimeframe();
            }
        }
    }

    private static NormalizedCandleEvent Normalize(CandleEvent candle)
    {
        var bar = candle.Bar;
        if (String.IsNullOrWhiteSpace(bar.Ticker))
        {
            throw new InvalidOperationException("Candle ticker is required.");
        }

        if (String.IsNullOrWhiteSpace(bar.Timeframe))
        {
            throw new InvalidOperationException($"Candle timeframe is required for {bar.Ticker}.");
        }

        if (bar.High < bar.Low || bar.Open <= 0 || bar.High <= 0 || bar.Low <= 0 || bar.Close <= 0)
        {
            throw new InvalidOperationException($"Invalid OHLC values for {bar.Ticker} {bar.Timeframe} at {bar.Timestamp:O}.");
        }

        if (bar.Volume < 0)
        {
            throw new InvalidOperationException($"Invalid negative volume for {bar.Ticker} {bar.Timeframe} at {bar.Timestamp:O}.");
        }

        var normalized = bar with
        {
            Ticker = bar.Ticker.Trim().ToUpperInvariant(),
            Timeframe = bar.Timeframe.Trim().ToLowerInvariant(),
            Timestamp = bar.Timestamp.ToUniversalTime()
        };

        return new NormalizedCandleEvent(normalized.Ticker, normalized.Timeframe, normalized);
    }

    private static int ResolveWorkerCount(int configuredWorkerCount)
    {
        if (configuredWorkerCount > 0)
        {
            return configuredWorkerCount;
        }

        return Math.Max(1, Environment.ProcessorCount - 1);
    }

    private static string NormalizeTickerForFailure(string ticker)
    {
        return String.IsNullOrWhiteSpace(ticker)
            ? "UNKNOWN"
            : ticker.Trim().ToUpperInvariant();
    }

    private static bool IsRegularSessionBar(DateTimeOffset timestamp, string timeframe, string timezoneId)
    {
        var exchangeTime = TimeZoneInfo.ConvertTime(timestamp, ResolveTimezone(timezoneId));
        if (exchangeTime.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        if (timeframe.EndsWith("d", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var timeOfDay = exchangeTime.TimeOfDay;
        return timeOfDay >= new TimeSpan(9, 30, 0) && timeOfDay < new TimeSpan(16, 0, 0);
    }

    private static TimeZoneInfo ResolveTimezone(string timezoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException) when (timezoneId.Equals("America/New_York", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    private readonly record struct TickerTimeframeKey(string Ticker, string Timeframe);

    private sealed class MutablePipelineMetrics
    {
        private int readCount;
        private int normalizedCount;
        private int groupedCount;
        private int derivedTimeframeCount;
        private int indicatorWorkItemCount;
        private int indicatorSnapshotSetCount;
        private int sessionFilteredCount;
        private int maxReadBufferDepth;
        private int maxNormalizeInputDepth;
        private int maxIndicatorInputDepth;

        public int IncrementRead() => Interlocked.Increment(ref readCount);

        public int IncrementNormalized() => Interlocked.Increment(ref normalizedCount);

        public int IncrementGrouped() => Interlocked.Increment(ref groupedCount);

        public int IncrementDerivedTimeframe() => Interlocked.Increment(ref derivedTimeframeCount);

        public int IncrementIndicatorWorkItem() => Interlocked.Increment(ref indicatorWorkItemCount);

        public int IncrementIndicatorSnapshotSet() => Interlocked.Increment(ref indicatorSnapshotSetCount);

        public int IncrementSessionFiltered() => Interlocked.Increment(ref sessionFilteredCount);

        public int NormalizedCount => Volatile.Read(ref normalizedCount);

        public void ObserveReadBufferDepth(int observed) => ObserveMax(ref maxReadBufferDepth, observed);

        public void ObserveNormalizeInputDepth(int observed) => ObserveMax(ref maxNormalizeInputDepth, observed);

        public void ObserveIndicatorInputDepth(int observed) => ObserveMax(ref maxIndicatorInputDepth, observed);

        public CandlePipelineMetrics ToImmutable(int failureCount)
        {
            return new CandlePipelineMetrics(
                readCount,
                normalizedCount,
                groupedCount,
                derivedTimeframeCount,
                indicatorWorkItemCount,
                indicatorSnapshotSetCount,
                failureCount,
                sessionFilteredCount,
                maxReadBufferDepth,
                maxNormalizeInputDepth,
                maxIndicatorInputDepth);
        }

        private static void ObserveMax(ref int currentMax, int observed)
        {
            var snapshot = currentMax;
            while (observed > snapshot)
            {
                var original = Interlocked.CompareExchange(ref currentMax, observed, snapshot);
                if (original == snapshot)
                {
                    return;
                }

                snapshot = original;
            }
        }
    }

    private sealed class ThrottledCounterProgress(
        IProgress<string>? progress,
        int eventInterval,
        TimeSpan minimumInterval,
        Func<int, string> format)
    {
        private readonly object gate = new();
        private DateTimeOffset lastReportAt = DateTimeOffset.MinValue;
        private int lastReportedCount;

        public void Report(int count)
        {
            if (progress is null || count <= 0 || count % eventInterval != 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            lock (gate)
            {
                if (count == lastReportedCount || now - lastReportAt < minimumInterval)
                {
                    return;
                }

                lastReportedCount = count;
                lastReportAt = now;
            }

            progress.Report(format(count));
        }

        public void Flush(int count)
        {
            if (progress is null || count <= 0)
            {
                return;
            }

            lock (gate)
            {
                if (count == lastReportedCount)
                {
                    return;
                }

                lastReportedCount = count;
                lastReportAt = DateTimeOffset.UtcNow;
            }

            progress.Report(format(count));
        }
    }
}

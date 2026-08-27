using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Pipeline;

namespace TradingFlow.Tests;

public class CandlePipelineEngineTests
{
    [Fact]
    public async Task RunAsync_BatchesProviderRead_DerivesRequiredTimeframes_AndComputesIndicators()
    {
        var provider = new CapturingMarketDataProvider();
        var engine = new CandlePipelineEngine();

        var result = await engine.RunAsync(
            new CandlePipelineRequest(
                ["AAPL", "MSFT"],
                ["5m"],
                ["5m", "15m"],
                "5m",
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow,
                BoundedCapacity: 10,
                WorkerCount: 2),
            provider,
            CancellationToken.None);

        Assert.Empty(result.Failures);
        Assert.Equal(["AAPL", "MSFT"], provider.RequestedTickers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Equal(["5m"], provider.RequestedTimeframes);
        Assert.Equal(180, result.Metrics.ReadCount);
        Assert.Equal(180, result.Metrics.NormalizedCount);
        Assert.Equal(180, result.Metrics.GroupedCount);
        Assert.Equal(2, result.Metrics.DerivedTimeframeCount);
        Assert.Equal(4, result.Metrics.IndicatorWorkItemCount);
        Assert.Equal(4, result.Metrics.IndicatorSnapshotSetCount);
        Assert.Equal(0, result.Metrics.FailureCount);
        Assert.InRange(result.Metrics.MaxReadBufferDepth, 0, 10);

        foreach (var ticker in new[] { "AAPL", "MSFT" })
        {
            Assert.True(result.TickerStates.TryGetValue(ticker, out var tickerState));
            Assert.True(tickerState.BarsByTimeframe.ContainsKey("5m"));
            Assert.True(tickerState.BarsByTimeframe.ContainsKey("15m"));
            Assert.True(tickerState.SnapshotsByTimeframe.ContainsKey("5m"));
            Assert.True(tickerState.SnapshotsByTimeframe.ContainsKey("15m"));
            Assert.NotEmpty(tickerState.SnapshotsByTimeframe["15m"]);
        }
    }

    [Fact]
    public async Task RunAsync_InvalidTickerBars_RecordTickerFailureWithoutStoppingOtherTickers()
    {
        var provider = new MixedQualityMarketDataProvider();
        var engine = new CandlePipelineEngine();

        var result = await engine.RunAsync(
            new CandlePipelineRequest(
                ["AAPL", "BAD"],
                ["5m"],
                ["5m"],
                "5m",
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow,
                BoundedCapacity: 4,
                WorkerCount: 2),
            provider,
            CancellationToken.None);

        Assert.True(result.TickerStates.ContainsKey("AAPL"));
        Assert.False(result.TickerStates.ContainsKey("BAD"));
        Assert.True(result.Failures.TryGetValue("BAD", out var reason));
        Assert.Contains("Invalid OHLC", reason);
        Assert.Equal(2, result.Metrics.ReadCount);
        Assert.Equal(1, result.Metrics.NormalizedCount);
        Assert.Equal(1, result.Metrics.GroupedCount);
        Assert.Equal(1, result.Metrics.FailureCount);
    }

    [Fact]
    public async Task RunAsync_WhenBatchReadFails_RetriesTickersIndividuallyAndKeepsSuccessfulTicker()
    {
        var provider = new BatchFailingMarketDataProvider();
        var engine = new CandlePipelineEngine();

        var result = await engine.RunAsync(
            new CandlePipelineRequest(
                ["AAPL", "BAD"],
                ["5m"],
                ["5m"],
                "5m",
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow,
                BoundedCapacity: 4,
                WorkerCount: 2),
            provider,
            CancellationToken.None);

        Assert.True(result.TickerStates.ContainsKey("AAPL"));
        Assert.False(result.TickerStates.ContainsKey("BAD"));
        Assert.True(result.Failures.TryGetValue("BAD", out var reason));
        Assert.Contains("symbol is not tradable", reason);
        Assert.Equal(3, provider.RequestCount);
        Assert.Equal(3, result.Metrics.ReadCount);
        Assert.Equal(1, result.Metrics.FailureCount);
    }

    [Fact]
    public async Task RunAsync_DoesNotDeriveFinerTimeframeFromCoarserSource()
    {
        var provider = new CapturingMarketDataProvider();
        var engine = new CandlePipelineEngine();

        var result = await engine.RunAsync(
            new CandlePipelineRequest(
                ["AAPL"],
                ["5m"],
                ["1m", "5m"],
                "5m",
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow,
                BoundedCapacity: 10,
                WorkerCount: 2),
            provider,
            CancellationToken.None);

        Assert.False(result.TickerStates.ContainsKey("AAPL"));
        Assert.True(result.Failures.TryGetValue("AAPL", out var reason));
        Assert.Contains("Cannot derive required timeframe 1m from coarser source timeframe 5m", reason);
    }

    [Fact]
    public async Task RunAsync_FiltersProviderBarThatIsNotCompletedAtRequestEnd()
    {
        var start = new DateTimeOffset(2026, 8, 27, 14, 30, 0, TimeSpan.Zero);
        var result = await new CandlePipelineEngine().RunAsync(
            new CandlePipelineRequest(
                ["AAPL"],
                ["1m"],
                ["1m"],
                "1m",
                start,
                start.AddSeconds(30),
                BoundedCapacity: 4,
                WorkerCount: 1),
            new SingleBarMarketDataProvider(start),
            default);

        Assert.False(result.TickerStates.ContainsKey("AAPL"));
        Assert.Equal(1, result.Metrics.ReadCount);
        Assert.Equal(1, result.Metrics.IncompleteBarFilteredCount);
        Assert.Contains("No market data bars", result.Failures["AAPL"]);
    }

    [Fact]
    public async Task RunAsync_PartialBatchFailureRetriesEveryTickerFromStart()
    {
        var result = await new CandlePipelineEngine().RunAsync(
            new CandlePipelineRequest(
                ["AAPL", "MSFT"],
                ["1m"],
                ["1m"],
                "1m",
                new DateTimeOffset(2026, 8, 27, 14, 29, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 27, 14, 35, 0, TimeSpan.Zero),
                BoundedCapacity: 4,
                WorkerCount: 2),
            new PartiallyFailingBatchMarketDataProvider(),
            default);

        Assert.Empty(result.Failures);
        Assert.Equal(3, result.TickerStates["AAPL"].BarsByTimeframe["1m"].Count);
        Assert.Equal(2, result.TickerStates["MSFT"].BarsByTimeframe["1m"].Count);
    }

    [Fact]
    public async Task RunAsync_WhenExtendedHoursDisabled_FiltersAfterHoursBeforeIndicators()
    {
        var provider = new RegularAndAfterHoursMarketDataProvider();
        var engine = new CandlePipelineEngine();

        var result = await engine.RunAsync(
            new CandlePipelineRequest(
                ["AMD"],
                ["15m"],
                ["15m"],
                "15m",
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow,
                BoundedCapacity: 10,
                WorkerCount: 2,
                IncludeExtendedHours: false,
                ExchangeTimezone: "America/New_York"),
            provider,
            CancellationToken.None);

        Assert.Empty(result.Failures);
        var state = Assert.Single(result.TickerStates.Values);
        Assert.Equal(3, result.Metrics.ReadCount);
        Assert.Equal(3, result.Metrics.NormalizedCount);
        Assert.Equal(2, result.Metrics.GroupedCount);
        Assert.Equal(1, result.Metrics.SessionFilteredCount);
        Assert.Equal(new DateTimeOffset(2026, 6, 8, 19, 45, 0, TimeSpan.Zero), state.BarsByTimeframe["15m"][^1].Timestamp);
        Assert.Equal(102m, state.SnapshotsByTimeframe["15m"][^1].CurrentPrice);
    }

    [Fact]
    public async Task RunAsync_WhenStoreContextProvided_PersistsProviderAndDerivedCandles()
    {
        var provider = new CapturingMarketDataProvider();
        var store = new RecordingCandleStore();
        var engine = new CandlePipelineEngine(store);

        var result = await engine.RunAsync(
            new CandlePipelineRequest(
                ["AAPL"],
                ["5m"],
                ["5m", "15m"],
                "5m",
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow,
                BoundedCapacity: 10,
                WorkerCount: 2,
                StoreContext: new CandleStoreContext("paper", "paper_run_1", "alpaca")),
            provider,
            CancellationToken.None);

        Assert.Empty(result.Failures);
        Assert.Collection(
            store.Requests.OrderBy(request => request.Source, StringComparer.OrdinalIgnoreCase),
            request =>
            {
                Assert.Equal("derived", request.Source);
                Assert.Equal("paper_run_1", request.Context.RunName);
                Assert.Equal(30, request.Bars.Count);
                Assert.All(request.Bars, bar => Assert.Equal("15m", bar.Timeframe));
            },
            request =>
            {
                Assert.Equal("provider", request.Source);
                Assert.Equal("paper_run_1", request.Context.RunName);
                Assert.Equal(90, request.Bars.Count);
                Assert.All(request.Bars, bar => Assert.Equal("5m", bar.Timeframe));
            });
    }

    [Fact]
    public async Task RunAsync_ReplayContractDeduplicatesIdenticalCompletedBars()
    {
        var result = await new CandlePipelineEngine().RunAsync(
            new CandlePipelineRequest(
                ["AAPL"],
                ["1m"],
                ["1m"],
                "1m",
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow,
                BoundedCapacity: 4,
                WorkerCount: 1),
            new DuplicateMarketDataProvider(),
            default);

        Assert.Empty(result.Failures);
        Assert.Equal(2, result.Metrics.ReadCount);
        Assert.Equal(1, result.Metrics.GroupedCount);
        Assert.Single(result.TickerStates["AAPL"].BarsByTimeframe["1m"]);
    }

    [Fact]
    public async Task RunAsync_ConflictingCompletedBarsExcludeTickerDeterministically()
    {
        var result = await new CandlePipelineEngine().RunAsync(
            new CandlePipelineRequest(
                ["AAPL"],
                ["1m"],
                ["1m"],
                "1m",
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow,
                BoundedCapacity: 4,
                WorkerCount: 2),
            new ConflictingMarketDataProvider(),
            default);

        Assert.False(result.TickerStates.ContainsKey("AAPL"));
        Assert.Contains("Conflicting completed", result.Failures["AAPL"]);
    }

    [Fact]
    public async Task RunAsync_AuthoritativeSparseProviderDerivesCompletedBarWithoutSyntheticTrades()
    {
        var start = new DateTimeOffset(2026, 8, 27, 13, 30, 0, TimeSpan.Zero);
        var result = await new CandlePipelineEngine().RunAsync(
            new CandlePipelineRequest(
                ["AAPL"],
                ["1m"],
                ["1m", "5m"],
                "1m",
                start,
                start.AddMinutes(5),
                BoundedCapacity: 4,
                WorkerCount: 2),
            new AuthoritativeSparseMarketDataProvider(start),
            default);

        Assert.Empty(result.Failures);
        var state = result.TickerStates["AAPL"];
        Assert.Equal(4, state.BarsByTimeframe["1m"].Count);
        Assert.Equal(4_000m, Assert.Single(state.BarsByTimeframe["5m"]).Volume);
    }

    private sealed class CapturingMarketDataProvider : IMarketDataProvider
    {
        public IReadOnlyList<string> RequestedTickers { get; private set; } = [];

        public IReadOnlyList<string> RequestedTimeframes { get; private set; } = [];

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RequestedTickers = tickers.ToArray();
            RequestedTimeframes = timeframes.ToArray();

            foreach (var ticker in tickers)
            {
                foreach (var timeframe in timeframes)
                {
                    for (var index = 0; index < 90; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var close = 100m + index * 0.1m;
                        yield return new OhlcvBar(
                            ticker,
                            new DateTimeOffset(2026, 6, 8, 13, 30, 0, TimeSpan.Zero).AddMinutes(index * 5),
                            timeframe,
                            close - 0.05m,
                            close + 0.25m,
                            close - 0.25m,
                            close,
                            100000m + index);
                        await Task.Yield();
                    }
                }
            }
        }
    }

    private sealed class SingleBarMarketDataProvider(DateTimeOffset timestamp) : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new OhlcvBar("AAPL", timestamp, "1m", 100m, 101m, 99m, 100.5m, 1_000m);
        }
    }

    private sealed class PartiallyFailingBatchMarketDataProvider : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (tickers.Count > 1)
            {
                yield return Create("AAPL", 0);
                throw new InvalidOperationException("page 2 failed");
            }

            var ticker = tickers.Single();
            var count = ticker == "AAPL" ? 3 : 2;
            for (var index = 0; index < count; index++)
            {
                yield return Create(ticker, index);
            }
        }

        private static OhlcvBar Create(string ticker, int minute) => new(
            ticker,
            new DateTimeOffset(2026, 8, 27, 14, 30, 0, TimeSpan.Zero).AddMinutes(minute),
            "1m",
            100m + minute,
            101m + minute,
            99m + minute,
            100.5m + minute,
            1_000m);
    }

    private sealed class AuthoritativeSparseMarketDataProvider(DateTimeOffset start) :
        IMarketDataProvider,
        IMarketDataCompletenessProvider
    {
        public bool OmittedIntradayIntervalsMeanNoQualifyingTrades => true;

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset requestStart,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var index in Enumerable.Range(0, 5).Where(index => index != 2))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new OhlcvBar(
                    "AAPL",
                    start.AddMinutes(index),
                    "1m",
                    100m + index,
                    101m + index,
                    99m + index,
                    100.5m + index,
                    1_000m);
                await Task.Yield();
            }
        }
    }

    private sealed class MixedQualityMarketDataProvider : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new OhlcvBar(
                "AAPL",
                new DateTimeOffset(2026, 6, 8, 13, 30, 0, TimeSpan.Zero),
                "5m",
                100m,
                101m,
                99m,
                100.5m,
                100000m);
            yield return new OhlcvBar(
                "BAD",
                new DateTimeOffset(2026, 6, 8, 13, 30, 0, TimeSpan.Zero),
                "5m",
                100m,
                98m,
                102m,
                100.5m,
                100000m);
        }
    }

    private sealed class BatchFailingMarketDataProvider : IMarketDataProvider
    {
        public int RequestCount { get; private set; }

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RequestCount++;
            await Task.Yield();

            if (tickers.Count > 1)
            {
                throw new InvalidOperationException("Alpaca rejected the multi-symbol request.");
            }

            var ticker = tickers.Single();
            if (ticker.Equals("BAD", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("symbol is not tradable");
            }

            for (var index = 0; index < 3; index++)
            {
                yield return new OhlcvBar(
                    ticker,
                    new DateTimeOffset(2026, 6, 8, 13, 30, 0, TimeSpan.Zero).AddMinutes(index * 5),
                    "5m",
                    100m + index,
                    101m + index,
                    99m + index,
                    100.5m + index,
                    100000m + index);
            }
        }
    }

    private sealed class RegularAndAfterHoursMarketDataProvider : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return CreateBar(new DateTimeOffset(2026, 6, 8, 19, 30, 0, TimeSpan.Zero), 101m);
            yield return CreateBar(new DateTimeOffset(2026, 6, 8, 19, 45, 0, TimeSpan.Zero), 102m);
            yield return CreateBar(new DateTimeOffset(2026, 6, 8, 20, 45, 0, TimeSpan.Zero), 999m);
        }

        private static OhlcvBar CreateBar(DateTimeOffset timestamp, decimal close)
        {
            return new OhlcvBar(
                "AMD",
                timestamp,
                "15m",
                close - 0.5m,
                close + 0.5m,
                close - 1m,
                close,
                100000m);
        }
    }

    private sealed class RecordingCandleStore : ICandleStore
    {
        private readonly List<CandleStoreWriteRequest> requests = new();

        public IReadOnlyList<CandleStoreWriteRequest> Requests => requests;

        public Task UpsertBarsAsync(CandleStoreWriteRequest request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OhlcvBar>> ReadBarsAsync(CandleStoreReadRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<OhlcvBar>>(Array.Empty<OhlcvBar>());
        }
    }

    private sealed class DuplicateMarketDataProvider : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var bar = new OhlcvBar(
                "AAPL",
                new DateTimeOffset(2026, 8, 27, 13, 30, 0, TimeSpan.Zero),
                "1m",
                100m,
                101m,
                99m,
                100.5m,
                1_000m);
            yield return bar;
            await Task.Yield();
            yield return bar;
        }
    }

    private sealed class ConflictingMarketDataProvider : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var timestamp = new DateTimeOffset(2026, 8, 27, 13, 30, 0, TimeSpan.Zero);
            yield return new OhlcvBar("AAPL", timestamp, "1m", 100m, 101m, 99m, 100.5m, 1_000m);
            await Task.Yield();
            yield return new OhlcvBar("AAPL", timestamp, "1m", 100m, 102m, 99m, 101.5m, 1_100m);
        }
    }
}

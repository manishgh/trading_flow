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

        Assert.True(result.TickerStates.TryGetValue("AAPL", out var state));
        Assert.True(state.BarsByTimeframe.ContainsKey("5m"));
        Assert.False(state.BarsByTimeframe.ContainsKey("1m"));
        Assert.True(result.Failures.TryGetValue("AAPL", out var reason));
        Assert.Contains("Cannot derive required timeframe 1m from coarser source timeframe 5m", reason);
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
}

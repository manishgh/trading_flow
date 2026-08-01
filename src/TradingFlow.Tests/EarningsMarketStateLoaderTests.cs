using System.Runtime.CompilerServices;
using TradingFlow.Domain.Market;
using TradingFlow.Earnings;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Tests;

public sealed class EarningsMarketStateLoaderTests
{
    [Fact]
    public async Task LoadAsync_BackfillsOnceThenUsesOverlappingIncrementalWindow()
    {
        var now = DateTimeOffset.Parse("2026-07-31T15:00:00Z");
        var sourceBars = Enumerable.Range(0, 100)
            .Select(index => new OhlcvBar(
                "TEST",
                now.AddMinutes(-500 + index * 5),
                "5m",
                100m,
                101m,
                99m,
                100m,
                1000m))
            .ToArray();
        var provider = new RecordingMarketDataProvider(sourceBars);
        var store = new RecordingCandleStore();
        var options = EarningsMonitorOptions.Default;
        var loader = new EarningsMarketStateLoader(new Lazy<IMarketDataProvider>(() => provider), store, options);

        var first = await loader.LoadAsync(["TEST"], now, CancellationToken.None);
        var second = await loader.LoadAsync(["TEST"], now.AddMinutes(5), CancellationToken.None);

        Assert.Equal(100, first["TEST"].Count);
        Assert.Equal(100, second["TEST"].Count);
        Assert.Equal(now.AddDays(-100), provider.Requests[0].Start);
        Assert.Equal(sourceBars[^1].Timestamp.AddMinutes(-5), provider.Requests[1].Start);
        Assert.Equal(2, store.WriteCount);
        Assert.Equal(100, store.Bars.Count);
    }

    [Fact]
    public void DefaultLookback_CoversTheFullSameSlotRelativeVolumeBaseline()
    {
        Assert.True(EarningsMonitorOptions.Default.IntradayLookbackDays >= 90);
        Assert.Equal(
            TradingFlow.Engine.Indicators.IndicatorEngine.RelativeVolumeLookbackSessions,
            EarningsMonitorOptions.Default.MinimumSlotRelativeVolumeSamples);
    }

    private sealed class RecordingMarketDataProvider(IReadOnlyList<OhlcvBar> source) : IMarketDataProvider
    {
        public List<(DateTimeOffset Start, DateTimeOffset End)> Requests { get; } = [];

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add((start, end));
            foreach (var bar in source.Where(bar => bar.Timestamp >= start && bar.Timestamp <= end))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return bar;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class RecordingCandleStore : ICandleStore
    {
        public Dictionary<DateTimeOffset, OhlcvBar> Bars { get; } = [];
        public int WriteCount { get; private set; }

        public Task UpsertBarsAsync(CandleStoreWriteRequest request, CancellationToken cancellationToken)
        {
            WriteCount++;
            foreach (var bar in request.Bars)
            {
                Bars[bar.Timestamp] = bar;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OhlcvBar>> ReadBarsAsync(
            CandleStoreReadRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OhlcvBar>>(
                Bars.Values
                    .Where(bar => bar.Ticker == request.Ticker &&
                        bar.Timeframe == request.Timeframe &&
                        bar.Timestamp >= request.Start &&
                        bar.Timestamp <= request.End)
                    .OrderBy(bar => bar.Timestamp)
                    .ToArray());
    }
}

using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Earnings;

/// <summary>
/// Builds earnings-analysis market state once, persists provider bars, and then advances
/// each symbol with small overlapping REST windows. The overlap makes provider revisions
/// idempotent while avoiding a full historical download on every monitor iteration.
/// </summary>
public sealed class EarningsMarketStateLoader
{
    private static readonly CandleStoreContext StoreContext = new("paper", "earnings-monitor", "alpaca");
    private readonly Lazy<IMarketDataProvider> marketData;
    private readonly ICandleStore candleStore;
    private readonly EarningsMonitorOptions options;
    private readonly SemaphoreSlim loadLock = new(1, 1);
    private readonly Dictionary<string, SortedDictionary<DateTimeOffset, OhlcvBar>> state =
        new(StringComparer.OrdinalIgnoreCase);

    public EarningsMarketStateLoader(
        Lazy<IMarketDataProvider> marketData,
        ICandleStore candleStore,
        EarningsMonitorOptions options)
    {
        this.marketData = marketData;
        this.candleStore = candleStore;
        this.options = options;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>>> LoadAsync(
        IReadOnlyCollection<string> tickers,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var normalized = tickers
            .Select(ticker => ticker.Trim().ToUpperInvariant())
            .Where(ticker => ticker.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0)
        {
            return new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase);
        }

        await loadLock.WaitAsync(cancellationToken);
        try
        {
            var end = nowUtc.ToUniversalTime();
            var windowStart = end.AddDays(-options.IntradayLookbackDays);
            await HydrateMissingSymbolsAsync(normalized, windowStart, end, cancellationToken);

            var cold = normalized.Where(ticker => state[ticker].Count == 0).ToArray();
            var warm = normalized.Where(ticker => state[ticker].Count > 0).ToArray();
            if (cold.Length > 0)
            {
                await FetchMergeAndPersistAsync(cold, windowStart, end, cancellationToken);
            }

            if (warm.Length > 0)
            {
                var incrementalStart = warm
                    .Select(ticker => state[ticker].Keys.Last())
                    .Min()
                    .AddMinutes(-5);
                await FetchMergeAndPersistAsync(warm, incrementalStart, end, cancellationToken);
            }

            foreach (var ticker in normalized)
            {
                var expired = state[ticker].Keys.Where(timestamp => timestamp < windowStart).ToArray();
                foreach (var timestamp in expired)
                {
                    state[ticker].Remove(timestamp);
                }
            }

            return normalized.ToDictionary(
                ticker => ticker,
                ticker => (IReadOnlyList<OhlcvBar>)state[ticker].Values.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            loadLock.Release();
        }
    }

    private async Task HydrateMissingSymbolsAsync(
        IReadOnlyCollection<string> tickers,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        foreach (var ticker in tickers.Where(ticker => !state.ContainsKey(ticker)))
        {
            var persisted = await candleStore.ReadBarsAsync(
                new CandleStoreReadRequest(
                    StoreContext.Scope,
                    StoreContext.RunName,
                    StoreContext.ProviderName,
                    ticker,
                    "5m",
                    start,
                    end),
                cancellationToken);
            state[ticker] = new SortedDictionary<DateTimeOffset, OhlcvBar>(
                persisted.ToDictionary(bar => bar.Timestamp, bar => bar));
        }
    }

    private async Task FetchMergeAndPersistAsync(
        IReadOnlyCollection<string> tickers,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var downloaded = new List<OhlcvBar>();
        await foreach (var bar in marketData.Value.GetBarsAsync(tickers, ["5m"], start, end, cancellationToken))
        {
            if (!state.TryGetValue(bar.Ticker, out var tickerState))
            {
                tickerState = new SortedDictionary<DateTimeOffset, OhlcvBar>();
                state[bar.Ticker] = tickerState;
            }

            tickerState[bar.Timestamp] = bar;
            downloaded.Add(bar);
        }

        if (downloaded.Count > 0)
        {
            await candleStore.UpsertBarsAsync(
                new CandleStoreWriteRequest(StoreContext, "provider", downloaded),
                cancellationToken);
        }
    }
}

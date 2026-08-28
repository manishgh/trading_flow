using System.Runtime.CompilerServices;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Market;

namespace TradingFlow.Engine.Pipeline;

/// <summary>
/// Converts historical completed bars into the same event contract consumed by
/// live market-state processing. Ordering is deterministic across symbols.
/// </summary>
public sealed class MarketDataReplayAdapter
{
    /// <summary>
    /// Reads every durable point-in-time version and emits the original followed
    /// by revisions in the order each version became observable.
    /// </summary>
    public async IAsyncEnumerable<MarketBarEvent> ReplayStoreAsync(
        ICandleStore candleStore,
        CandleStoreReadRequest request,
        string provider,
        string feed,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candleStore);
        ArgumentNullException.ThrowIfNull(request);
        var versions = await candleStore.ReadBarsAsync(
            request with { ReadMode = CandleStoreReadMode.AllVersions },
            cancellationToken);
        await foreach (var marketEvent in ReplayAsync(
                           versions,
                           provider,
                           feed,
                           cancellationToken))
        {
            yield return marketEvent;
        }
    }

    public MarketBarEvent Create(
        OhlcvBar bar,
        string provider,
        string feed)
        => Create(bar, provider, feed, MarketBarEventKind.Replay);

    private static MarketBarEvent Create(
        OhlcvBar bar,
        string provider,
        string feed,
        MarketBarEventKind kind)
    {
        ArgumentNullException.ThrowIfNull(bar);
        var observedAt = bar.KnownAtUtc?.ToUniversalTime() ??
            bar.Timestamp.ToUniversalTime().Add(TimeframeParser.Parse(bar.Timeframe));
        return new MarketBarEvent(
            provider,
            feed,
            bar,
            kind,
            observedAt,
            0);
    }

    public async IAsyncEnumerable<MarketBarEvent> ReplayAsync(
        IEnumerable<OhlcvBar> bars,
        string provider,
        string feed,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var events = bars
            .GroupBy(value => new
            {
                Ticker = value.Ticker.Trim().ToUpperInvariant(),
                Timeframe = value.Timeframe.Trim().ToLowerInvariant(),
                Timestamp = value.Timestamp.ToUniversalTime()
            })
            .SelectMany(group => group
                .OrderBy(value => value.KnownAtUtc ??
                    value.Timestamp.ToUniversalTime().Add(TimeframeParser.Parse(value.Timeframe)))
                .Select((bar, index) => Create(
                    bar,
                    provider,
                    feed,
                    index == 0 ? MarketBarEventKind.Replay : MarketBarEventKind.ReplayRevision)))
            .OrderBy(value => value.ObservedAtUtc)
            .ThenBy(value => value.Bar.Timestamp)
            .ThenBy(value => value.Bar.Ticker, StringComparer.Ordinal)
            .ThenBy(value => value.Bar.Timeframe, StringComparer.Ordinal)
            .ToArray();

        foreach (var marketEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return marketEvent;
            await Task.Yield();
        }
    }
}

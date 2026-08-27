using System.Runtime.CompilerServices;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Market;

namespace TradingFlow.Engine.Pipeline;

/// <summary>
/// Converts historical completed bars into the same event contract consumed by
/// live market-state processing. Ordering is deterministic across symbols.
/// </summary>
public sealed class MarketDataReplayAdapter
{
    public MarketBarEvent Create(
        OhlcvBar bar,
        string provider,
        string feed)
    {
        ArgumentNullException.ThrowIfNull(bar);
        var observedAt = bar.Timestamp.ToUniversalTime().Add(TimeframeParser.Parse(bar.Timeframe));
        return new MarketBarEvent(
            provider,
            feed,
            bar,
            MarketBarEventKind.Replay,
            observedAt,
            0);
    }

    public async IAsyncEnumerable<MarketBarEvent> ReplayAsync(
        IEnumerable<OhlcvBar> bars,
        string provider,
        string feed,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var bar in bars
                     .OrderBy(value => value.Timestamp)
                     .ThenBy(value => value.Ticker, StringComparer.Ordinal)
                     .ThenBy(value => value.Timeframe, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Create(bar, provider, feed);
            await Task.Yield();
        }
    }
}

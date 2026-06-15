using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Market;

public sealed class BarResampler
{
    public IReadOnlyList<OhlcvBar> Resample(IReadOnlyList<OhlcvBar> sourceBars, string targetTimeframe)
    {
        var bucketSize = TimeframeParser.Parse(targetTimeframe);
        return sourceBars
            .OrderBy(x => x.Timestamp)
            .GroupBy(x => FloorTimestamp(x.Timestamp, bucketSize))
            .Select(group =>
            {
                var ordered = group.OrderBy(x => x.Timestamp).ToArray();
                var first = ordered[0];
                var last = ordered[^1];
                return new OhlcvBar(
                    first.Ticker,
                    group.Key,
                    targetTimeframe,
                    first.Open,
                    ordered.Max(x => x.High),
                    ordered.Min(x => x.Low),
                    last.Close,
                    ordered.Sum(x => x.Volume));
            })
            .OrderBy(x => x.Timestamp)
            .ToArray();
    }

    private static DateTimeOffset FloorTimestamp(DateTimeOffset timestamp, TimeSpan bucketSize)
    {
        var ticks = timestamp.UtcTicks - (timestamp.UtcTicks % bucketSize.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}

using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Market;

public sealed class BarResampler
{
    public sealed record BucketWindow(
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string SessionSegment);

    public IReadOnlyList<OhlcvBar> Resample(IReadOnlyList<OhlcvBar> sourceBars, string targetTimeframe)
    {
        var bucketSize = TimeframeParser.Parse(targetTimeframe);
        return sourceBars
            .OrderBy(x => x.Timestamp)
            .GroupBy(x => GetBucketWindow(x.Timestamp, bucketSize).StartUtc)
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

    /// <summary>
    /// Resamples only buckets containing every expected source bar. This is the
    /// strategy-safe path: a single received minute must never masquerade as a
    /// completed five-minute or hourly candle after the wall clock passes.
    /// </summary>
    public IReadOnlyList<OhlcvBar> ResampleComplete(
        IReadOnlyList<OhlcvBar> sourceBars,
        string targetTimeframe)
    {
        if (sourceBars.Count == 0)
        {
            return [];
        }

        var sourceSize = TimeframeParser.Parse(sourceBars[0].Timeframe);
        var targetSize = TimeframeParser.Parse(targetTimeframe);
        if (targetSize.TotalDays >= 1 || targetSize <= sourceSize)
        {
            return Resample(sourceBars, targetTimeframe);
        }

        var completeSource = sourceBars
            .Where(bar => TimeframeParser.Parse(bar.Timeframe) == sourceSize)
            .GroupBy(bar => GetBucketWindow(bar.Timestamp, targetSize).StartUtc)
            .Where(group => IsComplete(group, GetBucketWindow(group.Key, targetSize), sourceSize))
            .SelectMany(group => group)
            .ToArray();
        return Resample(completeSource, targetTimeframe);
    }

    /// <summary>
    /// Resamples an authoritative historical response where omitted intraday
    /// intervals explicitly mean that no qualifying trade occurred. It never
    /// invents a flat source bar: OHLCV is aggregated from real trades only,
    /// and the containing bucket must be complete by wall-clock time.
    /// </summary>
    public IReadOnlyList<OhlcvBar> ResampleAuthoritativeSparseHistory(
        IReadOnlyList<OhlcvBar> sourceBars,
        string targetTimeframe,
        DateTimeOffset completedThroughUtc)
    {
        if (sourceBars.Count == 0)
        {
            return [];
        }

        var sourceSize = TimeframeParser.Parse(sourceBars[0].Timeframe);
        var targetSize = TimeframeParser.Parse(targetTimeframe);
        if (targetSize <= sourceSize)
        {
            return Resample(
                sourceBars
                    .Where(bar => bar.Timestamp.ToUniversalTime() + sourceSize <= completedThroughUtc.ToUniversalTime())
                    .ToArray(),
                targetTimeframe);
        }

        var completeSource = sourceBars
            .Where(bar => TimeframeParser.Parse(bar.Timeframe) == sourceSize)
            .GroupBy(bar => GetBucketWindow(bar.Timestamp, targetSize).StartUtc)
            .Where(group => GetBucketWindow(group.Key, targetSize).EndUtc <= completedThroughUtc.ToUniversalTime())
            .SelectMany(group => group)
            .ToArray();
        return Resample(completeSource, targetTimeframe);
    }

    public BucketWindow GetBucketWindow(DateTimeOffset timestamp, string targetTimeframe) =>
        GetBucketWindow(timestamp, TimeframeParser.Parse(targetTimeframe));

    private static readonly TimeZoneInfo ExchangeTimeZone = ResolveExchangeTimeZone();

    private static TimeZoneInfo ResolveExchangeTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    private static BucketWindow GetBucketWindow(DateTimeOffset timestamp, TimeSpan bucketSize)
    {
        var exchangeTime = TimeZoneInfo.ConvertTime(timestamp, ExchangeTimeZone);

        if (bucketSize.TotalDays >= 1)
        {
            var midnight = exchangeTime.Date;
            var nextMidnight = midnight.AddDays(1);
            return new BucketWindow(
                ToUtc(midnight),
                ToUtc(nextMidnight),
                "daily");
        }

        var timeOfDay = exchangeTime.TimeOfDay;
        DateTime segmentStart;
        DateTime segmentEnd;
        string segment;
        if (timeOfDay >= new TimeSpan(20, 0, 0))
        {
            segmentStart = exchangeTime.Date.AddHours(20);
            segmentEnd = exchangeTime.Date.AddDays(1).AddHours(4);
            segment = "overnight";
        }
        else if (timeOfDay < new TimeSpan(4, 0, 0))
        {
            segmentStart = exchangeTime.Date.AddDays(-1).AddHours(20);
            segmentEnd = exchangeTime.Date.AddHours(4);
            segment = "overnight";
        }
        else if (timeOfDay < new TimeSpan(9, 30, 0))
        {
            segmentStart = exchangeTime.Date.AddHours(4);
            segmentEnd = exchangeTime.Date.AddHours(9).AddMinutes(30);
            segment = "premarket";
        }
        else if (timeOfDay < new TimeSpan(16, 0, 0))
        {
            segmentStart = exchangeTime.Date.AddHours(9).AddMinutes(30);
            segmentEnd = exchangeTime.Date.AddHours(16);
            segment = "regular";
        }
        else
        {
            segmentStart = exchangeTime.Date.AddHours(16);
            segmentEnd = exchangeTime.Date.AddHours(20);
            segment = "postmarket";
        }

        var elapsed = exchangeTime.DateTime - segmentStart;
        var bucketTicks = elapsed.Ticks - (elapsed.Ticks % bucketSize.Ticks);
        var bucketStart = segmentStart.AddTicks(bucketTicks);
        var bucketEnd = bucketStart.Add(bucketSize);
        if (bucketEnd > segmentEnd)
        {
            bucketEnd = segmentEnd;
        }

        return new BucketWindow(ToUtc(bucketStart), ToUtc(bucketEnd), segment);
    }

    private static DateTimeOffset ToUtc(DateTime exchangeDateTime) =>
        new DateTimeOffset(exchangeDateTime, ExchangeTimeZone.GetUtcOffset(exchangeDateTime)).ToUniversalTime();

    private static bool IsComplete(
        IEnumerable<OhlcvBar> bars,
        BucketWindow window,
        TimeSpan sourceSize)
    {
        var expected = (int)((window.EndUtc - window.StartUtc).Ticks / sourceSize.Ticks);
        if (expected <= 0)
        {
            return false;
        }

        var timestamps = bars
            .Select(bar => bar.Timestamp.ToUniversalTime())
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        if (timestamps.Length != expected)
        {
            return false;
        }

        for (var index = 0; index < expected; index++)
        {
            if (timestamps[index] != window.StartUtc.AddTicks(sourceSize.Ticks * index))
            {
                return false;
            }
        }

        return true;
    }
}

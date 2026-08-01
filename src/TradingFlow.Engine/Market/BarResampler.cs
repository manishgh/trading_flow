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

    private static DateTimeOffset FloorTimestamp(DateTimeOffset timestamp, TimeSpan bucketSize)
    {
        var exchangeTime = TimeZoneInfo.ConvertTime(timestamp, ExchangeTimeZone);

        if (bucketSize.TotalDays >= 1)
        {
            var midnight = exchangeTime.Date;
            return new DateTimeOffset(midnight, ExchangeTimeZone.GetUtcOffset(midnight)).ToUniversalTime();
        }

        var timeOfDay = exchangeTime.TimeOfDay;
        var regularOpen = new TimeSpan(9, 30, 0);

        if (timeOfDay >= regularOpen)
        {
            var offset = timeOfDay - regularOpen;
            var bucketTicks = offset.Ticks - (offset.Ticks % bucketSize.Ticks);
            var bucketTime = regularOpen.Add(TimeSpan.FromTicks(bucketTicks));
            var bucketDateTime = exchangeTime.Date.Add(bucketTime);
            return new DateTimeOffset(bucketDateTime, ExchangeTimeZone.GetUtcOffset(bucketDateTime)).ToUniversalTime();
        }

        var premarketOpen = new TimeSpan(4, 0, 0);
        if (timeOfDay >= premarketOpen)
        {
            var offset = timeOfDay - premarketOpen;
            var bucketTicks = offset.Ticks - (offset.Ticks % bucketSize.Ticks);
            var bucketTime = premarketOpen.Add(TimeSpan.FromTicks(bucketTicks));
            var bucketDateTime = exchangeTime.Date.Add(bucketTime);
            return new DateTimeOffset(bucketDateTime, ExchangeTimeZone.GetUtcOffset(bucketDateTime)).ToUniversalTime();
        }

        var midnightTicks = timeOfDay.Ticks - (timeOfDay.Ticks % bucketSize.Ticks);
        var midnightDateTime = exchangeTime.Date.Add(TimeSpan.FromTicks(midnightTicks));
        return new DateTimeOffset(midnightDateTime, ExchangeTimeZone.GetUtcOffset(midnightDateTime)).ToUniversalTime();
    }
}

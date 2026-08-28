using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Engine.Market;

public sealed class BarResampler
{
    private readonly MarketEvidenceProfile marketEvidenceProfile;
    private readonly TimeZoneInfo exchangeTimeZone;

    public BarResampler(MarketEvidenceProfile? marketEvidenceProfile = null)
    {
        this.marketEvidenceProfile = marketEvidenceProfile ?? new MarketEvidenceProfile(
            "resampler_standard_calendar_v1",
            MarketEvidenceProfile.DefaultLookbackSessions,
            MarketEvidenceProfile.DefaultMinimumValidSamples,
            "America/New_York",
            allowStandardWeekdayFallback: true);
        exchangeTimeZone = ResolveExchangeTimeZone(this.marketEvidenceProfile.ExchangeTimeZone);
    }

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
                var dataFeed = RequireSingleProvenance(
                    ordered.Select(bar => bar.DataFeed),
                    "data feed");
                var adjustmentPolicy = RequireSingleProvenance(
                    ordered.Select(bar => bar.AdjustmentPolicy),
                    "adjustment policy");
                return new OhlcvBar(
                    first.Ticker,
                    group.Key,
                    targetTimeframe,
                    first.Open,
                    ordered.Max(x => x.High),
                    ordered.Min(x => x.Low),
                    last.Close,
                    ordered.Sum(x => x.Volume),
                    dataFeed,
                    adjustmentPolicy,
                    ordered.Max(x => x.KnownAtUtc),
                    ResolveVerifiedCoverage(ordered));
            })
            .OrderBy(x => x.Timestamp)
            .ToArray();
    }

    private static DateTimeOffset? ResolveVerifiedCoverage(IReadOnlyCollection<OhlcvBar> bars)
    {
        if (bars.Any(bar => bar.CoverageVerifiedThroughUtc is null))
        {
            return null;
        }

        return bars.Min(bar => bar.CoverageVerifiedThroughUtc)!.Value.ToUniversalTime();
    }

    private static string RequireSingleProvenance(IEnumerable<string> values, string field)
    {
        var normalized = values
            .Select(value => String.IsNullOrWhiteSpace(value) ? "unspecified" : value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length != 1)
        {
            throw new InvalidOperationException($"Cannot resample bars with mixed {field} provenance.");
        }

        return normalized[0];
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

    private static TimeZoneInfo ResolveExchangeTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException) when (timeZoneId.Equals("America/New_York", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    private BucketWindow GetBucketWindow(DateTimeOffset timestamp, TimeSpan bucketSize)
    {
        var exchangeTime = TimeZoneInfo.ConvertTime(timestamp, exchangeTimeZone);

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
        var exchangeDate = DateOnly.FromDateTime(exchangeTime.DateTime);
        var tradeDate = timeOfDay >= new TimeSpan(20, 0, 0)
            ? exchangeDate.AddDays(1)
            : exchangeDate;
        if (!marketEvidenceProfile.TryResolveSchedule(tradeDate, out var schedule))
        {
            throw new InvalidOperationException(
                $"Authoritative exchange calendar is unavailable for {tradeDate:yyyy-MM-dd}.");
        }

        if (!schedule.IsTradingDay)
        {
            throw new InvalidOperationException(
                $"Cannot resample an intraday bar into closed exchange session {tradeDate:yyyy-MM-dd}.");
        }

        var regularOpen = schedule.RegularOpen.ToTimeSpan();
        var regularClose = schedule.RegularClose.ToTimeSpan();
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
        else if (timeOfDay < regularOpen)
        {
            segmentStart = exchangeTime.Date.AddHours(4);
            segmentEnd = exchangeTime.Date.Add(regularOpen);
            segment = "premarket";
        }
        else if (timeOfDay < regularClose)
        {
            segmentStart = exchangeTime.Date.Add(regularOpen);
            segmentEnd = exchangeTime.Date.Add(regularClose);
            segment = "regular";
        }
        else
        {
            segmentStart = exchangeTime.Date.Add(regularClose);
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

    private DateTimeOffset ToUtc(DateTime exchangeDateTime) =>
        new DateTimeOffset(exchangeDateTime, exchangeTimeZone.GetUtcOffset(exchangeDateTime)).ToUniversalTime();

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

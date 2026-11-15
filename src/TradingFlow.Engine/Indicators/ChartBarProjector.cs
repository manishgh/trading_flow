using TradingFlow.Domain.Market;
using TradingFlow.Engine.Market;

namespace TradingFlow.Engine.Indicators;

public sealed record ChartTimeRange(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
public sealed record ChartCoveredMinutes(IReadOnlyList<ChartSourceBar> Bars, DateTimeOffset AvailableAtUtc);

/// <summary>Applies the chart clock, exchange windows and explicit coverage proofs to completed bars.</summary>
public sealed class ChartBarProjector(IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules)
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private readonly BarResampler resampler = new(MarketEvidenceProfile.ProductionDefault.WithSessionSchedules(schedules));

    // Map a UTC source timestamp to its New York calendar date without guessing a session.
    public static DateOnly NewYorkDate(DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, NewYork).DateTime);

    // Set a verified session boundary using the exchange offset on that date.
    public static DateTimeOffset At(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, NewYork.GetUtcOffset(local)).ToUniversalTime();
    }

    // Resolve only sessions attested by the source calendar.
    public ChartTimeRange? Regular(DateOnly date) =>
        schedules.TryGetValue(date, out var schedule) && schedule.IsTradingDay
            ? new ChartTimeRange(At(date, schedule.RegularOpen), At(date, schedule.RegularClose))
            : null;

    // An as-of classification cannot use an assumed open on an uncovered date.
    public string? Segment(DateTimeOffset timestamp)
    {
        var date = NewYorkDate(timestamp);
        if (Regular(date) is not { } regular)
        {
            return null;
        }
        if (timestamp >= regular.StartUtc && timestamp < regular.EndUtc)
        {
            return "regular";
        }
        if (timestamp >= At(date, new TimeOnly(4, 0)) && timestamp < regular.StartUtc)
        {
            return "premarket";
        }
        return timestamp >= regular.EndUtc && timestamp < At(date, new TimeOnly(20, 0))
            ? "postmarket"
            : null;
    }

    // Use real provider daily bars, with their exchange close as the eligibility boundary.
    public IReadOnlyList<ChartCompletedBar> Daily(
        IEnumerable<ChartSourceBar> sources, IReadOnlySet<DateTimeOffset> basisBreaks,
        DateTimeOffset cutoffUtc, DateTimeOffset asOfUtc)
    {
        // A conflicting date cannot impersonate a single authoritative session.
        var byDate = sources.GroupBy(source => NewYorkDate(source.Bar.Timestamp))
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        var result = new List<ChartCompletedBar>();
        var continuity = 0;
        foreach (var (date, schedule) in schedules.OrderBy(item => item.Key))
        {
            if (!schedule.IsTradingDay)
            {
                continue;
            }
            var end = At(date, schedule.RegularClose);
            if (end > cutoffUtc)
            {
                break;
            }
            if (!byDate.TryGetValue(date, out var source) || source.AvailableAtUtc > asOfUtc)
            {
                continuity++;
                continue;
            }
            if (basisBreaks.Contains(source.Bar.Timestamp))
            {
                continuity++;
            }
            result.Add(new ChartCompletedBar(source.Bar, end, date, "daily", continuity,
                source.SourceKind, source.RetrievedAtUtc, Later(source.AvailableAtUtc, end)));
        }
        return result;
    }

    // Within each segment only completed, covered buckets can become candles.
    // Continuity advances for a gap or basis break and survives overnight only across regular sessions.
    public IReadOnlyList<ChartCompletedBar> Intraday(
        IReadOnlyDictionary<DateTimeOffset, ChartSourceBar> source,
        IReadOnlyDictionary<DateTimeOffset, DateTimeOffset> quiet,
        IReadOnlySet<DateTimeOffset> basisBreaks,
        string interval, bool includeExtended, DateTimeOffset cutoffUtc, DateTimeOffset asOfUtc)
    {
        if (source.Count == 0)
        {
            return [];
        }

        var first = NewYorkDate(source.Keys.Min());
        var days = schedules.Keys.Where(date => date >= first && date <= NewYorkDate(cutoffUtc)).Order();
        var result = new List<ChartCompletedBar>();
        var continuity = 0;
        foreach (var date in days)
        {
            foreach (var segment in Segments(date, includeExtended))
            {
                AppendSegment(source, quiet, basisBreaks, interval, segment, cutoffUtc, asOfUtc, result, ref continuity);
            }
        }

        return result;
    }

    // Check the intended viewport, including leading and trailing mature buckets with no emitted candle.
    public bool IsIntradayCovered(
        IReadOnlyList<DateOnly> sessions, IReadOnlyDictionary<DateTimeOffset, ChartSourceBar> source,
        IReadOnlyDictionary<DateTimeOffset, DateTimeOffset> quiet, IReadOnlySet<DateTimeOffset> basisBreaks,
        string interval, bool includeExtended, DateTimeOffset cutoff, DateTimeOffset asOf)
    {
        foreach (var segment in sessions.SelectMany(date => Segments(date, includeExtended)).Where(range => range.StartUtc < cutoff))
        {
            var end = segment.EndUtc;
            if (cutoff < end)
            {
                var window = resampler.GetBucketWindow(cutoff.AddTicks(-1), interval);
                end = window.EndUtc <= cutoff ? window.EndUtc : window.StartUtc;
            }
            if (end > segment.StartUtc &&
                ReadCoveredMinutes(new ChartTimeRange(segment.StartUtc, end), source, quiet, basisBreaks, asOf) is null)
            {
                return false;
            }
        }
        return true;
    }

    // Use one session segmentation for both projection and its coverage check.
    private IEnumerable<ChartTimeRange> Segments(DateOnly date, bool extended)
    {
        if (Regular(date) is not { } regular)
        {
            yield break;
        }
        if (extended)
        {
            yield return new ChartTimeRange(At(date, new TimeOnly(4, 0)), regular.StartUtc);
        }
        yield return regular;
        if (extended)
        {
            yield return new ChartTimeRange(regular.EndUtc, At(date, new TimeOnly(20, 0)));
        }
    }

    // Take no OHLC from a quiet minute. A known quiet interval still preserves calendar continuity.
    public static ChartCoveredMinutes? ReadCoveredMinutes(
        ChartTimeRange range, IReadOnlyDictionary<DateTimeOffset, ChartSourceBar> source,
        IReadOnlyDictionary<DateTimeOffset, DateTimeOffset> quiet, IReadOnlySet<DateTimeOffset> basisBreaks,
        DateTimeOffset asOfUtc, bool allowBreakAtStart = false)
    {
        if (range.EndUtc <= range.StartUtc)
        {
            return null;
        }

        var bars = new List<ChartSourceBar>();
        var available = range.EndUtc;
        for (var minute = range.StartUtc; minute < range.EndUtc; minute = minute.AddMinutes(1))
        {
            if (basisBreaks.Contains(minute) && (!allowBreakAtStart || minute != range.StartUtc))
            {
                return null;
            }

            if (source.TryGetValue(minute, out var bar) && bar.AvailableAtUtc <= asOfUtc)
            {
                bars.Add(bar);
                available = Later(available, bar.AvailableAtUtc);
            }
            else if (quiet.TryGetValue(minute, out var proof) && proof <= asOfUtc)
            {
                available = Later(available, proof);
            }
            else
            {
                return null;
            }
        }
        return new ChartCoveredMinutes(bars, available);
    }

    // Reuse the authoritative resampler windows and OHLCV rules for each eligible bucket.
    private void AppendSegment(
        IReadOnlyDictionary<DateTimeOffset, ChartSourceBar> source,
        IReadOnlyDictionary<DateTimeOffset, DateTimeOffset> quiet,
        IReadOnlySet<DateTimeOffset> breaks,
        string interval, ChartTimeRange range, DateTimeOffset cutoffUtc, DateTimeOffset asOfUtc,
        List<ChartCompletedBar> result, ref int continuity)
    {
        for (var start = range.StartUtc; start < range.EndUtc; )
        {
            var window = resampler.GetBucketWindow(start, interval);
            if (window.EndUtc > cutoffUtc)
            {
                break;
            }
            if (breaks.Contains(window.StartUtc))
            {
                continuity++;
            }
            var covered = ReadCoveredMinutes(new ChartTimeRange(window.StartUtc, window.EndUtc), source, quiet,
                breaks, asOfUtc, allowBreakAtStart: true);
            start = window.EndUtc;
            if (covered is null)
            {
                continuity++;
                continue;
            }
            if (covered.Bars.Count == 0)
            {
                continue;
            }

            var inputs = covered.Bars.Select(item => item.Bar).ToArray();
            var bar = inputs.Length == 1 && interval == "1m" ? inputs[0] : resampler.Resample(inputs, interval).Single();
            var reconstructed = covered.Bars.Any(item => item.Bar.KnownAtUtc is null);
            if (reconstructed)
            {
                bar = bar with { KnownAtUtc = null };
            }
            var sourceKind = interval == "1m" ? covered.Bars[0].SourceKind : "derived_1m";
            result.Add(new ChartCompletedBar(bar, window.EndUtc, NewYorkDate(window.StartUtc), window.SessionSegment,
                continuity, sourceKind, covered.Bars.Max(item => item.RetrievedAtUtc), covered.AvailableAtUtc));
        }
    }

    // Keep an overlay or candle unavailable until its last input or coverage proof is known.
    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;
}

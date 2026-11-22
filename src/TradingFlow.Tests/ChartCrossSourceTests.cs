using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Tests;

public sealed class ChartCrossSourceTests
{
    // A calendar repair invalidates cached numerical work even when raw histories and extended display bars match.
    [Fact]
    public void RevisedEarlyCloseChangesHistoryRevisionWithoutChangingBarRows()
    {
        var day = new DateOnly(2026, 11, 27);
        var initial = new Dictionary<DateOnly, MarketSessionSchedule> { [day] = MarketSessionSchedule.RegularDay(day) };
        var revised = new Dictionary<DateOnly, MarketSessionSchedule> { [day] = new(day, true, new TimeOnly(9, 30), new TimeOnly(13, 0)) };
        var start = ChartBarProjector.At(day, new TimeOnly(9, 30));
        var source = new Dictionary<DateTimeOffset, ChartSourceBar> { [start] = Source(start, "1m", 100, 10, start.AddMinutes(1)) };
        var history = Batch(new ChartBarProjector(initial), [day], source) with
        { CalendarRevision = ChartInputRevision.Calendar(initial) };
        var originalRevision = history.Revision;
        var changed = history with
        { Projector = new ChartBarProjector(revised), CalendarRevision = ChartInputRevision.Calendar(revised) };
        Assert.Same(history.Bars, changed.Bars);
        Assert.NotEqual(originalRevision, changed.Revision);
        Assert.Equal(18, changed.Projector.Regular(day)!.EndUtc.Hour);
    }

    // A recorded late receipt is drawn only on candles that open at or after it. The same daily candle without a recorded
    // receipt was downloaded later: it is drawn from its scheduled completion and marked as reconstructed.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DailyStudyRespectsRecordedReceiptsAndMarksDownloadedHistory(bool recorded)
    {
        var (days, projector) = Sessions(new TimeOnly(16, 0));
        var open = projector.Regular(days[20])!.StartUtc;
        var received = open.AddMinutes(1);
        var dailyRows = days[..20].Select((day, index) =>
        {
            var timestamp = ChartBarProjector.At(day, TimeOnly.MinValue);
            var end = projector.Regular(day)!.EndUtc;
            var proof = index == 19 ? received : end;
            var bar = Source(timestamp, "1d", index + 1, 100, proof);
            return index == 19 && !recorded ? bar with { Bar = bar.Bar with { KnownAtUtc = null }, RetrievedAtUtc = proof } : bar;
        }).ToDictionary(row => row.Bar.Timestamp);
        var history = Batch(projector, days[6..], []);
        var daily = Batch(projector, days[..20], dailyRows);
        var visible = new[] { Display(days[20], open), Display(days[20], received) };
        var key = ChartSeriesService.Normalize(new ChartSeriesKey("TEST", "1m", "regular", "week", "sma20:1d"));
        var studies = ChartIndicatorProfile.Read(key);
        var merged = new MergedChartSource(history.Bars, history.Quiet, history.BasisBreaks);
        var result = ChartStudyProjection.Calculate(key, studies, history, daily, merged, visible, [],
            received.AddMinutes(1), received.AddMinutes(2), received.AddMinutes(2), "fixture-revision", default);
        var series = Assert.Single(result);
        if (!recorded)
        {
            Assert.Equal("ready", series.Status);
            Assert.Equal(new double?[] { 10.5, 10.5 }, Assert.Single(series.Samples));
            Assert.True(series.Reconstructed);
            return;
        }
        Assert.Equal("partial", series.Status);
        Assert.Equal(new double?[] { null, 10.5 }, Assert.Single(series.Samples));
        Assert.Equal([1], series.SegmentStarts);
        Assert.Equal(received, series.AvailableAtUtc);
        Assert.False(series.Reconstructed);

        var archived = ChartStudyProjection.Calculate(key, studies, history, daily, merged, visible[..1], [],
            received, received, received.AddMinutes(2), "fixture-revision", default);
        Assert.Empty(Assert.Single(archived).Samples);
        Assert.Contains("recorded receipt", archived[0].Reason);
    }

    // A longer interval's value belongs to the candles from its completion until the next one completes; the first candles of a
    // session keep the previous session's last value, and a missing longer candle leaves a gap instead of an old value.
    [Fact]
    public void LongerIntervalFollowsItsLatestCompletedCandle()
    {
        var (days, projector) = Sessions(new TimeOnly(9, 50));
        var dates = days[^2..];
        var rows = new Dictionary<DateTimeOffset, ChartSourceBar>();
        foreach (var (date, day) in dates.Select((date, index) => (date, index)))
        {
            var open = projector.Regular(date)!.StartUtc;
            for (var minute = 0; minute < 20; minute++)
            {
                if (day == 1 && minute == 7) continue;
                var start = open.AddMinutes(minute);
                rows[start] = Source(start, "1m", 100 + 10 * day + minute / 5, 10, start.AddMinutes(1));
            }
        }
        var history = Batch(projector, dates, rows);
        var key = ChartSeriesService.Normalize(new ChartSeriesKey("TEST", "1m", "regular", "intraday", "sma2:5m"));
        var merged = new MergedChartSource(history.Bars, history.Quiet, history.BasisBreaks);
        var today = projector.Regular(dates[1])!;
        var visible = Enumerable.Range(0, 20).Where(minute => minute != 7)
            .Select(minute => Display(dates[1], today.StartUtc.AddMinutes(minute))).ToArray();
        var series = Assert.Single(ChartStudyProjection.Calculate(key, ChartIndicatorProfile.Read(key), history, null, merged, visible, [],
            today.EndUtc, today.EndUtc, today.EndUtc, "longer", default));
        var values = visible.Zip(Assert.Single(series.Samples)).ToDictionary(pair => (int)(pair.First.Bar.Timestamp - today.StartUtc).TotalMinutes, pair => pair.Second);
        // Yesterday's last two five-minute closes are 102 and 103; the first candle of today completes at 09:35 (minute 4).
        for (var minute = 0; minute < 4; minute++) Assert.Equal(102.5, values[minute]);
        Assert.Equal(106.5, values[4]);
        Assert.Equal(106.5, values[8]);
        // 09:35-09:40 misses a minute, so it has no candle: the value ends when that candle would have completed.
        Assert.Null(values[9]);
        Assert.Null(values[14]);
        Assert.Equal(112.5, values[19]);

        // Without yesterday's closing five-minute candle there is no value to carry into today.
        var lost = projector.Regular(dates[0])!.EndUtc.AddMinutes(-2);
        var cut = Batch(projector, dates, rows.Where(row => row.Key != lost).ToDictionary());
        var gap = Assert.Single(ChartStudyProjection.Calculate(key, ChartIndicatorProfile.Read(key), cut, null,
            new MergedChartSource(cut.Bars, cut.Quiet, cut.BasisBreaks), visible, [], today.EndUtc, today.EndUtc, today.EndUtc, "gap", default));
        Assert.Null(gap.Samples[0][0]);
    }

    // The cache's ordinary 15 sessions cannot provide RVOL; compatible already warm evidence can complete the cohort.
    [Fact]
    public void RelativeVolumeNeedsExactlyTwentyCompatibleSameTimeSessions()
    {
        var (days, projector) = Sessions(new TimeOnly(9, 31));
        var prices = days.Select((day, index) =>
        {
            var open = projector.Regular(day)!.StartUtc;
            return Source(open, "1m", 100, index == 20 ? 31.5m : index + 1, open.AddMinutes(1));
        }).ToArray();
        var current = Batch(projector, days[6..], prices[6..].ToDictionary(row => row.Bar.Timestamp));
        var old = Batch(projector, days[..15], prices[..15].ToDictionary(row => row.Bar.Timestamp));
        var key = ChartSeriesService.Normalize(new ChartSeriesKey("TEST", "1m", "regular", "intraday", "rvol"));
        var studies = ChartIndicatorProfile.Read(key);
        var merged = new MergedChartSource(current.Bars, current.Quiet, current.BasisBreaks);
        var start = prices[^1].Bar.Timestamp;
        var end = start.AddMinutes(1);
        var visible = new[] { Display(days[20], start) };
        var insufficient = Assert.Single(ChartStudyProjection.Calculate(key, studies, current, null, merged, visible,
            [], end, end, end, "cold", default));
        Assert.Equal("unavailable", insufficient.Status);
        Assert.Equal(14, insufficient.BaselineSampleCount);

        var ready = Assert.Single(ChartStudyProjection.Calculate(key, studies, current, null, merged, visible,
            [old], end, end, end, "warm", default));
        Assert.Equal("ready", ready.Status);
        Assert.Equal(20, ready.BaselineSampleCount);
        Assert.Equal(3d, Assert.Single(Assert.Single(ready.Samples)));

        var revised = prices[6] with { Bar = prices[6].Bar with { Volume = 200 } };
        var incompatible = old.Bars.ToDictionary();
        incompatible[revised.Bar.Timestamp] = revised;
        var rejected = Assert.Single(ChartStudyProjection.Calculate(key, studies, current, null, merged, visible,
            [Batch(projector, days[..15], incompatible)], end, end, end, "incompatible", default));
        Assert.Equal("unavailable", rejected.Status);
        Assert.Equal(14, rejected.BaselineSampleCount);
    }

    // Supply 21 scheduled New York sessions and no holiday guesses.
    private static (DateOnly[] Dates, ChartBarProjector Projector) Sessions(TimeOnly close)
    {
        var dates = Enumerable.Range(0, 40).Select(index => new DateOnly(2026, 10, 5).AddDays(index))
            .Where(date => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).Take(21).ToArray();
        return (dates, new ChartBarProjector(dates.ToDictionary(date => date,
            date => new MarketSessionSchedule(date, true, new TimeOnly(9, 30), close))));
    }

    // Keep acquired proof explicit for the already warm batches.
    private static ChartHistoryBatch Batch(ChartBarProjector projector, DateOnly[] dates,
        Dictionary<DateTimeOffset, ChartSourceBar> bars) =>
        new(projector, dates, bars, new Dictionary<DateTimeOffset, DateTimeOffset>(), new HashSet<DateTimeOffset>(),
            dates.ToHashSet(), new Dictionary<DateOnly, DateTimeOffset>(), new Dictionary<DateOnly, ChartMinuteAcquisition>(),
            null, null, null);

    // Define actual adjusted SIP input and its independent availability.
    private static ChartSourceBar Source(DateTimeOffset start, string interval, decimal close, decimal volume, DateTimeOffset received) =>
        new(new OhlcvBar("TEST", start, interval, close, close, close, close, volume, "sip", "all", received),
            "fixture", null, received);

    // Create a visible minute with a normal completion receipt.
    private static ChartCompletedBar Display(DateOnly date, DateTimeOffset start)
    {
        var end = start.AddMinutes(1);
        return new ChartCompletedBar(Source(start, "1m", 100, 10, end).Bar, end, date, "regular", 0, "fixture", null, end);
    }
}

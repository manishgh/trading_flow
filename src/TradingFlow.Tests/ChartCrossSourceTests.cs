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

    // A late daily reconstruction is joined only when an intraday candle opens at or after the actual receipt.
    [Fact]
    public void DailyStudyCannotAppearBeforeItsCoveredSourceReceipt()
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
            return index == 19 ? bar with { Bar = bar.Bar with { KnownAtUtc = null }, RetrievedAtUtc = proof } : bar;
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
        Assert.Equal("partial", series.Status);
        Assert.Equal(new double?[] { null, 10.5 }, Assert.Single(series.Samples));
        Assert.Equal([1], series.SegmentStarts);
        Assert.Equal(received, series.AvailableAtUtc);
        Assert.True(series.Reconstructed);

        var archived = ChartStudyProjection.Calculate(key, studies, history, daily, merged, visible[..1], [],
            received, received, received.AddMinutes(2), "fixture-revision", default);
        Assert.Empty(Assert.Single(archived).Samples);
        Assert.Contains("actually observed", archived[0].Reason);
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

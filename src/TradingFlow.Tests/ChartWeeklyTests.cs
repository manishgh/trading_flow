using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Moq;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Earnings;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Storage;
using TradingFlow.Web;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Tests;

public sealed partial class ChartProjectionTests
{
    private static readonly ChartSeriesKey WeeklyKey = new("TEST", "1w", "regular");

    // A schedule through Sunday proves the shortened week's close across the autumn clock change.
    [Fact]
    public void WeeklyCombinesHolidayAndEarlyCloseInputsWithIndependentReceiptClocks()
    {
        var monday = new DateOnly(2026, 11, 2);
        var first = monday.AddDays(1);
        var last = monday.AddDays(4);
        var early = new TimeOnly(13, 0);
        var kernel = Enumerable.Range(-7, 14).Select(monday.AddDays).ToDictionary(date => date,
            date => date == monday || date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? MarketSessionSchedule.Closed(date)
                : date == last ? new MarketSessionSchedule(last, true, new TimeOnly(9, 30), early)
                    : MarketSessionSchedule.RegularDay(date));
        var projector = new ChartBarProjector(kernel);
        var end = new DateTimeOffset(2026, 11, 6, 18, 0, 0, TimeSpan.Zero);
        var received = end.AddMinutes(18);
        var source = Enumerable.Range(0, 4).Select(i =>
            BarAt(ChartBarProjector.At(first.AddDays(i), TimeOnly.MinValue), "1d", 100m + i)).ToArray();
        source[1] = source[1] with { Bar = source[1].Bar with { Low = 97m } };
        source[3] = source[3] with
        {
            AvailableAtUtc = received,
            RetrievedAtUtc = received,
            Bar = source[3].Bar with { KnownAtUtc = null }
        };
        var breaks = new HashSet<DateTimeOffset>();
        var beforeClose = projector.Weekly("TEST", source, breaks, first, end.AddTicks(-1), received);
        Assert.True(beforeClose.Pending);
        Assert.Empty(beforeClose.Evaluated);
        var beforeReceipt = Assert.Single(projector.Weekly("TEST", source, breaks, first, end, received.AddTicks(-1)).Evaluated);
        Assert.Null(beforeReceipt.Candle);
        Assert.Contains("2026-11-06", beforeReceipt.Gap);

        var week = Assert.Single(projector.Weekly("TEST", source, breaks, first, end, received).Evaluated);
        var candle = Assert.IsType<ChartCompletedBar>(week.Candle);
        Assert.Equal((first, last, end), (candle.SessionDate, week.LastSession, candle.EndUtc));
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 5, 0, 0, TimeSpan.Zero), candle.Bar.Timestamp);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 4, 0, 0, TimeSpan.Zero),
            ChartBarProjector.At(monday.AddDays(-3), TimeOnly.MinValue));
        Assert.Equal((100m, 104m, 97m, 103m, 400m), (candle.Bar.Open, candle.Bar.High, candle.Bar.Low, candle.Bar.Close, candle.Bar.Volume));
        Assert.Equal(("1w", "sip", "all"), (candle.Bar.Timeframe, candle.Bar.DataFeed, candle.Bar.AdjustmentPolicy));
        Assert.Equal(received, candle.AvailableAtUtc);
        Assert.Equal(received, candle.RetrievedAtUtc);
        Assert.Null(candle.Bar.KnownAtUtc);
        Assert.Equal(source, week.Constituents);

        // A separate replay sees a price correction without changing the original session or end label.
        source[2] = source[2] with
        {
            AvailableAtUtc = received.AddMinutes(1),
            Bar = source[2].Bar with { High = 109m, KnownAtUtc = received.AddMinutes(1) }
        };
        var revised = Assert.Single(projector.Weekly("TEST", source, breaks, first, end, received.AddMinutes(1)).Evaluated).Candle!;
        Assert.Equal(candle.Bar.Timestamp, revised.Bar.Timestamp);
        Assert.Equal(109m, revised.Bar.High);
        Assert.Equal(received.AddMinutes(1), revised.AvailableAtUtc);
    }

    // Missing and conflicting source sessions, a split seam and an uncovered weekend cannot become partial candles.
    [Fact]
    public void WeeklyOmitsConflictsAndUnverifiedWeeksButAllowsAnOpeningBasisBoundary()
    {
        var monday = new DateOnly(2026, 10, 26);
        var full = Enumerable.Range(0, 14).Select(monday.AddDays).ToDictionary(date => date,
            date => date < monday.AddDays(5) ? MarketSessionSchedule.RegularDay(date) : MarketSessionSchedule.Closed(date));
        var projector = new ChartBarProjector(full);
        var cutoff = ChartBarProjector.At(monday.AddDays(13), TimeOnly.MinValue);
        var dates = Enumerable.Range(0, 5).Select(monday.AddDays);
        var source = dates.Select(date => BarAt(ChartBarProjector.At(date, TimeOnly.MinValue), "1d", 100m)).ToArray();
        var missing = Assert.Single(projector.Weekly("TEST", source.Where((_, index) => index != 2),
            new HashSet<DateTimeOffset>(), monday, cutoff, cutoff).Evaluated);
        Assert.Contains("Unavailable SIP daily candle for 2026-10-28", missing.Gap);
        var conflict = Assert.Single(projector.Weekly("TEST", source.Append(source[2]),
            new HashSet<DateTimeOffset>(), monday, cutoff, cutoff).Evaluated);
        Assert.Contains("Conflicting SIP daily candle for 2026-10-28", conflict.Gap);
        var split = Assert.Single(projector.Weekly("TEST", source,
            new HashSet<DateTimeOffset> { source[2].Bar.Timestamp }, monday, cutoff, cutoff).Evaluated);
        Assert.Null(split.Candle);
        Assert.Contains("Price basis", split.Gap);
        Assert.NotNull(Assert.Single(projector.Weekly("TEST", source,
            new HashSet<DateTimeOffset> { source[0].Bar.Timestamp }, monday, cutoff, cutoff).Evaluated).Candle);

        full.Remove(monday.AddDays(6));
        var unverified = Assert.Single(new ChartBarProjector(full).Weekly("TEST", source,
            new HashSet<DateTimeOffset>(), monday, cutoff, cutoff).Evaluated);
        Assert.Null(unverified.Candle);
        Assert.Contains("Exchange schedule is unverified", unverified.Gap);
    }

    // The native daily acquisition edge cuts a week that belongs wholly to the next exclusive-before page.
    [Fact]
    public async Task WeeklyPagerReturnsTheSkippedAcquisitionEdgeExactlyOnce()
    {
        var date = new DateOnly(2026, 11, 17);
        var clock = new MutableChartClock(ChartBarProjector.At(date, new TimeOnly(19, 0)));
        var provider = new ChartHistoryFixture();
        var series = MakeLevelTestSeries(provider, clock);
        var key = ChartSeriesService.Normalize(WeeklyKey);
        var latest = await series.ReadAsync(key, null, default);
        var acquisitionEdge = ChartBarProjector.NewYorkDate(Assert.Single(provider.Requests).Start);
        var edgeMonday = ChartBarProjector.WeekStart(acquisitionEdge);
        Assert.True(edgeMonday < acquisitionEdge);
        Assert.Equal(edgeMonday.AddDays(7), latest.Snapshot.Bars[0].SessionDate);
        Assert.Equal(latest.Snapshot.Bars[0].StartUtc, latest.NextBeforeUtc);
        Assert.True(latest.Snapshot.Evidence.Weekly!.Pending);
        Assert.InRange(latest.Snapshot.Bars.Count, 98, 100);
        Assert.Empty(latest.Snapshot.Indicators!);
        Assert.Empty(latest.Snapshot.Zones);

        var cursor = new ChartHistoryCursor(new EphemeralDataProtectionProvider(), clock);
        Assert.Equal("weekly", key.Context);
        Assert.Throws<ArgumentException>(() => cursor.Create("reader", key, latest.NextBeforeUtc!.Value.AddHours(1)));
        Assert.Throws<ArgumentException>(() => ChartSeriesService.Normalize(WeeklyKey with { Indicators = "ema20" }));
        var token = cursor.Create("reader", key, latest.NextBeforeUtc)!;
        Assert.Throws<ArgumentException>(() => cursor.Read(token, "reader", key with { Interval = "1d", Context = "swing" }));
        var older = await series.ReadAsync(key, cursor.Read(token, "reader", key), default);

        Assert.False(older.Snapshot.Evidence.Weekly!.Pending);
        Assert.Equal(edgeMonday, older.Snapshot.Bars[^1].SessionDate);
        Assert.Equal(edgeMonday.AddDays(4), older.Snapshot.Bars[^1].Weekly!.LastSessionDate);
        var all = latest.Snapshot.Bars.Concat(older.Snapshot.Bars).ToArray();
        Assert.Single(all, candle => candle.SessionDate == edgeMonday);
        Assert.Equal(all.Length, all.Select(candle => candle.SessionDate).Distinct().Count());
        Assert.All(new[] { latest, older }, page =>
        {
            Assert.InRange(page.Snapshot.Bars.Sum(candle => candle.Weekly!.Constituents.Count), 1, 500);
            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(page.Snapshot, StockChartHub.Json).Length, 1,
                StockChartHub.MaximumSnapshotBytes);
        });
        Assert.Equal(2, provider.Requests.Count);
    }

    // Page ownership advances through missing data and keeps the next earlier window accessible.
    [Fact]
    public async Task WeeklyPagerRetainsCoverageAndCursorWhenEveryCandleIsMissing()
    {
        var date = new DateOnly(2026, 11, 17);
        var clock = new MutableChartClock(ChartBarProjector.At(date, new TimeOnly(19, 0)));
        var provider = new ChartHistoryFixture { HideAllDaily = true };
        var series = MakeLevelTestSeries(provider, clock);
        var first = await series.ReadAsync(WeeklyKey, null, default);
        Assert.Empty(first.Snapshot.Bars);
        Assert.NotEmpty(first.Snapshot.Evidence.Weekly!.Gaps);
        Assert.Equal("partial", first.Snapshot.Evidence.Coverage);
        Assert.NotNull(first.NextBeforeUtc);
        var older = await series.ReadAsync(WeeklyKey, first.NextBeforeUtc, default);
        Assert.Empty(older.Snapshot.Bars);
        Assert.True(older.NextBeforeUtc < first.NextBeforeUtc);
        Assert.True(older.Snapshot.Evidence.Weekly!.Gaps[^1].WeekOf < first.Snapshot.Evidence.Weekly.Gaps[0].WeekOf);
    }

    // Incomplete schedule proof omits only its week while the daily chart keeps its strict calendar gate.
    [Fact]
    public async Task WeeklyHistoryKeepsVerifiedWeeksWhenOneCalendarDateIsMissing()
    {
        var date = new DateOnly(2026, 11, 17);
        var missing = new DateOnly(2026, 11, 5);
        var clock = new MutableChartClock(ChartBarProjector.At(date, new TimeOnly(19, 0)));
        var provider = new ChartHistoryFixture { UnverifiedDate = missing };
        var series = MakeLevelTestSeries(provider, clock);
        var weekly = (await series.ReadAsync(WeeklyKey, null, default)).Snapshot;
        var gap = Assert.Single(weekly.Evidence.Weekly!.Gaps);
        Assert.Equal(ChartBarProjector.WeekStart(missing), gap.WeekOf);
        Assert.Contains("Exchange schedule is unverified", gap.Reason);
        Assert.DoesNotContain(weekly.Bars, candle => candle.SessionDate == gap.WeekOf);
        Assert.Equal(new DateOnly(2026, 11, 9), weekly.Bars[^1].SessionDate);

        var daily = (await series.ReadAsync(WeeklyKey with { Interval = "1d" }, null, default)).Snapshot;
        Assert.Empty(daily.Bars);
        Assert.Single(provider.Requests);
    }

    // A matching price overlap establishes the basis of a volume correction. A later price mismatch does not.
    [Fact]
    public async Task WeeklyDailyRevisionUpdatesVolumeAndOmitsAnUnprovedPriceJoin()
    {
        var date = new DateOnly(2026, 11, 16);
        var clock = new MutableChartClock(ChartBarProjector.At(date, new TimeOnly(12, 0)));
        var provider = new ChartHistoryFixture();
        var series = MakeLevelTestSeries(provider, clock);
        var dailyKey = WeeklyKey with { Interval = "1d" };
        var original = (await series.ReadAsync(WeeklyKey, null, default)).Snapshot;
        var originalDaily = (await series.ReadAsync(dailyKey, null, default)).Snapshot;
        provider.RevisedVolume = date.AddDays(-3);
        clock.Now = clock.Now.AddMinutes(31);
        var now = clock.Now;
        var revised = (await series.ReadAsync(WeeklyKey, null, default)).Snapshot;
        var revisedDaily = (await series.ReadAsync(dailyKey, null, default)).Snapshot;
        Assert.NotEqual(original.Evidence.Weekly!.SourceRevision, revised.Evidence.Weekly!.SourceRevision);
        Assert.Equal(original.Bars[^1].StartUtc, revised.Bars[^1].StartUtc);
        Assert.Equal(original.Bars[^1].EndUtc, revised.Bars[^1].EndUtc);
        Assert.Equal(original.Bars[^1].Volume + 75m, revised.Bars[^1].Volume);
        Assert.Equal(now, revised.Bars[^1].AvailableAtUtc);
        Assert.Null(revised.Bars[^1].KnownAtUtc);
        Assert.Equal(originalDaily.Bars[^1] with { Volume = 175m, AvailableAtUtc = now, RetrievedAtUtc = now }, revisedDaily.Bars[^1]);
        Assert.Empty(revised.Evidence.BasisBreaks);

        provider.Pivot = date.AddDays(-3);
        clock.Now = clock.Now.AddMinutes(31);
        var split = (await series.ReadAsync(WeeklyKey, null, default)).Snapshot;
        Assert.DoesNotContain(split.Bars, bar => bar.SessionDate == revised.Bars[^1].SessionDate);
        Assert.Contains(split.Evidence.Weekly!.Gaps, gap => gap.WeekOf == revised.Bars[^1].SessionDate &&
            gap.Reason.Contains("Price basis", StringComparison.Ordinal));
        Assert.NotEmpty(split.Evidence.BasisBreaks);
    }

    // A duplicate daily timestamp removes only its week and survives a failed retry despite a stored version.
    [Fact]
    public async Task WeeklyProviderConflictStaysUnavailableUntilOneCoherentDailyRowReturns()
    {
        var date = new DateOnly(2026, 11, 16);
        var conflict = date.AddDays(-3);
        var clock = new MutableChartClock(ChartBarProjector.At(date, new TimeOnly(12, 0)));
        var stale = BarAt(ChartBarProjector.At(conflict, TimeOnly.MinValue), "1d", 100m).Bar with
        {
            KnownAtUtc = clock.Now.AddMinutes(40)
        };
        var store = new Mock<ICandleStore>();
        store.Setup(owner => owner.ReadBarsAsync(It.IsAny<CandleStoreReadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CandleStoreReadRequest request, CancellationToken _) =>
                request.Start <= stale.Timestamp && stale.Timestamp < request.End && stale.KnownAtUtc <= request.AsOfUtc
                    ? new[] { stale } : []);
        var provider = new ChartHistoryFixture();
        var series = MakeLevelTestSeries(new ArchivedMinuteFixture(clock).CreateProvider(provider), clock, candles: store.Object);
        var original = (await series.ReadAsync(WeeklyKey, null, default)).Snapshot;
        var affectedWeek = original.Bars[^1].SessionDate;
        Assert.Equal(ChartBarProjector.WeekStart(conflict), affectedWeek);

        provider.ConflictingDaily = conflict;
        clock.Now = clock.Now.AddMinutes(31);
        var ambiguous = (await series.ReadAsync(WeeklyKey, null, default)).Snapshot;
        Assert.Equal(original.Bars.Count - 1, ambiguous.Bars.Count);
        Assert.DoesNotContain(ambiguous.Bars, bar => bar.SessionDate == affectedWeek);
        Assert.Contains(ambiguous.Evidence.Weekly!.Gaps, gap => gap.WeekOf == affectedWeek &&
            gap.Reason == "Conflicting SIP daily candle for 2026-11-13.");
        Assert.NotEqual(original.Evidence.Weekly!.SourceRevision, ambiguous.Evidence.Weekly.SourceRevision);

        provider.ConflictingDaily = null;
        provider.Missing = conflict;
        clock.Now = clock.Now.AddMinutes(31);
        var stillAmbiguous = (await series.ReadAsync(WeeklyKey, null, default)).Snapshot;
        Assert.Equal(ambiguous.Evidence.Weekly.Gaps, stillAmbiguous.Evidence.Weekly!.Gaps);
        Assert.DoesNotContain(stillAmbiguous.Bars, bar => bar.SessionDate == affectedWeek);
        var daily = (await series.ReadAsync(WeeklyKey with { Interval = "1d" }, null, default)).Snapshot;
        Assert.DoesNotContain(daily.Bars, bar => bar.SessionDate == conflict);

        provider.Missing = null;
        clock.Now = clock.Now.AddMinutes(31);
        var repaired = (await series.ReadAsync(WeeklyKey, null, default)).Snapshot;
        Assert.Equal(affectedWeek, repaired.Bars[^1].SessionDate);
        Assert.DoesNotContain(repaired.Evidence.Weekly!.Gaps, gap => gap.WeekOf == affectedWeek);
        Assert.Equal(clock.Now, repaired.Bars[^1].AvailableAtUtc);
        Assert.Equal(4, provider.Requests.Count);
    }

    // Duplicated rows still consume the arrival budget even when the accepted session count fits.
    [Fact]
    public async Task WeeklyDailyAcquisitionRejectsRawRowOverflowBeforePublishing()
    {
        var date = new DateOnly(2026, 11, 13);
        var clock = new MutableChartClock(ChartBarProjector.At(date, new TimeOnly(19, 0)));
        var provider = new ChartHistoryFixture { ConflictingDaily = date };
        var series = MakeLevelTestSeries(new ArchivedMinuteFixture(clock).CreateProvider(provider), clock);

        var overflow = (await series.ReadAsync(WeeklyKey, null, default)).Snapshot;
        Assert.Empty(overflow.Bars);
        Assert.Contains("The daily SIP history read did not complete.", Assert.Single(overflow.Evidence.Notes));
        Assert.Single(provider.Requests);
    }

    // Holiday and weekend events group to their source week with exact original clocks.
    [Fact]
    public async Task WeeklyMarkersKeepHolidayAndAfterCloseClockEvidence()
    {
        var monday = new DateOnly(2026, 11, 9);
        var holiday = ChartBarProjector.At(monday, new TimeOnly(8, 0)).AddTicks(1234567);
        var saturday = ChartBarProjector.At(monday.AddDays(5), new TimeOnly(8, 0)).AddTicks(7654321);
        var known = saturday.AddMinutes(15);
        var clock = new MutableChartClock(saturday.AddMinutes(20));
        var provider = new ChartHistoryFixture { ClosedDate = monday };
        var news = new Mock<INewsFeedRepository>();
        news.Setup(repo => repo.QueryAsync(It.IsAny<NewsFeedQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NewsFeedPage([new PersistedNewsItem
            {
                Id = "holiday", Ticker = "TEST", Provider = "fixture", Timestamp = holiday, IngestedAt = holiday.AddMinutes(5)
            }], 1));
        var earnings = new Mock<IEarningsRepository>();
        earnings.Setup(repo => repo.GetCalendarAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EarningsCalendarEvent[]
            {
                new() { Id = "saturday", Ticker = "TEST", Provider = "fixture", ScheduledAtUtc = saturday, FirstSeenAtUtc = known }
            });
        var series = new ChartSeriesService(new Lazy<IMarketDataProvider>(() => provider), NullCandleStore.Instance,
            null!, StreamingMarketStateOptions.Default, news.Object, earnings.Object, clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ChartSeriesService>.Instance);
        var page = (await series.ReadAsync(WeeklyKey, null, default)).Snapshot;
        Assert.Equal(monday.AddDays(1), page.Bars[^1].SessionDate);
        Assert.All(page.Markers, marker => Assert.Equal(monday.AddDays(1), marker.WeeklySessionDate));
        Assert.Collection(page.Markers, newsMarker => Assert.Equal(holiday, newsMarker.EventAtUtc),
            earningsMarker => Assert.Equal((saturday, known), (earningsMarker.EventAtUtc, earningsMarker.KnownAtUtc!.Value)));
    }
}

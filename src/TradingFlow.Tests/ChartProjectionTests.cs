using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Earnings;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Services.Charts;

namespace TradingFlow.Tests;

public sealed class ChartProjectionTests
{
    // The two right-side bars and their receipt clock determine when a pivot can appear.
    [Fact]
    public void ConfirmedPivotWaitsForLateSecondRightBarAndRetractsOnRevision()
    {
        var date = new DateOnly(2026, 8, 26);
        var open = ChartBarProjector.At(date, new TimeOnly(9, 30));
        var bars = Enumerable.Range(0, 120).Select(index => PivotBar(open.AddMinutes(index * 15), date)).ToArray();
        bars[117] = bars[117] with { Bar = bars[117].Bar with { High = 110m } };
        var late = bars[^1].EndUtc.AddSeconds(63);
        bars[^1] = bars[^1] with { AvailableAtUtc = late };
        var active = new HashSet<DateOnly> { date };

        Assert.Empty(ChartSupportResistance.Calculate("TEST:15m", bars, active, late.AddTicks(-1), 100m));
        var band = Assert.Single(ChartSupportResistance.Calculate("TEST:15m", bars, active, late, 100m));
        Assert.Equal("resistance", band.Type);
        Assert.Equal(bars[117].Bar.Timestamp, band.FirstPivotUtc);
        Assert.Equal(bars[^1].EndUtc, band.LatestConfirmationUtc);
        Assert.Equal(late, band.EffectiveAtUtc);
        Assert.Equal(1, band.PivotCount);
        Assert.Equal(band.Id, Assert.Single(ChartSupportResistance.Calculate("TEST:15m", bars, active, late.AddMinutes(1), 100m)).Id);

        bars[118] = bars[118] with { Bar = bars[118].Bar with { High = 110m }, AvailableAtUtc = late.AddMinutes(1) };
        Assert.Empty(ChartSupportResistance.Calculate("TEST:15m", bars, active, late.AddMinutes(1), 100m));
    }

    // An unresolved gap starts the fixed ATR warm-up again, and inactive dates retire pivots.
    [Fact]
    public void AGapOrSessionExpirySuppressesThePreviousPivot()
    {
        var date = new DateOnly(2026, 8, 26);
        var open = ChartBarProjector.At(date, new TimeOnly(9, 30));
        var bars = Enumerable.Range(0, 120).Select(index => PivotBar(open.AddMinutes(index * 15), date)).ToArray();
        bars[117] = bars[117] with { Bar = bars[117].Bar with { Low = 90m } };
        var end = bars[^1].EndUtc;
        Assert.Empty(ChartSupportResistance.Calculate("TEST:15m", bars, new HashSet<DateOnly> { date.AddDays(1) }, end, 100m));

        bars[^2] = bars[^2] with { Continuity = 1 };
        bars[^1] = bars[^1] with { Continuity = 1 };
        Assert.Empty(ChartSupportResistance.Calculate("TEST:15m", bars, new HashSet<DateOnly> { date }, end, 100m));
    }

    // Quiet minutes prove coverage at their observation time and contribute no artificial OHLCV.
    [Fact]
    public void CoveredQuietMinuteCompletesFiveMinuteCandleWithoutSyntheticVolume()
    {
        var date = new DateOnly(2026, 11, 27);
        var schedule = new MarketSessionSchedule(date, true, new TimeOnly(9, 30), new TimeOnly(13, 0));
        var projector = new ChartBarProjector(new Dictionary<DateOnly, MarketSessionSchedule> { [date] = schedule });
        var open = ChartBarProjector.At(date, schedule.RegularOpen);
        var end = open.AddMinutes(5);
        var proof = end.AddSeconds(21);
        var minutes = new[] { 0, 2, 3, 4 }.ToDictionary(index => open.AddMinutes(index),
            index => BarAt(open.AddMinutes(index), "1m", 100m + index));
        var quiet = new Dictionary<DateTimeOffset, DateTimeOffset> { [open.AddMinutes(1)] = proof };
        var noBreaks = new HashSet<DateTimeOffset>();

        Assert.Empty(projector.Intraday(minutes, quiet, noBreaks, "5m", false, end, proof.AddTicks(-1)));
        var candle = Assert.Single(projector.Intraday(minutes, quiet, noBreaks, "5m", false, end, proof));
        Assert.Equal("regular", candle.Segment);
        Assert.Equal(open, candle.Bar.Timestamp);
        Assert.Equal(end, candle.EndUtc);
        Assert.Equal(100m, candle.Bar.Open);
        Assert.Equal(104m, candle.Bar.Close);
        Assert.Equal(400m, candle.Bar.Volume);
        Assert.Equal(proof, candle.AvailableAtUtc);
        Assert.Empty(projector.Intraday(minutes, quiet, new HashSet<DateTimeOffset> { open.AddMinutes(2) }, "5m", false, end, proof));
    }

    // A continuous emitted pair cannot hide missing leading or trailing source minutes.
    [Fact]
    public void CoverageChecksExpectedEdgesWithoutInventingCandles()
    {
        var date = new DateOnly(2026, 8, 26);
        var projector = new ChartBarProjector(new Dictionary<DateOnly, MarketSessionSchedule>
        {
            [date] = MarketSessionSchedule.RegularDay(date)
        });
        var open = ChartBarProjector.At(date, new TimeOnly(9, 30));
        var end = open.AddMinutes(4);
        var source = new[] { open.AddMinutes(1), open.AddMinutes(2) }.ToDictionary(stamp => stamp, stamp => BarAt(stamp, "1m", 100m));
        var quiet = new Dictionary<DateTimeOffset, DateTimeOffset>();
        var noBreaks = new HashSet<DateTimeOffset>();

        var emitted = projector.Intraday(source, quiet, noBreaks, "1m", false, end, end);
        Assert.Equal(emitted[0].Continuity, emitted[^1].Continuity);
        Assert.False(projector.IsIntradayCovered([date], source, quiet, noBreaks, "1m", false, end, end));
        quiet[open] = end;
        quiet[open.AddMinutes(3)] = end;
        Assert.True(projector.IsIntradayCovered([date], source, quiet, noBreaks, "1m", false, end, end));
    }

    // The actual early exchange close and an absent session remain distinct from daily timestamps.
    [Fact]
    public void DailyUsesEarlyCloseAndSplitsContinuityAtMissingSession()
    {
        var first = new DateOnly(2026, 11, 24);
        var missing = first.AddDays(1);
        var holiday = first.AddDays(2);
        var last = first.AddDays(3);
        var projector = new ChartBarProjector(new Dictionary<DateOnly, MarketSessionSchedule>
        {
            [first] = MarketSessionSchedule.RegularDay(first),
            [missing] = MarketSessionSchedule.RegularDay(missing),
            [holiday] = MarketSessionSchedule.Closed(holiday),
            [last] = new(last, true, new TimeOnly(9, 30), new TimeOnly(13, 0))
        });
        var earlyClose = new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);
        var sources = new[] { first, last }.Select(date => BarAt(ChartBarProjector.At(date, TimeOnly.MinValue), "1d", 100m)).ToArray();

        Assert.Single(projector.Daily(sources, new HashSet<DateTimeOffset>(), earlyClose.AddTicks(-1), earlyClose));
        var completed = projector.Daily(sources, new HashSet<DateTimeOffset>(), earlyClose, earlyClose);
        Assert.Equal(2, completed.Count);
        Assert.Equal(last, completed[^1].SessionDate);
        Assert.Equal(earlyClose, completed[^1].EndUtc);
        Assert.NotEqual(completed[0].Continuity, completed[^1].Continuity);
        Assert.DoesNotContain(ChartContextLevels.Daily(completed, [first, missing, last], last, sources[0].Bar.Timestamp),
            level => level.Key is "prior_session_high" or "sma50");
    }

    // A previous-basis SMA or range cannot supply a current value or distance after a split.
    [Fact]
    public void DailySuppressesOldBasisLevelsUntilNewBasisWarmsUp()
    {
        var start = new DateOnly(2026, 6, 1);
        var daily = Enumerable.Range(0, 56).Select(index =>
        {
            var date = start.AddDays(index);
            var time = ChartBarProjector.At(date, TimeOnly.MinValue);
            var bar = BarAt(time, "1d", index == 55 ? 50m : 100m).Bar;
            return new ChartCompletedBar(bar, time.AddHours(16), date, "daily", index == 55 ? 1 : 0,
                "fixture", null, time.AddHours(16));
        }).ToArray();
        var scheduled = daily.Select(bar => bar.SessionDate).ToArray();
        var beforeBreak = ChartContextLevels.Daily(daily.Take(55).ToArray(), scheduled, scheduled[54], daily[0].Bar.Timestamp);
        Assert.Contains(beforeBreak, level => level.Key == "sma50");
        Assert.Contains(beforeBreak, level => level.Key == "prior_session_high");

        var latest = ChartContextLevels.Daily(daily, scheduled, scheduled[^1], daily[0].Bar.Timestamp);
        Assert.DoesNotContain(latest, level => level.Key is "sma50" or "prior_session_high" or "prior_week_high");
    }

    // Several chart modes share one underlying acquisition, including stable reconstructed timestamps across refresh.
    [Fact]
    public async Task ConcurrentReadsSurviveCallerCancellationAndRefreshOnlyRecentDailySessions()
    {
        var date = new DateOnly(2026, 11, 16);
        var now = ChartBarProjector.At(date, new TimeOnly(12, 0));
        var clock = new MutableChartClock(now);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ChartHistoryFixture { ReleaseFetch = release.Task };
        var cache = new ChartHistorySource(new Lazy<IMarketDataProvider>(() => provider), NullCandleStore.Instance, clock,
            TimeSpan.FromMinutes(3), NullLogger.Instance);
        using var left = new CancellationTokenSource();
        var departed = cache.ReadAsync("TEST", true, date, left.Token);
        var pending = Enumerable.Range(0, 4).Select(_ => cache.ReadAsync("TEST", true, date, default)).ToArray();
        left.Cancel();
        release.SetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await departed);
        var initial = await Task.WhenAll(pending);
        var first = initial[0];
        Assert.All(initial, result => Assert.Same(first, result));
        Assert.Single(provider.Requests);
        Assert.InRange(first.Bars.Count, 490, 500);
        Assert.Null(first.Bars.Values.First().Bar.KnownAtUtc);
        Assert.Equal(now, first.Bars.Values.First().RetrievedAtUtc);

        clock.Now = now.AddMinutes(3);
        await cache.ReadAsync("TEST", true, date, default);
        Assert.Single(provider.Requests);
        clock.Now = now.AddMinutes(31);
        var refreshed = await cache.ReadAsync("TEST", true, date, default);
        Assert.Equal(2, provider.Requests.Count);
        Assert.InRange(provider.Requests[^1].End - provider.Requests[^1].Start, TimeSpan.Zero, TimeSpan.FromDays(5));
        Assert.Equal(first.Bars.Count, refreshed.Bars.Count);
        Assert.All(refreshed.Bars.Values, bar => Assert.Equal(now, bar.RetrievedAtUtc));
    }

    // A failed daily overlap query keeps its last successful clock and retries on the next cache refresh.
    [Fact]
    public async Task FailedDailyReconciliationRetriesBeforeTheNormalCadence()
    {
        var date = new DateOnly(2026, 11, 16);
        var now = ChartBarProjector.At(date, new TimeOnly(12, 0));
        var clock = new MutableChartClock(now);
        var provider = new ChartHistoryFixture();
        var cache = new ChartHistorySource(new Lazy<IMarketDataProvider>(() => provider), NullCandleStore.Instance, clock,
            TimeSpan.FromMinutes(3), NullLogger.Instance);

        var initial = await cache.ReadAsync("TEST", true, date, default);
        Assert.Equal(now, initial.DailyReconciledAtUtc);
        provider.FailOnce = PreviousSessions(date, 2);
        clock.Now = now.AddMinutes(31);
        var failed = await cache.ReadAsync("TEST", true, date, default);
        Assert.NotNull(failed.Limitation);
        Assert.Equal(now, failed.DailyReconciledAtUtc);
        Assert.Equal(2, provider.Requests.Count);

        clock.Now = now.AddMinutes(34);
        var recovered = await cache.ReadAsync("TEST", true, date, default);
        Assert.Null(recovered.Limitation);
        Assert.Equal(clock.Now, recovered.DailyReconciledAtUtc);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal(provider.Requests[^2], provider.Requests[^1]);
    }

    // A bounded partial seed retries older failures while preserving acquired windows and honest coverage.
    [Fact]
    public async Task PartialMinuteSeedRetriesUnacquiredSessionsWithoutClaimingUnknownOmissions()
    {
        var date = new DateOnly(2026, 11, 16);
        var now = ChartBarProjector.At(date, new TimeOnly(12, 0));
        var clock = new MutableChartClock(now);
        var provider = new ChartHistoryFixture { FailOnce = date.AddDays(-5) };
        var cache = new ChartHistorySource(new Lazy<IMarketDataProvider>(() => provider), NullCandleStore.Instance, clock,
            TimeSpan.FromMinutes(3), NullLogger.Instance);
        var partial = await cache.ReadAsync("TEST", false, date, default);
        Assert.NotNull(partial.Limitation);
        Assert.Null(partial.CoveredThroughUtc);
        Assert.DoesNotContain(date.AddDays(-5), partial.AcquiredSessions);
        var firstRequestCount = provider.Requests.Count;

        clock.Now = now.AddMinutes(3);
        var refreshed = await cache.ReadAsync("TEST", false, date, default);
        Assert.Contains(provider.Requests.Skip(firstRequestCount), request => ChartBarProjector.NewYorkDate(request.Start) == date.AddDays(-5));
        Assert.DoesNotContain(date.AddDays(-5), refreshed.AcquiredSessions);
        Assert.Equal(clock.Now.AddMinutes(30), refreshed.RetryUnverifiedAfterUtc[date.AddDays(-5)]);
        Assert.Null(refreshed.CoveredThroughUtc);
        Assert.Null(refreshed.Limitation);
        Assert.Empty(refreshed.Quiet);
    }

    // An absent daily row backs off. A later repair plus recent revision preserves verified rows between them.
    [Fact]
    public async Task UnverifiedOlderDailySessionBacksOffThenRepairsWithoutFalseInteriorBreaks()
    {
        var date = new DateOnly(2026, 11, 16);
        var missing = date.AddDays(-7);
        var revised = PreviousSessions(date, 1);
        var missingStamp = ChartBarProjector.At(missing, TimeOnly.MinValue);
        var revisedStamp = ChartBarProjector.At(revised, TimeOnly.MinValue);
        var interiorStamp = ChartBarProjector.At(missing.AddDays(1), TimeOnly.MinValue);
        var now = ChartBarProjector.At(date, new TimeOnly(12, 0));
        var clock = new MutableChartClock(now);
        var provider = new ChartHistoryFixture { Missing = missing };
        var cache = new ChartHistorySource(new Lazy<IMarketDataProvider>(() => provider), NullCandleStore.Instance, clock,
            TimeSpan.FromMinutes(3), NullLogger.Instance);
        var first = await cache.ReadAsync("TEST", true, date, default);
        Assert.DoesNotContain(missing, first.AcquiredSessions);
        Assert.Equal(now.AddMinutes(30), first.RetryUnverifiedAfterUtc[missing]);

        clock.Now = now.AddMinutes(3);
        await cache.ReadAsync("TEST", true, date, default);
        Assert.Single(provider.Requests);

        provider.Missing = null;
        provider.Pivot = revised;
        clock.Now = now.AddMinutes(31);
        var repaired = await cache.ReadAsync("TEST", true, date, default);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(missingStamp, provider.Requests[^1].Start);
        Assert.Contains(missing, repaired.AcquiredSessions);
        Assert.False(repaired.RetryUnverifiedAfterUtc.ContainsKey(missing));
        Assert.Contains(missingStamp, repaired.Bars.Keys);
        Assert.Equal(110m, repaired.Bars[revisedStamp].Bar.High);
        Assert.Equal(clock.Now, repaired.Bars[revisedStamp].RetrievedAtUtc);
        Assert.Equal(now, repaired.Bars[interiorStamp].RetrievedAtUtc);
        Assert.DoesNotContain(repaired.BasisBreaks, stamp => stamp > missingStamp && stamp <= revisedStamp);
    }

    // Prove exact overlap bounds, one final full acquisition and reuse over a weekend rollover.
    [Fact]
    public async Task SettledMinuteHistoryUsesSmallOverlapAndSkipsClosedRefreshes()
    {
        var date = new DateOnly(2026, 11, 13);
        var clock = new MutableChartClock(ChartBarProjector.At(date, new TimeOnly(10, 3)));
        var opening = ChartBarProjector.At(date, new TimeOnly(4, 0));
        var close = ChartBarProjector.At(date, new TimeOnly(20, 0));
        var firstReceipt = clock.Now;
        var fixture = new ArchivedMinuteFixture(clock);
        var cache = fixture.CreateSource();

        var initial = await cache.ReadAsync("TEST", false, date, default);
        Assert.Equal(15, fixture.Requests.Count);
        Assert.Equal(firstReceipt.AddMinutes(-3), initial.CoveredThroughUtc);
        Assert.Equal(firstReceipt, initial.Bars[opening].RetrievedAtUtc);

        clock.Now = firstReceipt.AddMinutes(1);
        await cache.ReadAsync("TEST", false, date, default);
        Assert.Equal(15, fixture.Requests.Count);

        clock.Now = firstReceipt.AddMinutes(3);
        var next = await cache.ReadAsync("TEST", false, date, default);
        Assert.Equal(16, fixture.Requests.Count);
        Assert.Equal((firstReceipt.AddMinutes(-18), clock.Now.AddMinutes(-3)), fixture.Requests[^1]);
        Assert.Equal(clock.Now.AddMinutes(-3), next.CoveredThroughUtc);
        Assert.Equal(firstReceipt, next.Bars[opening].RetrievedAtUtc);

        clock.Now = firstReceipt.AddMinutes(31);
        await cache.ReadAsync("TEST", false, date, default);
        Assert.Equal((opening, clock.Now.AddMinutes(-3)), fixture.Requests[^1]);

        clock.Now = close.AddMinutes(4);
        fixture.PartialNextCapture = true;
        var partial = await cache.ReadAsync("TEST", false, date, default);
        Assert.Equal((opening, close), fixture.Requests[^1]);
        Assert.NotNull(partial.Limitation);
        Assert.DoesNotContain(date, partial.AcquiredSessions);
        Assert.Equal(firstReceipt.AddMinutes(28), partial.CoveredThroughUtc);

        clock.Now = close.AddMinutes(7);
        var settled = await cache.ReadAsync("TEST", false, date, default);
        Assert.Equal((opening, close), fixture.Requests[^1]);
        Assert.Equal(close, settled.CoveredThroughUtc);
        Assert.Contains(date, settled.AcquiredSessions);
        var archived = fixture.Requests.Count;

        clock.Now = close.AddHours(1);
        await cache.ReadAsync("TEST", false, date, default);
        foreach (var days in new[] { 1, 2, 3 })
        {
            var anchor = date.AddDays(days);
            clock.Now = ChartBarProjector.At(anchor, new TimeOnly(3, 0));
            var retained = await cache.ReadAsync("TEST", false, anchor, default);
            Assert.Equal(close, retained.CoveredThroughUtc);
        }
        Assert.Equal(archived, fixture.Requests.Count);

        var monday = date.AddDays(3);
        clock.Now = ChartBarProjector.At(monday, new TimeOnly(4, 9));
        await cache.ReadAsync("TEST", false, monday, default);
        Assert.Equal(archived + 1, fixture.Requests.Count);
        Assert.Equal((ChartBarProjector.At(monday, new TimeOnly(4, 0)), clock.Now.AddMinutes(-3)), fixture.Requests[^1]);
    }

    // An empty suffix retains the last proven minute, then retries the full range and backs off if it is also empty.
    [Fact]
    public async Task EmptyMinuteSuffixTriggersFullRepairAndDoesNotAdvance()
    {
        var date = new DateOnly(2026, 11, 13);
        var now = ChartBarProjector.At(date, new TimeOnly(10, 3));
        var clock = new MutableChartClock(now);
        var fixture = new ArchivedMinuteFixture(clock);
        var cache = fixture.CreateSource();
        var seed = await cache.ReadAsync("TEST", false, date, default);
        fixture.Empty = true;

        clock.Now = now.AddMinutes(3);
        var emptySuffix = await cache.ReadAsync("TEST", false, date, default);
        Assert.Equal(now.AddMinutes(-18), fixture.Requests[^1].Start);
        Assert.Equal(seed.CoveredThroughUtc, emptySuffix.CoveredThroughUtc);
        Assert.Equal(seed.Quiet.Keys.Order(), emptySuffix.Quiet.Keys.Order());

        clock.Now = now.AddMinutes(6);
        var emptyFull = await cache.ReadAsync("TEST", false, date, default);
        Assert.Equal(ChartBarProjector.At(date, new TimeOnly(4, 0)), fixture.Requests[^1].Start);
        Assert.Equal(seed.CoveredThroughUtc, emptyFull.CoveredThroughUtc);
        Assert.Equal(clock.Now.AddMinutes(30), emptyFull.RetryUnverifiedAfterUtc[date]);
        var archived = fixture.Requests.Count;

        clock.Now = now.AddMinutes(10);
        await cache.ReadAsync("TEST", false, date, default);
        Assert.Equal(archived, fixture.Requests.Count);

        fixture.Empty = false;
        clock.Now = now.AddMinutes(37);
        var recovered = await cache.ReadAsync("TEST", false, date, default);
        Assert.Equal(archived + 1, fixture.Requests.Count);
        Assert.Equal(clock.Now.AddMinutes(-3), recovered.CoveredThroughUtc);
    }

    // A fully archived retry can revise or remove one bar, without moving any unchanged evidence clocks.
    [Fact]
    public async Task ArchivedReconciliationRetainsStableClocksAndRecordsChangedEvidence()
    {
        var date = new DateOnly(2026, 11, 13);
        var now = ChartBarProjector.At(date, new TimeOnly(10, 3));
        var opening = ChartBarProjector.At(date, new TimeOnly(4, 0));
        var clock = new MutableChartClock(now);
        var fixture = new ArchivedMinuteFixture(clock);
        var cache = fixture.CreateSource();
        var before = await cache.ReadAsync("TEST", false, date, default);
        fixture.Revised = opening.AddMinutes(5);
        fixture.Removed = opening.AddMinutes(10);
        fixture.Late = opening.AddMinutes(11);
        Assert.Contains(fixture.Removed.Value, before.Bars.Keys);
        Assert.Contains(fixture.Late.Value, before.Quiet.Keys);

        clock.Now = now.AddMinutes(31);
        var revised = await cache.ReadAsync("TEST", false, date, default);
        Assert.Equal(now, revised.Bars[opening].RetrievedAtUtc);
        Assert.Equal(now, revised.Quiet[opening.AddMinutes(1)]);
        Assert.Equal(101m, revised.Bars[fixture.Revised.Value].Bar.Close);
        Assert.Equal(clock.Now, revised.Bars[fixture.Revised.Value].RetrievedAtUtc);
        Assert.DoesNotContain(fixture.Removed.Value, revised.Bars.Keys);
        Assert.Equal(clock.Now, revised.Quiet[fixture.Removed.Value]);
        Assert.DoesNotContain(fixture.Late.Value, revised.Quiet.Keys);
        Assert.Equal(clock.Now, revised.Bars[fixture.Late.Value].RetrievedAtUtc);
    }

    // Wait for the actual early close and settling delay, then reuse its final overlap through the weekend.
    [Fact]
    public async Task NativeDailyHistoryWaitsForScheduledSettlementAndReusesWeekendSeed()
    {
        var date = new DateOnly(2026, 11, 27);
        var close = ChartBarProjector.At(date, new TimeOnly(13, 0));
        var clock = new MutableChartClock(close.AddMinutes(1));
        var provider = new ChartHistoryFixture { EarlyClose = date };
        var cache = new ChartHistorySource(new Lazy<IMarketDataProvider>(() => provider), NullCandleStore.Instance, clock,
            TimeSpan.FromMinutes(3), NullLogger.Instance);
        var candle = ChartBarProjector.At(date, TimeOnly.MinValue);

        var before = await cache.ReadAsync("TEST", true, date, default);
        Assert.Single(provider.Requests);
        Assert.DoesNotContain(candle, before.Bars.Keys);

        clock.Now = close.AddMinutes(4);
        var settled = await cache.ReadAsync("TEST", true, date, default);
        Assert.Equal(2, provider.Requests.Count);
        Assert.True(provider.Requests[^1].Start < candle);
        Assert.Contains(date, settled.AcquiredSessions);
        Assert.Equal(close, settled.CoveredThroughUtc);
        Assert.Equal(clock.Now, settled.Bars[candle].RetrievedAtUtc);

        clock.Now = close.AddHours(7);
        await cache.ReadAsync("TEST", true, date, default);
        clock.Now = close.AddDays(2);
        var weekend = await cache.ReadAsync("TEST", true, date.AddDays(2), default);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(close, weekend.CoveredThroughUtc);
        Assert.Equal(settled.Bars[candle], weekend.Bars[candle]);
    }

    // Tie all relative levels to the latest displayed session, even on archived pages read after a close or before an open.
    [Theory]
    [InlineData(13, 20, 13, 13)]
    [InlineData(15, 12, 13, 13)]
    [InlineData(16, 8, 13, 13)]
    [InlineData(16, 10, 16, 13)]
    [InlineData(26, 12, 25, 25)]
    public async Task ChartLevelsFollowTheLatestDisplayedSession(int requestDay, int requestHour,
        int lastIntradayDay, int lastDailyDay)
    {
        var requestDate = new DateOnly(2026, 11, requestDay);
        var expectedIntraday = new DateOnly(2026, 11, lastIntradayDay);
        var expectedDaily = new DateOnly(2026, 11, lastDailyDay);
        var holiday = new DateOnly(2026, 11, 26);
        var cutoff = ChartBarProjector.At(requestDate, new TimeOnly(requestHour, 0));
        var clock = new MutableChartClock(cutoff.AddSeconds(1));
        var calendar = new ChartHistoryFixture { ClosedDate = holiday };
        var minuteProvider = new ArchivedMinuteFixture(clock).CreateProvider(calendar);
        var intraday = MakeLevelTestSeries(minuteProvider, clock);
        var daily = MakeLevelTestSeries(calendar, clock);

        var minuteSnapshot = (await intraday.ReadAsync(new ChartSeriesKey("TEST", "1m", "regular", "intraday"), cutoff, default)).Snapshot;
        Assert.Equal(expectedIntraday, minuteSnapshot.Bars[^1].SessionDate);
        var vwap = Assert.Single(minuteSnapshot.Levels, level => level.Key == "vwap");
        Assert.Equal(expectedIntraday, vwap.SourceDate);
        Assert.Equal(minuteSnapshot.Bars.Count, vwap.Points!.Count);
        Assert.Equal(minuteSnapshot.Bars[^1].StartUtc, vwap.Points[^1].StartUtc);
        Assert.Equal(expectedIntraday, Assert.Single(minuteSnapshot.Levels, level => level.Key == "opening_range_high").SourceDate);
        Assert.Equal(expectedIntraday, Assert.Single(minuteSnapshot.Levels, level => level.Key == "premarket_low").SourceDate);
        Assert.Equal(PreviousSessions(expectedIntraday, 1),
            Assert.Single(minuteSnapshot.Levels, level => level.Key == "prior_session_high").SourceDate);

        var dailySnapshot = (await daily.ReadAsync(new ChartSeriesKey("TEST", "1d", "regular", "swing"), cutoff, default)).Snapshot;
        Assert.Equal(expectedDaily, dailySnapshot.Bars[^1].SessionDate);
        Assert.Equal(PreviousSessions(expectedDaily, 1),
            Assert.Single(dailySnapshot.Levels, level => level.Key == "prior_session_low").SourceDate);
    }

    // Use the normal history projector with empty, independent event repositories for level assertions.
    private static ChartSeriesService MakeLevelTestSeries(IMarketDataProvider provider, TimeProvider clock)
    {
        var news = new Mock<INewsFeedRepository>();
        news.Setup(repo => repo.QueryAsync(It.IsAny<NewsFeedQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NewsFeedPage([], 0));
        var earnings = new Mock<IEarningsRepository>();
        earnings.Setup(repo => repo.GetCalendarAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EarningsCalendarEvent>());
        return new ChartSeriesService(new Lazy<IMarketDataProvider>(() => provider), NullCandleStore.Instance,
            null!, StreamingMarketStateOptions.Default, news.Object, earnings.Object, clock, NullLogger<ChartSeriesService>.Instance);
    }

    // Native daily structure has distinct week, month and year windows with the same source evidence.
    [Theory]
    [InlineData(3, true, true, true)]
    [InlineData(10, false, true, true)]
    [InlineData(90, false, false, true)]
    [InlineData(260, false, false, false)]
    public async Task NativeDailyContextSelectsConfirmedPivotByScheduledAge(int age, bool week, bool month, bool year)
    {
        var date = new DateOnly(2026, 11, 16);
        var now = ChartBarProjector.At(date, new TimeOnly(20, 30));
        var clock = new MutableChartClock(now);
        var provider = new ChartHistoryFixture { Pivot = PreviousSessions(date, age + 2) };
        var news = new Mock<INewsFeedRepository>();
        news.Setup(repo => repo.QueryAsync(It.IsAny<NewsFeedQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(new NewsFeedPage([], 0));
        var earnings = new Mock<IEarningsRepository>();
        earnings.Setup(repo => repo.GetCalendarAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyCollection<string>>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<EarningsCalendarEvent>());
        var series = new ChartSeriesService(new Lazy<IMarketDataProvider>(() => provider), NullCandleStore.Instance,
            null!, StreamingMarketStateOptions.Default, news.Object, earnings.Object, clock, NullLogger<ChartSeriesService>.Instance);

        foreach (var (context, expected) in new[] { ("week", week), ("month", month), ("year", year) })
        {
            var key = new ChartSeriesKey("TEST", "1d", "regular", context);
            var snapshot = (await series.ReadAsync(key, null, default)).Snapshot;
            if (expected)
            {
                var zone = Assert.Single(snapshot.Zones);
                Assert.True(zone.CurrentOnly);
                Assert.Equal("1d", zone.SourceTimeframe);
                Assert.Equal(now, zone.EffectiveAtUtc);
            }
            else
            {
                Assert.Empty(snapshot.Zones);
            }
        }
        Assert.Single(provider.Requests);

        clock.Now = now.AddMinutes(3);
        var reloaded = (await series.ReadAsync(new ChartSeriesKey("TEST", "1d", "regular", "year"), null, default)).Snapshot;
        if (year)
        {
            Assert.Equal(now, Assert.Single(reloaded.Zones).EffectiveAtUtc);
        }
        Assert.Single(provider.Requests);
    }

    // A history page queries only its own publication window and retains separate receipt clocks.
    [Fact]
    public async Task HistoryMarkersQueryTheViewportAndPreserveStoredEvidence()
    {
        var date = new DateOnly(2026, 11, 16);
        var now = ChartBarProjector.At(date, new TimeOnly(20, 30));
        var cutoff = ChartBarProjector.At(date.AddDays(-7), new TimeOnly(16, 0));
        var eventAt = cutoff.AddMinutes(-30);
        var received = eventAt.AddMinutes(5);
        var news = new Mock<INewsFeedRepository>();
        news.Setup(repo => repo.QueryAsync(It.Is<NewsFeedQuery>(query =>
                query.Ticker == "TEST" && query.StockLinkedOnly && query.SinceUtc < eventAt &&
                query.UntilUtc == cutoff && query.Take == 100), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NewsFeedPage(
            [
                new PersistedNewsItem { Id = "older", Ticker = "TEST", Provider = "fixture", Timestamp = eventAt,
                    IngestedAt = received, Headline = "Historical article", Url = "javascript:ignored" }
            ], 1));
        var earnings = new Mock<IEarningsRepository>();
        earnings.Setup(repo => repo.GetCalendarAsync(It.IsAny<DateOnly>(), date.AddDays(-7), It.IsAny<IReadOnlyCollection<string>>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(new EarningsCalendarEvent[]
            {
                new() { Id = "schedule", Ticker = "TEST", Provider = "fixture", ScheduledAtUtc = eventAt.AddMinutes(1),
                    FirstSeenAtUtc = received, IsScheduleEstimate = true, ResultFirstSeenAtUtc = now.AddMinutes(1) }
            });
        var provider = new ChartHistoryFixture();
        var clock = new MutableChartClock(now);
        var series = new ChartSeriesService(new Lazy<IMarketDataProvider>(() => provider), NullCandleStore.Instance,
            null!, StreamingMarketStateOptions.Default, news.Object, earnings.Object, clock, NullLogger<ChartSeriesService>.Instance);
        var key = new ChartSeriesKey("TEST", "1d", "regular", "week");

        var snapshot = (await series.ReadAsync(key, cutoff, default)).Snapshot;
        var again = (await series.ReadAsync(key, cutoff, default)).Snapshot;

        Assert.Equal(snapshot.Markers, again.Markers);
        Assert.Collection(snapshot.Markers,
            article =>
            {
                Assert.Equal("news", article.Kind);
                Assert.Equal(eventAt, article.EventAtUtc);
                Assert.Equal(received, article.KnownAtUtc);
                Assert.Null(article.Url);
            },
            release =>
            {
                Assert.Equal("earnings", release.Kind);
                Assert.True(release.IsEstimate);
                Assert.Equal(received, release.KnownAtUtc);
                Assert.Null(release.Detail);
            });
        news.Verify(repo => repo.QueryAsync(It.IsAny<NewsFeedQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // A complete empty response cannot prove a quiet session even when the provider documents omitted trade intervals.
    [Fact]
    public async Task ArchivedEmptyMinuteResponseNeverProvesQuietCoverage()
    {
        var date = new DateOnly(2026, 11, 16);
        var now = ChartBarProjector.At(date, new TimeOnly(12, 0));
        var provider = new Mock<IMarketDataProvider>(MockBehavior.Strict);
        var capture = provider.As<IHistoricalMarketDataEvidenceProvider>();
        capture.Setup(owner => owner.CaptureHistoricalBarsAsync("TEST", "1m", It.IsAny<DateTimeOffset>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(
                new HistoricalBarsCapture([], new HistoricalQueryEvidence(now, now, true,
                    [new RawArchiveReceipt("fixture", "fixture", null!)])));
        provider.As<IMarketDataCompletenessProvider>().SetupGet(owner => owner.OmittedSubDailyIntervalsMeanNoQualifyingTrades).Returns(true);
        var calendar = new ChartHistoryFixture();
        provider.As<IMarketSessionScheduleProvider>()
            .Setup(owner => owner.LoadMarketSessionSchedulesAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .Returns((DateOnly start, DateOnly end, CancellationToken token) => calendar.LoadMarketSessionSchedulesAsync(start, end, token));
        var cache = new ChartHistorySource(new Lazy<IMarketDataProvider>(() => provider.Object), NullCandleStore.Instance,
            new MutableChartClock(now), TimeSpan.FromMinutes(3), NullLogger.Instance);

        var batch = await cache.ReadAsync("TEST", false, date, default);

        Assert.Empty(batch.Bars);
        Assert.Empty(batch.Quiet);
        Assert.Empty(batch.AcquiredSessions);
        Assert.NotEmpty(batch.RetryUnverifiedAfterUtc);
        Assert.Null(batch.CoveredThroughUtc);
        capture.Verify(owner => owner.CaptureHistoricalBarsAsync("TEST", "1m", It.IsAny<DateTimeOffset>(),
            It.IsAny<DateTimeOffset>(), It.IsInRange(1L, 961L, Moq.Range.Inclusive), It.IsAny<CancellationToken>()), Times.Exactly(15));
    }

    // Normalize chart presentation independently of symbol admission and refuse fabricated aggregate intervals.
    [Theory]
    [InlineData("1m", "regular", "auto", "intraday")]
    [InlineData("15m", "extended", "intraday", "intraday")]
    [InlineData("1d", "regular", "auto", "swing")]
    [InlineData("1d", "regular", "week", "week")]
    [InlineData("1d", "regular", "month", "month")]
    [InlineData("1d", "regular", "year", "year")]
    public void NormalizeAdmitsOnlySupportedNativeContexts(string interval, string session, string context, string expected)
    {
        var key = ChartSeriesService.Normalize(new ChartSeriesKey(" brk.b ", interval, session, context));

        Assert.Equal("BRK-B", key.Symbol);
        Assert.Equal(expected, key.Context);
    }

    // Weekly and annual candle fabrication and 24-hour equity schedules are unsupported.
    [Theory]
    [InlineData("1w", "regular", "year")]
    [InlineData("1d", "extended", "month")]
    [InlineData("5m", "regular", "year")]
    public void NormalizeRefusesUnsupportedSeries(string interval, string session, string context) =>
        Assert.Throws<ArgumentException>(() => ChartSeriesService.Normalize(new ChartSeriesKey("TEST", interval, session, context)));

    // Count fixture weeks by scheduled trading sessions without synthesizing weekend bars.
    private static DateOnly PreviousSessions(DateOnly date, int count)
    {
        while (count > 0)
        {
            date = date.AddDays(-1);
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                count--;
            }
        }
        return date;
    }

    // Give most bars identical extrema so deliberate fixture pivots are the only strict candidates.
    private static ChartCompletedBar PivotBar(DateTimeOffset timestamp, DateOnly date)
    {
        var bar = BarAt(timestamp, "15m", 100m).Bar;
        return new ChartCompletedBar(bar, timestamp.AddMinutes(15), date, "regular", 0,
            "fixture", null, timestamp.AddMinutes(15));
    }

    // Supply a completed SIP price with independent point-in-time availability.
    private static ChartSourceBar BarAt(DateTimeOffset timestamp, string interval, decimal close)
    {
        var received = timestamp.AddMinutes(1);
        return new ChartSourceBar(new OhlcvBar("TEST", timestamp, interval, close, close + 1m, close - 1m,
            close, 100m, "sip", "all", received), "fixture", null, received);
    }

    private sealed class MutableChartClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        // Move time without real delay or provider access.
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ArchivedMinuteFixture(MutableChartClock clock)
    {
        public List<(DateTimeOffset Start, DateTimeOffset End)> Requests { get; } = [];
        public bool Empty { get; set; }
        public bool PartialNextCapture { get; set; }
        public DateTimeOffset? Revised { get; set; }
        public DateTimeOffset? Removed { get; set; }
        public DateTimeOffset? Late { get; set; }

        // Exercise the production archived source path without contacting the provider.
        public ChartHistorySource CreateSource() =>
            new(new Lazy<IMarketDataProvider>(() => CreateProvider(new ChartHistoryFixture())), NullCandleStore.Instance,
                clock, TimeSpan.FromMinutes(3), NullLogger.Instance);

        // Present the same archived test source to the full chart series reader and the cache tests.
        public IMarketDataProvider CreateProvider(ChartHistoryFixture calendar)
        {
            var provider = new Mock<IMarketDataProvider>(MockBehavior.Strict);
            provider.As<IHistoricalMarketDataEvidenceProvider>()
                .Setup(owner => owner.CaptureHistoricalBarsAsync("TEST", "1m", It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns((string symbol, string interval, DateTimeOffset start, DateTimeOffset end, long cap, CancellationToken token) =>
                    Task.FromResult(Capture(start, end, cap)));
            provider.As<IMarketDataCompletenessProvider>().SetupGet(owner => owner.OmittedSubDailyIntervalsMeanNoQualifyingTrades).Returns(true);
            provider.As<IMarketSessionScheduleProvider>()
                .Setup(owner => owner.LoadMarketSessionSchedulesAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
                .Returns((DateOnly start, DateOnly end, CancellationToken token) => calendar.LoadMarketSessionSchedulesAsync(start, end, token));
            return provider.Object;
        }

        // Return sparse but explicitly archived SIP windows, including intentional failure and revision cases.
        private HistoricalBarsCapture Capture(DateTimeOffset start, DateTimeOffset end, long cap)
        {
            Requests.Add((start, end));
            var count = (int)(end - start).TotalMinutes;
            Assert.Equal(count + 1L, cap);
            var bars = Empty ? [] : Enumerable.Range(0, count).Select(offset => start.AddMinutes(offset))
                .Where(time => time != Removed && (time.Minute % 5 == 0 || time == Late))
                .Select(time => BarAt(time, "1m", time == Revised ? 101m : 100m).Bar with { CoverageVerifiedThroughUtc = end })
                .ToArray();
            var complete = !PartialNextCapture;
            PartialNextCapture = false;
            return new HistoricalBarsCapture(bars,
                new HistoricalQueryEvidence(clock.Now, clock.Now, complete, [new RawArchiveReceipt("fixture", "fixture", null!)]));
        }
    }

    private sealed class ChartHistoryFixture : IMarketDataProvider, IMarketSessionScheduleProvider
    {
        public List<(DateTimeOffset Start, DateTimeOffset End)> Requests { get; } = [];
        public DateOnly? FailOnce { get; set; }
        public DateOnly? Pivot { get; set; }
        public DateOnly? Missing { get; set; }
        public DateOnly? EarlyClose { get; init; }
        public DateOnly? ClosedDate { get; init; }
        public Task ReleaseFetch { get; init; } = Task.CompletedTask;

        // Return deterministic native daily prices while exposing the bounds of each archive acquisition.
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes, DateTimeOffset start, DateTimeOffset end,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add((start, end));
            await Task.Yield();
            await ReleaseFetch.WaitAsync(cancellationToken);
            var first = ChartBarProjector.NewYorkDate(start);
            if (first == FailOnce)
            {
                FailOnce = null;
                throw new IOException("Synthetic partial history read.");
            }
            if (timeframes.Single() == "1m")
            {
                yield return BarAt(ChartBarProjector.At(first, new TimeOnly(9, 30)), "1m", 100m).Bar;
                yield break;
            }
            for (var date = ChartBarProjector.NewYorkDate(start); date <= ChartBarProjector.NewYorkDate(end); date = date.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (date != Missing && date != ClosedDate && date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                {
                    var bar = BarAt(ChartBarProjector.At(date, TimeOnly.MinValue), "1d", 100m).Bar;
                    yield return date == Pivot ? bar with { High = 110m } : bar;
                }
            }
        }

        // Include closed days explicitly as required by the complete exchange calendar contract.
        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDateInclusive, DateOnly endDateInclusive, CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<DateOnly, MarketSessionSchedule> calendar = Enumerable
                .Range(startDateInclusive.DayNumber, endDateInclusive.DayNumber - startDateInclusive.DayNumber + 1)
                .Select(DateOnly.FromDayNumber).ToDictionary(date => date,
                    date => date == ClosedDate || date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                        ? MarketSessionSchedule.Closed(date)
                        : date == EarlyClose
                            ? new MarketSessionSchedule(date, true, new TimeOnly(9, 30), new TimeOnly(13, 0))
                            : MarketSessionSchedule.RegularDay(date));
            return Task.FromResult(calendar);
        }
    }
}

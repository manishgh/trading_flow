using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Regime;
using TradingFlow.Web;

namespace TradingFlow.Tests;

/// <summary>Focused checks for the workspace regime strip: VIX sizing labels, fail-closed regime rules, sector tail read and shell wiring.</summary>
public sealed class WorkspacePulseTests : IDisposable
{
    private static readonly DateOnly Session = new(2026, 11, 19);
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "tf-pulse-" + Guid.NewGuid().ToString("N"));

    // Builds a one-day VIX series for the previous session.
    private static VixCloseSeries Vix(decimal close) =>
        VixCloseSeries.From([new KeyValuePair<DateOnly, decimal>(Session, close)]);

    // Builds a consecutive UTC-midnight daily bar sequence ending on the requested session date.
    private static IReadOnlyList<OhlcvBar> BuildDailyBars(string symbol, int count, DateOnly lastDate, decimal startClose, decimal step)
    {
        var dates = new List<DateOnly>(count);
        for (var cursor = lastDate; dates.Count < count; cursor = cursor.AddDays(-1))
        {
            if (cursor.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            dates.Add(cursor);
        }

        dates.Reverse();
        return dates.Select((date, index) =>
        {
            var close = startClose + (index * step);
            return new OhlcvBar(symbol, new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), "1d",
                close - 0.5m, close + 0.5m, close - 1m, close, 1_000_000m);
        }).ToArray();
    }

    // Builds a weekday-only authoritative exchange schedule covering the requested date range.
    private static IReadOnlyDictionary<DateOnly, MarketSessionSchedule> BuildWeekdaySchedules(DateOnly start, DateOnly end)
    {
        var map = new Dictionary<DateOnly, MarketSessionSchedule>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            map[d] = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? MarketSessionSchedule.Closed(d)
                : MarketSessionSchedule.RegularDay(d);
        }

        return map;
    }

    [Theory]
    [InlineData(15.0, 1.0, 0.0)]
    [InlineData(20.0, 0.5, 0.5)]
    [InlineData(25.0, 0.0, 1.0)]
    public void VixPulseScalesLongAndShortRisk(double close, double longScale, double shortScale)
    {
        var pulse = WorkspacePulseEndpoints.BuildVixPulse(Vix((decimal)close), Session, Now);
        Assert.True(pulse.Available);
        Assert.Equal((decimal)longScale, pulse.LongRiskScale);
        Assert.Equal((decimal)shortScale, pulse.ShortRiskScale);
    }

    [Fact]
    public void MissingVixSeriesFailsClosedWithZeroScales()
    {
        var pulse = WorkspacePulseEndpoints.BuildVixPulse(null, Session, Now);
        Assert.False(pulse.Available);
        Assert.Equal(0m, pulse.LongRiskScale);
        Assert.Equal(0m, pulse.ShortRiskScale);
    }

    [Fact]
    public void TopSectorsReadsOnlyTheFinalSessionRows()
    {
        var folder = Path.Combine(root, "screens", "sector-funds-top3-test");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "eligible.csv"),
        [
            "session_date,ticker,rank",
            "2026-11-18,XLK,1", "2026-11-18,XLE,2", "2026-11-18,XLU,3",
            "2026-11-19,XLI,1", "2026-11-19,XLV,2", "2026-11-19,XLF,3"
        ]);
        var (sectors, asOf) = WorkspacePulseEndpoints.TryReadLatestTopSectors(Path.Combine(root, "screens"));
        Assert.Equal(["XLI", "XLV", "XLF"], sectors);
        Assert.Equal("2026-11-19", asOf);
    }

    [Fact]
    public void TopSectorsAreEmptyWhenNoScreenIsPublished()
    {
        var (sectors, asOf) = WorkspacePulseEndpoints.TryReadLatestTopSectors(Path.Combine(root, "screens"));
        Assert.Empty(sectors);
        Assert.Null(asOf);
    }

    [Fact]
    public void EvaluateBenchmarkMatchesUtcMidnightDailyCloseAndFailsClosedOnUncompletedStaleOrInsufficientBars()
    {
        var schedules = BuildWeekdaySchedules(Session.AddDays(-14), Session.AddDays(2));
        var risingBars = BuildDailyBars("SPY", 75, Session, startClose: 500m, step: 1m);
        var expectedClose = risingBars[^1].Close;

        var evaluated = WorkspacePulseEndpoints.EvaluateBenchmark("SPY", risingBars, schedules, Now);
        Assert.True(evaluated.Available);
        Assert.True(evaluated.LongGateOn);
        Assert.False(evaluated.ShortGateOn);
        Assert.Equal(Session, evaluated.LastSessionDate);
        Assert.Equal(Math.Round(expectedClose, 2), evaluated.LastClose);

        // Fewer than 70 completed bars (50-SMA + 20-bar slope) must fail closed, never show Neutral / Gate OFF.
        var shortHistory = BuildDailyBars("SPY", 60, Session, startClose: 500m, step: 1m);
        var insufficient = WorkspacePulseEndpoints.EvaluateBenchmark("SPY", shortHistory, schedules, Now);
        Assert.False(insufficient.Available);
        Assert.Null(insufficient.LastClose);

        // Only an uncompleted / future-known bar must fail closed instead of falling back to bars[^1].
        var uncompletedOnly = new[]
        {
            new OhlcvBar("SPY", new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), "1d", 580m, 585m, 579m, 584m, 100m)
        };
        var uncompleted = WorkspacePulseEndpoints.EvaluateBenchmark("SPY", uncompletedOnly, schedules, Now);
        Assert.False(uncompleted.Available);
        Assert.Null(uncompleted.LastClose);

        // Stale benchmark ending D-2 against authoritative schedule expecting D-1 must fail closed.
        var staleBars = BuildDailyBars("SPY", 75, Session.AddDays(-1), startClose: 500m, step: 1m);
        var stale = WorkspacePulseEndpoints.EvaluateBenchmark("SPY", staleBars, schedules, Now);
        Assert.False(stale.Available);
        Assert.False(stale.LongGateOn);
    }

    [Fact]
    public async Task TryLoadSchedulesFailsClosedWhenScheduleProviderReturnsEmptyOrThrows()
    {
        var emptyResult = await WorkspacePulseEndpoints.TryLoadSchedulesAsync(new FakeScheduleProvider(returnEmpty: true), Now, CancellationToken.None);
        Assert.False(emptyResult.Succeeded);
        Assert.Null(emptyResult.Schedules);

        var throwResult = await WorkspacePulseEndpoints.TryLoadSchedulesAsync(new FakeScheduleProvider(throwOnLoad: true), Now, CancellationToken.None);
        Assert.False(throwResult.Succeeded);
        Assert.Null(throwResult.Schedules);

        var calendarlessResult = await WorkspacePulseEndpoints.TryLoadSchedulesAsync(new FakeCalendarlessProvider(), Now, CancellationToken.None);
        Assert.True(calendarlessResult.Succeeded);
        Assert.Null(calendarlessResult.Schedules);
    }

    [Fact]
    public void VixPulseNeverWalksBackwardAndFailsClosedWithoutExactVerifiedSession()
    {
        var olderCloseOnly = VixCloseSeries.From([new KeyValuePair<DateOnly, decimal>(Session.AddDays(-3), 15m)]);
        var schedules = BuildWeekdaySchedules(Session.AddDays(-14), Session.AddDays(2));

        // Without benchmark session or schedules, never walk back to an older FRED close.
        var noSession = WorkspacePulseEndpoints.BuildVixPulse(olderCloseOnly, benchmarkPreviousSession: null, Now);
        Assert.False(noSession.Available);
        Assert.Equal(0m, noSession.LongRiskScale);
        Assert.Equal(0m, noSession.ShortRiskScale);

        // With authoritative schedules expecting 2026-11-19, missing 2026-11-19 close must fail closed rather than use 2026-11-16.
        var missingExpected = WorkspacePulseEndpoints.BuildVixPulse(olderCloseOnly, benchmarkPreviousSession: null, Now, scheduleSucceeded: true, schedules: schedules);
        Assert.False(missingExpected.Available);
        Assert.Equal(Session, missingExpected.SessionDate);
        Assert.Equal(0m, missingExpected.LongRiskScale);
        Assert.Equal(0m, missingExpected.ShortRiskScale);

        // When schedule lookup failed, fail closed even if a VIX close exists.
        var failedSchedule = WorkspacePulseEndpoints.BuildVixPulse(Vix(15m), Session, Now, scheduleSucceeded: false, schedules: null);
        Assert.False(failedSchedule.Available);
        Assert.Equal(0m, failedSchedule.LongRiskScale);
        Assert.Equal(0m, failedSchedule.ShortRiskScale);

        // With authoritative schedules and exact expected previous session published, evaluate normally.
        var exactExpected = WorkspacePulseEndpoints.BuildVixPulse(Vix(15m), benchmarkPreviousSession: null, Now, scheduleSucceeded: true, schedules: schedules);
        Assert.True(exactExpected.Available);
        Assert.Equal(Session, exactExpected.SessionDate);
        Assert.Equal(1.0m, exactExpected.LongRiskScale);
    }

    [Fact]
    public void ShellExposesFourWorkspacesRegimeStripAndAskButton()
    {
        var layout = File.ReadAllText(Path.Combine(TestRepository.FindRoot(), "src", "TradingFlow.Web", "Pages", "Shared", "_Layout.cshtml"));
        Assert.DoesNotContain('\uFFFD', layout);
        foreach (var expected in new[] { "\"Desk\"", "\"Strategy Lab\"", "\"Catalysts & Options\"", "\"Portfolio & Audit\"",
                     "\"/AccountLossReview\"", "loading&hellip;", "data-regime-pulse", "id=\"OpenCommandPalette\"", "ui2-foundation.css", "regime-pulse.js" })
        {
            Assert.Contains(expected, layout);
        }
    }

    private sealed class FakeCalendarlessProvider : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeScheduleProvider(bool returnEmpty = false, bool throwOnLoad = false)
        : IMarketDataProvider, IMarketSessionScheduleProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDateInclusive, DateOnly endDateInclusive, CancellationToken cancellationToken)
        {
            if (throwOnLoad) throw new InvalidOperationException("Simulated calendar outage.");
            IReadOnlyDictionary<DateOnly, MarketSessionSchedule> result = returnEmpty
                ? new Dictionary<DateOnly, MarketSessionSchedule>()
                : BuildWeekdaySchedules(startDateInclusive, endDateInclusive);
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

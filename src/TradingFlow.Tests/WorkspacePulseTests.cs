using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Regime;
using TradingFlow.Web;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

/// <summary>Focused checks for the workspace regime strip: VIX sizing labels, fail-closed regime rules, live sector ranking and shell wiring.</summary>
public sealed class WorkspacePulseTests : IDisposable
{
    private static readonly DateOnly Session = new(2026, 11, 19);
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
    // The regime rules the ten swing configs use today (50-day average with a 20-session slope; VIX 18 / 22 with half
    // size between), resolved through the same selection the strip uses.
    private static readonly WorkspaceRegimeRules Rules = WorkspacePulseService.SelectRules(
    [
        new RegimeRules("SPY", RegimeRules.PriceAboveSma, 50, SmaRisingLookbackBars: 20, NasdaqListingBenchmark: "QQQ", VixSeries: "VIXCLS",
            VixFullRiskBelow: 18m, VixNoLongsAbove: 22m, VixReducedRiskScale: 0.5m),
        new RegimeRules("SPY", RegimeRules.PriceBelowFallingSma, 50, VixSeries: "VIXCLS", VixReducedRiskScale: 0.5m,
            SmaFallingLookbackBars: 20, VixNoShortsBelow: 18m, VixShortFullRiskAbove: 22m)
    ]);

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
        var pulse = WorkspacePulseService.BuildVixPulse(Vix((decimal)close), Session, Now, scheduleSucceeded: true, schedules: null, rules: Rules);
        Assert.True(pulse.Available);
        Assert.Equal((decimal)longScale, pulse.LongRiskScale);
        Assert.Equal((decimal)shortScale, pulse.ShortRiskScale);
    }

    [Fact]
    public void MissingVixSeriesFailsClosedWithZeroScales()
    {
        var pulse = WorkspacePulseService.BuildVixPulse(null, Session, Now, scheduleSucceeded: true, schedules: null, rules: Rules);
        Assert.False(pulse.Available);
        Assert.Equal(0m, pulse.LongRiskScale);
        Assert.Equal(0m, pulse.ShortRiskScale);
    }

    [Fact]
    public void RegimeRulesComeFromTheMostCommonConfiguredRuleAndSayWhenStrategiesDiffer()
    {
        var common = new RegimeRules("SPY", RegimeRules.PriceAboveSma, 50, SmaRisingLookbackBars: 20);
        var other = new RegimeRules("SPY", RegimeRules.PriceAboveSma, 100, SmaRisingLookbackBars: 10);
        var rules = WorkspacePulseService.SelectRules([common, common with { NasdaqListingBenchmark = "QQQ" }, other]);
        // The Nasdaq home-market link applies per stock, so the first two count as one rule (2 of 3).
        Assert.Equal(50, rules.Long!.SmaPeriod);
        Assert.Contains("2 of 3", rules.LongSource);
        Assert.Null(rules.Short);
        Assert.Contains("No enabled short strategy", rules.ShortSource);

        var none = WorkspacePulseService.SelectRules([]);
        var unavailable = WorkspacePulseService.EvaluateBenchmark("SPY", [], null, Now, none);
        Assert.False(unavailable.Available);
        Assert.Equal("No strategy uses a regime rule", unavailable.SummaryLabel);
    }

    [Fact]
    public void SectorLeadersRankBy12Minus1ReturnKeepTopSixAndNameEachSector()
    {
        // Steps give each fund a different 12-1 return; a higher step means a higher return from the same start.
        var steps = new Dictionary<string, decimal>
        {
            ["XLK"] = 0.60m, ["XLF"] = 0.50m, ["XLI"] = 0.40m, ["XLV"] = 0.30m, ["XLY"] = 0.20m,
            ["XLC"] = 0.10m, ["XLE"] = -0.10m, ["XLU"] = -0.20m
        };
        var bars = steps.ToDictionary(pair => pair.Key,
            pair => BuildDailyBars(pair.Key, 260, Session, 100m, pair.Value).ToList(), StringComparer.OrdinalIgnoreCase);
        bars["XLB"] = BuildDailyBars("XLB", 100, Session, 100m, 5m).ToList(); // too little history: left out, not ranked
        var funds = steps.Keys.Append("XLB").ToArray();

        var ranking = TradingFlow.Web.Services.SectorLeadersService.Rank(funds, bars, schedules: null, Now);

        Assert.Null(ranking.UnavailableReason);
        Assert.Equal(Session, ranking.AsOfSession);
        Assert.Equal(["XLK", "XLF", "XLI", "XLV", "XLY", "XLC"], ranking.Leaders.Select(leader => leader.Symbol));
        Assert.Equal([1, 2, 3, 4, 5, 6], ranking.Leaders.Select(leader => leader.Rank));
        Assert.Equal("Technology", ranking.Leaders[0].Name);
        Assert.Equal("Communication services", ranking.Leaders[5].Name);
        // 12-1 return for the leader (shared research-screen formula): the close 22 sessions before the latest completed
        // session divided by the close 253 sessions before it, minus one.
        var xlk = bars["XLK"];
        Assert.Equal(Math.Round(xlk[^23].Close / xlk[^254].Close - 1m, 4), ranking.Leaders[0].Return12Minus1);
    }

    [Fact]
    public void SectorLeadersAreUnavailableWithAReasonWhenNoFundHasAFullYear()
    {
        var bars = new Dictionary<string, List<OhlcvBar>>(StringComparer.OrdinalIgnoreCase)
        {
            ["XLK"] = BuildDailyBars("XLK", 50, Session, 100m, 1m).ToList()
        };
        var ranking = TradingFlow.Web.Services.SectorLeadersService.Rank(["XLK"], bars, schedules: null, Now);
        Assert.Empty(ranking.Leaders);
        Assert.Null(ranking.AsOfSession);
        Assert.Contains("full year", ranking.UnavailableReason);
    }

    [Fact]
    public void EvaluateBenchmarkMatchesUtcMidnightDailyCloseAndFailsClosedOnUncompletedStaleOrInsufficientBars()
    {
        var schedules = BuildWeekdaySchedules(Session.AddDays(-14), Session.AddDays(2));
        var risingBars = BuildDailyBars("SPY", 75, Session, startClose: 500m, step: 1m);
        var expectedClose = risingBars[^1].Close;

        var evaluated = WorkspacePulseService.EvaluateBenchmark("SPY", risingBars, schedules, Now, Rules);
        Assert.True(evaluated.Available);
        Assert.True(evaluated.LongGateOn);
        Assert.False(evaluated.ShortGateOn);
        Assert.Equal(Session, evaluated.LastSessionDate);
        Assert.Equal(Math.Round(expectedClose, 2), evaluated.LastClose);

        // Fewer than 70 completed bars (50-SMA + 20-bar slope) must fail closed, never show Neutral / Gate OFF.
        var shortHistory = BuildDailyBars("SPY", 60, Session, startClose: 500m, step: 1m);
        var insufficient = WorkspacePulseService.EvaluateBenchmark("SPY", shortHistory, schedules, Now, Rules);
        Assert.False(insufficient.Available);
        Assert.Null(insufficient.LastClose);

        // Only an uncompleted / future-known bar must fail closed instead of falling back to bars[^1].
        var uncompletedOnly = new[]
        {
            new OhlcvBar("SPY", new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero), "1d", 580m, 585m, 579m, 584m, 100m)
        };
        var uncompleted = WorkspacePulseService.EvaluateBenchmark("SPY", uncompletedOnly, schedules, Now, Rules);
        Assert.False(uncompleted.Available);
        Assert.Null(uncompleted.LastClose);

        // Stale benchmark ending D-2 against authoritative schedule expecting D-1 must fail closed.
        var staleBars = BuildDailyBars("SPY", 75, Session.AddDays(-1), startClose: 500m, step: 1m);
        var stale = WorkspacePulseService.EvaluateBenchmark("SPY", staleBars, schedules, Now, Rules);
        Assert.False(stale.Available);
        Assert.False(stale.LongGateOn);
    }

    [Fact]
    public async Task TryLoadSchedulesFailsClosedWhenScheduleProviderReturnsEmptyOrThrows()
    {
        var emptyResult = await DeskMarketData.TryLoadSchedulesAsync(new FakeScheduleProvider(returnEmpty: true), Now, 15, CancellationToken.None);
        Assert.False(emptyResult.Succeeded);
        Assert.Null(emptyResult.Schedules);

        var throwResult = await DeskMarketData.TryLoadSchedulesAsync(new FakeScheduleProvider(throwOnLoad: true), Now, 15, CancellationToken.None);
        Assert.False(throwResult.Succeeded);
        Assert.Null(throwResult.Schedules);

        var calendarlessResult = await DeskMarketData.TryLoadSchedulesAsync(new FakeCalendarlessProvider(), Now, 15, CancellationToken.None);
        Assert.True(calendarlessResult.Succeeded);
        Assert.Null(calendarlessResult.Schedules);
    }

    [Fact]
    public async Task CachedScheduleLoadCallsTheProviderOncePerNewYorkDayAndNeverCachesAFailure()
    {
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var provider = new FakeScheduleProvider();
        var first = await DeskMarketData.TryLoadSchedulesCachedAsync(provider, Now, 15, cache, CancellationToken.None);
        var second = await DeskMarketData.TryLoadSchedulesCachedAsync(provider, Now.AddMinutes(5), 15, cache, CancellationToken.None);
        Assert.True(first.Succeeded);
        Assert.Same(first.Schedules, second.Schedules);
        Assert.Equal(1, provider.Loads);

        var failing = new FakeScheduleProvider(throwOnLoad: true);
        Assert.False((await DeskMarketData.TryLoadSchedulesCachedAsync(failing, Now.AddDays(1), 15, cache, CancellationToken.None)).Succeeded);
        Assert.False((await DeskMarketData.TryLoadSchedulesCachedAsync(failing, Now.AddDays(1), 15, cache, CancellationToken.None)).Succeeded);
        Assert.Equal(2, failing.Loads);

        // Requesting a 65-day forward calendar uses its own cache key so forward expiries are covered without colliding with the 1-day strip calendar.
        var forward = await DeskMarketData.TryLoadSchedulesCachedAsync(provider, Now, 65, cache, CancellationToken.None, daysForward: 65);
        Assert.True(forward.Succeeded);
        Assert.NotNull(forward.Schedules);
        Assert.True(forward.Schedules!.ContainsKey(DateOnly.FromDateTime(Now.UtcDateTime).AddDays(60)));
        Assert.Equal(2, provider.Loads);
        var forwardCached = await DeskMarketData.TryLoadSchedulesCachedAsync(provider, Now.AddMinutes(5), 65, cache, CancellationToken.None, daysForward: 65);
        Assert.Same(forward.Schedules, forwardCached.Schedules);
        Assert.Equal(2, provider.Loads);
    }

    [Fact]
    public async Task SingleFlightJoinsConcurrentCallersAndRestartsAfterAFailure()
    {
        var flight = new SingleFlight<string, int>(StringComparer.Ordinal);
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var first = flight.RunAsync("k", () => { Interlocked.Increment(ref starts); return gate.Task; });
        var joined = flight.RunAsync("k", () => { Interlocked.Increment(ref starts); return Task.FromResult(-1); });
        Assert.Same(first, joined);
        gate.SetResult(7);
        Assert.Equal(7, await joined);
        Assert.Equal(1, starts);
        await WaitUntilAsync(() => flight.RunningCount == 0);

        var failed = flight.RunAsync("k", () => Task.FromException<int>(new InvalidOperationException("boom")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        await WaitUntilAsync(() => flight.RunningCount == 0);
        Assert.Equal(3, await flight.RunAsync("k", () => Task.FromResult(3)));
    }

    // Polls a condition for up to two seconds; the helper removes finished entries on a continuation.
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public void VixPulseNeverWalksBackwardAndFailsClosedWithoutExactVerifiedSession()
    {
        var olderCloseOnly = VixCloseSeries.From([new KeyValuePair<DateOnly, decimal>(Session.AddDays(-3), 15m)]);
        var schedules = BuildWeekdaySchedules(Session.AddDays(-14), Session.AddDays(2));

        // Without benchmark session or schedules, never walk back to an older FRED close.
        var noSession = WorkspacePulseService.BuildVixPulse(olderCloseOnly, benchmarkPreviousSession: null, Now, scheduleSucceeded: true, schedules: null, rules: Rules);
        Assert.False(noSession.Available);
        Assert.Equal(0m, noSession.LongRiskScale);
        Assert.Equal(0m, noSession.ShortRiskScale);

        // With authoritative schedules expecting 2026-11-19, missing 2026-11-19 close must fail closed rather than use 2026-11-16.
        var missingExpected = WorkspacePulseService.BuildVixPulse(olderCloseOnly, benchmarkPreviousSession: null, Now, scheduleSucceeded: true, schedules: schedules, rules: Rules);
        Assert.False(missingExpected.Available);
        Assert.Equal(Session, missingExpected.SessionDate);
        Assert.Equal(0m, missingExpected.LongRiskScale);
        Assert.Equal(0m, missingExpected.ShortRiskScale);
        // The newest published close is reported for display only; it never becomes the sizing close.
        Assert.Null(missingExpected.Close);
        Assert.Equal(15m, missingExpected.LatestPublishedClose);
        Assert.Equal(Session.AddDays(-3), missingExpected.LatestPublishedDate);

        // When schedule lookup failed, fail closed even if a VIX close exists.
        var failedSchedule = WorkspacePulseService.BuildVixPulse(Vix(15m), Session, Now, scheduleSucceeded: false, schedules: null, rules: Rules);
        Assert.False(failedSchedule.Available);
        Assert.Equal(0m, failedSchedule.LongRiskScale);
        Assert.Equal(0m, failedSchedule.ShortRiskScale);

        // With authoritative schedules and exact expected previous session published, evaluate normally.
        var exactExpected = WorkspacePulseService.BuildVixPulse(Vix(15m), benchmarkPreviousSession: null, Now, scheduleSucceeded: true, schedules: schedules, rules: Rules);
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
                     "\"/AccountLossReview\"", "loading&hellip;", "data-regime-pulse", "id=\"OpenCommandPalette\"", "tokens.css", "components.css", "workspaces.css", "regime-pulse.js" })
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

        public int Loads { get; private set; }

        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDateInclusive, DateOnly endDateInclusive, CancellationToken cancellationToken)
        {
            Loads++;
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

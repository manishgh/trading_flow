using System.Runtime.CompilerServices;
using TradingFlow.Backtesting;
using TradingFlow.Cli;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Strategies;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Tests;

/// <summary>
/// The month-end decision schedule, the 210-session average distance and the entry-factor exit used by etf.trend-10m@1.0.0
/// (design docs/design-review/2026-11-13/etf-trend-core-design.md, option B), and the catalog strategy run end to end.
/// Synthetic fixture: weekday sessions from 2 June 2025 to 30 October 2026, as-traded daily bars at New York midnight.
/// UPP rises 0.05 a session throughout. DIP rises the same way, closes 10 below its trend from 10 to 14 August 2026 and
/// recovers by the month end, then drops 20 below its trend from 15 September 2026 and stays there. LATE falls 0.05 a
/// session until 9 July 2026, then jumps to 100 and rises. The test window is July to October 2026.
/// </summary>
public sealed class EtfTrendMonthEndTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly string[] Pool = ["DIP", "LATE", "UPP"];

    // A session is a month-end decision session only when the calendar's next trading session falls in another month,
    // including across a year end and a holiday on the first of the month.
    [Theory]
    [InlineData("2026-07-30", "2026-07-31", false)]
    [InlineData("2026-07-31", "2026-08-03", true)]
    [InlineData("2026-12-31", "2027-01-04", true)]
    [InlineData("2026-12-30", "2026-12-31", false)]
    public void MonthEnd_HoldsOnlyWhenTheNextSessionIsInAnotherMonth(string session, string next, bool expected)
    {
        Assert.Equal(expected, DecisionSchedules.IsDecisionSession(DecisionSchedules.MonthEnd, DateOnly.Parse(session), DateOnly.Parse(next)));
        Assert.True(DecisionSchedules.IsDecisionSession(null, DateOnly.Parse(session), DateOnly.Parse(next)));
    }

    // An unknown schedule name, or a "next" session that is not later, is refused instead of deciding every day.
    [Fact]
    public void Schedule_RejectsUnknownNamesAndANonLaterNextSession()
    {
        Assert.Throws<InvalidOperationException>(() => DecisionSchedules.Validate("monthly", "entry_rules.decision_schedule"));
        Assert.Throws<InvalidOperationException>(() =>
            DecisionSchedules.IsDecisionSession(DecisionSchedules.MonthEnd, new DateOnly(2026, 7, 31), new DateOnly(2026, 7, 31)));
    }

    // The next session skips weekends and holidays; a date missing from the calendar is missing evidence, not a month end.
    [Fact]
    public void NextTradingSession_SkipsClosedDays_AndRefusesACalendarGap()
    {
        var calendar = Calendar(new DateOnly(2026, 6, 25), new DateOnly(2026, 7, 10));
        calendar[new DateOnly(2026, 7, 3)] = MarketSessionSchedule.Closed(new DateOnly(2026, 7, 3));
        Assert.Equal(new DateOnly(2026, 7, 6), ExchangeSessionCalendar.NextTradingSession(calendar, new DateOnly(2026, 7, 2)));
        calendar.Remove(new DateOnly(2026, 7, 7));
        Assert.Throws<InvalidDataException>(() => ExchangeSessionCalendar.NextTradingSession(calendar, new DateOnly(2026, 7, 6)));
    }

    // The distance is the close's percent above the average of the last N closes, unavailable with fewer than N closes.
    [Fact]
    public void SmaDistance_IsPercentAboveTheAverageOfTheLastNCloses()
    {
        var bars = Enumerable.Range(0, 5).Select(i => Bar("X", new DateOnly(2026, 7, 6).AddDays(i), 10m + i)).ToArray();
        var snapshots = new IndicatorEngine().Compute(bars).ToArray();
        var rules = new TechnicalFactorRules(TechnicalFactorRules.SmaDistance, 4, MinValue: 0);
        // Last four closes 11, 12, 13, 14: average 12.5, close 14 is 12% above it.
        Assert.Equal(12m, TechnicalFactorCalculator.Analyze(rules, bars, snapshots, 4, null, bars[4].Timestamp.AddDays(1)).Value);
        var early = TechnicalFactorCalculator.Analyze(rules, bars, snapshots, 2, null, bars[2].Timestamp.AddDays(1));
        Assert.Equal((null, "sma_history_unavailable"), (early.Value, early.UnavailableReason));
        Assert.Equal(4, rules.RequiredCompletedBars);
    }

    // The exit fires at or below the threshold only on a scheduled session; a missing value keeps the position.
    [Fact]
    public void EntryFactorExit_FiresAtOrBelowTheThresholdOnlyOnScheduledSessions()
    {
        var strategy = TrendStrategy().Definition;
        var bars = DailyBars().Where(bar => bar.Ticker == "DIP").ToArray();
        var snapshots = new IndicatorEngine().Compute(bars).ToArray();
        var below = Array.FindIndex(bars, bar => Session(bar) == new DateOnly(2026, 9, 30));
        var midMonthBelow = Array.FindIndex(bars, bar => Session(bar) == new DateOnly(2026, 8, 12));
        Assert.Equal(TechnicalExecutionEngine.EntryFactorExitReason,
            TechnicalExecutionEngine.GetEntryFactorExitReason(strategy, bars, snapshots, below, isScheduledSession: true));
        Assert.Null(TechnicalExecutionEngine.GetEntryFactorExitReason(strategy, bars, snapshots, below, isScheduledSession: false));
        Assert.Equal(TechnicalExecutionEngine.EntryFactorExitReason,
            TechnicalExecutionEngine.GetEntryFactorExitReason(strategy, bars, snapshots, midMonthBelow, isScheduledSession: true));
        Assert.Null(TechnicalExecutionEngine.GetEntryFactorExitReason(strategy, bars, snapshots, 100, isScheduledSession: true));
    }

    // A factor exit without an entry factor, or a schedule on a non-daily rule, is refused when the file is read.
    [Theory]
    [InlineData("    min_value: 0\n", "    min_value: 0\n  decision_schedule: monthly\n")]
    [InlineData("  entry_factor_exit_schedule: month_end\n", "  entry_factor_exit_schedule: weekly\n")]
    [InlineData("  exit_on_entry_factor_at_or_below: 0\n", "")]
    public void StrategyFile_RejectsInvalidScheduledRules(string find, string replace)
    {
        var text = File.ReadAllText(TrendFile()).Replace("\r\n", "\n");
        Assert.Contains(find, text);
        var path = Path.Combine(Path.GetTempPath(), "etf-trend-invalid-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, text.Replace(find, replace));
            Assert.Throws<InvalidOperationException>(() => new SimpleYamlReader().ReadStrategy(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The 210-session average is part of the as-traded look-back, so a list that leaves out only 200 sessions after a split
    // is refused; paper/live entry and exit paths and the signal study refuse both ETF versions before any order work.
    [Fact]
    public void BacktestOnlyRules_AreCoveredByTheSplitCheckAndRefusedOutsideTheDailyReplay()
    {
        var trend = TrendStrategy().Definition;
        var control = Strategy("etf-buy-and-hold-control.bt-v1.yaml").Definition;
        Assert.Equal(210, BacktestRunner.LongestIndicatorLookback(trend));
        foreach (var strategy in new[] { trend, control })
        {
            Assert.Throws<NotSupportedException>(() => TechnicalExecutionEngine.RequireNoBacktestOnlyRules(strategy));
            Assert.Throws<NotSupportedException>(() => TradingFlow.Research.Signals.SignalStudyOutcomes.RequireSupported(strategy));
        }
        TechnicalExecutionEngine.RequireNoBacktestOnlyRules(trend with
        {
            EntryRules = trend.EntryRules with { DecisionSchedule = null },
            ExitRules = trend.ExitRules with { ExitOnEntryFactorAtOrBelow = null, EntryFactorExitSchedule = null }
        });
    }

    // etf.trend-10m@1.0.0 through the ordinary runner: UPP and DIP are bought at the 1 July open (decided at the 30 June
    // close); DIP is not sold during its mid-August dip because it recovered by the month end, and is sold at the 1 October
    // open after closing below its average on 30 September; LATE rose above its average on 10 July but is bought only at the
    // 3 August open, after the 31 July month-end decision. 31 July 2026 is a Friday: the decision is judged on Monday's
    // eligible list, the session it fills in (before 13 November 2026 a Friday close was checked against Saturday and refused).
    [Fact]
    public async Task TrendRule_EntersAndExitsOnlyAtMonthEnds()
    {
        var result = await RunAsync(TrendStrategy());

        Assert.True(result.EconomicResultsComplete);
        var dip = Assert.Single(result.CompletedTrades);
        Assert.Equal(("DIP", TechnicalExecutionEngine.EntryFactorExitReason), (dip.Ticker, dip.ExitReason));
        var exitFill = Assert.Single(result.PortfolioState!.Fills, fill => !fill.IsEntry);
        Assert.Equal(("DIP", new DateOnly(2026, 10, 1)), (exitFill.Ticker, exitFill.SourceSessionDate));
        Assert.Equal(["LATE", "UPP"], result.PortfolioState!.OpenPositions.Select(position => position.Ticker).Order());
        Assert.Equal(new Dictionary<string, DateOnly> { ["DIP"] = new(2026, 7, 1), ["LATE"] = new(2026, 8, 3), ["UPP"] = new(2026, 7, 1) },
            EntrySessions(result));
    }

    // The buy-and-hold control buys every fund at the first month-end decision and never sells.
    [Fact]
    public async Task BuyAndHoldControl_BuysEveryFundAtTheFirstMonthEndAndNeverSells()
    {
        var result = await RunAsync(Strategy("etf-buy-and-hold-control.bt-v1.yaml"));

        Assert.True(result.EconomicResultsComplete);
        Assert.Empty(result.CompletedTrades);
        Assert.Equal(Pool, result.PortfolioState!.OpenPositions.Select(position => position.Ticker).Order());
        Assert.Equal(Pool.ToDictionary(ticker => ticker, _ => new DateOnly(2026, 7, 1)), EntrySessions(result));
    }

    // The session of each fund's single entry fill; a fund bought twice would fail the dictionary build.
    private static Dictionary<string, DateOnly> EntrySessions(BacktestResult result) =>
        result.PortfolioState!.Fills.Where(fill => fill.IsEntry).ToDictionary(fill => fill.Ticker, fill => fill.SourceSessionDate);

    // Run one catalog strategy with the ETF run profile on the fixture's eligible list for July to October 2026.
    private static async Task<BacktestResult> RunAsync(AuthorizedRuntimeStrategy validated)
    {
        using var root = new TemporaryFolder();
        var reader = new SimpleYamlReader();
        var catalog = Catalog(reader);
        var runtime = StrategyDecisionRequestAssembler.CreateBacktestRuntimeStrategy(validated.Definition, catalog);
        var profile = reader.ReadBacktestRun(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "etf-trend-profile.yaml"));
        var screen = await PublishEligibleScreenAsync(Path.Combine(root.Path, "screen"));
        var start = Midnight(new(2026, 7, 1));
        var run = profile with
        {
            RunName = "synthetic-etf-trend", Provider = "synthetic-etf-trend", Tickers = Pool, Intervals = ["1d"],
            RawRoot = Path.Combine(root.Path, "raw"), NormalizedRoot = Path.Combine(root.Path, "normalized"), ResultsRoot = Path.Combine(root.Path, "results"),
            TimeWindow = profile.TimeWindow with { Type = "fixed", Start = start, End = Midnight(new(2026, 10, 31)) },
            Universe = UniverseConfig.StaticUniverse with { Mode = UniverseConfig.ResolvedSnapshotMode, ResolvedSource = "research_screen",
                SelectedSourceTickers = Pool, ResolvedAtUtc = start, RankedMembership = new(screen.ManifestPath, screen.ManifestSha256) }
        };
        var runner = new BacktestRunner(reader, catalog, marketDataProviderFactory: _ => new FixtureProvider());
        return await runner.RunValidatedAsync(run, [runtime], DateTimeOffset.UtcNow, Path.Combine(run.ResultsRoot, "result.json"), default);
    }

    // An eligible list naming every fund on every session from 1 June 2026 (prior-close price rule, no splits).
    private static async Task<PublishedResearchScreen> PublishEligibleScreenAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var sessions = Sessions().Where(day => day >= new DateOnly(2026, 6, 1)).ToArray();
        await File.WriteAllLinesAsync(Path.Combine(folder, "eligible.csv"),
            new[] { "session_date,ticker,price,average_volume,average_dollar_volume,atr,history_sessions" }.Concat(sessions.SelectMany(day =>
                Pool.Select(ticker => $"{day:yyyy-MM-dd},{ticker},60,2000000,120000000,1,300"))));
        await File.WriteAllLinesAsync(Path.Combine(folder, DailyScreenCommand.SplitExclusionsFileName), ["session_date,ticker,reason"]);
        await ResearchScreenManifest.PublishAsync(folder, new("daily", "actions", null, "sessions"),
            SyntheticScreen.Rules with { PriceBasis = DailyScreenPriceBasis.PriorClose }, null, sessions[0], sessions[^1],
            "alpaca_security_id", null, default, new ResearchScreenSplitRule(210, 0));
        return PublishedResearchScreen.Read(folder);
    }

    private static StrategyArtifactCatalog Catalog(SimpleYamlReader reader)
    {
        var root = TestRepository.FindRoot();
        return new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), reader);
    }

    private static string TrendFile() =>
        Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies", "etf-trend-10m.bt-v1.yaml");

    private static AuthorizedRuntimeStrategy TrendStrategy() => Strategy("etf-trend-10m.bt-v1.yaml");

    // Read and validate a catalog strategy file for a backtest.
    private static AuthorizedRuntimeStrategy Strategy(string file)
    {
        var reader = new SimpleYamlReader();
        return new StrategyRunArtifactValidator(reader, Catalog(reader)).ReadAndValidateRuntimeArtifact(
            Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "strategies", file), StrategySelectionMode.Backtest);
    }

    // Weekday sessions of the fixture (no holidays).
    private static DateOnly[] Sessions() => Enumerable.Range(new DateOnly(2025, 6, 2).DayNumber,
            new DateOnly(2026, 10, 30).DayNumber - new DateOnly(2025, 6, 2).DayNumber + 1).Select(DateOnly.FromDayNumber)
        .Where(day => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToArray();

    // Weekdays open, weekends closed, over [start, end].
    private static Dictionary<DateOnly, MarketSessionSchedule> Calendar(DateOnly start, DateOnly end) =>
        Enumerable.Range(start.DayNumber, end.DayNumber - start.DayNumber + 1).Select(DateOnly.FromDayNumber).ToDictionary(day => day,
            day => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? MarketSessionSchedule.Closed(day) : MarketSessionSchedule.RegularDay(day));

    // New York midnight of a date in UTC, the opening stamp of that date's daily bar.
    private static DateTimeOffset Midnight(DateOnly day) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), NewYork), TimeSpan.Zero);

    private static DateOnly Session(OhlcvBar bar) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bar.Timestamp, NewYork).DateTime);

    // The three funds' as-traded daily bars described in the class summary.
    private static IEnumerable<OhlcvBar> DailyBars()
    {
        var sessions = Sessions();
        var jump = new DateOnly(2026, 7, 10);
        foreach (var (day, i) in sessions.Select((day, i) => (day, i)))
        {
            var trend = 50m + 0.05m * i;
            yield return Bar("UPP", day, trend);
            var dipped = day >= new DateOnly(2026, 8, 10) && day <= new DateOnly(2026, 8, 14) ? trend - 10m
                : day >= new DateOnly(2026, 9, 15) ? trend - 20m : trend;
            yield return Bar("DIP", day, dipped);
            yield return Bar("LATE", day, day < jump ? 70m - 0.05m * i : 100m + 0.05m * i);
        }
    }

    // One as-traded daily bar opening at the close (no gap), with a one-point range and two million shares.
    private static OhlcvBar Bar(string ticker, DateOnly day, decimal close) =>
        new(ticker, Midnight(day), "1d", close, close + 0.5m, close - 0.5m, close, 2_000_000m, "sip", "raw");

    /// <summary>Serves the fixture's daily bars and its weekday calendar; no network and no real data.</summary>
    private sealed class FixtureProvider : IMarketDataProvider, IMarketSessionScheduleProvider, IShareSplitProvider
    {
        private readonly OhlcvBar[] bars = DailyBars().ToArray();

        // The fixture has no splits.
        public Task<IReadOnlyDictionary<string, IReadOnlyList<ShareSplit>>> LoadShareSplitsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ShareSplit>>>(new Dictionary<string, IReadOnlyList<ShareSplit>>());

        // The fixture's bars of the requested funds and timeframes inside [start, end).
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var bar in bars.Where(bar => tickers.Contains(bar.Ticker) && timeframes.Contains(bar.Timeframe) &&
                         bar.Timestamp >= start && bar.Timestamp < end))
                yield return bar;
        }

        // Weekdays are regular sessions and weekends are closed; the fixture declares no holidays.
        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(DateOnly start, DateOnly end,
            CancellationToken token) => Task.FromResult<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>(Calendar(start, end));
    }
}

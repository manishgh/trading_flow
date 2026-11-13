using System.Runtime.CompilerServices;
using TradingFlow.Backtesting;
using TradingFlow.Cli;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Tests;

/// <summary>
/// The ETF sector rotation (design docs/design-review/2026-11-13/etf-rotation-design.md): the 12-1 return ranking of the
/// research list, admission capacity with same-open swaps and marked-equity sizing, and etf.rotation-12-1@1.0.0 end to end.
/// Synthetic fixture: weekday sessions from 2 June 2025 to 30 October 2026; AAA, BBB, CCC and DDD all rise 0.05 a session (so
/// all stay above their 210-session average). The list names AAA, BBB and CCC until 31 July 2026 and AAA, BBB and DDD from
/// 3 August 2026, as a ranking would after the 31 July close.
/// </summary>
public sealed class EtfRotationTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly string[] Pool = ["AAA", "BBB", "CCC", "DDD"];
    private static readonly DateOnly Swap = new(2026, 8, 3);

    // The 12-1 return reads the close 21 sessions before the latest prior close over the close 252 sessions before it, and
    // needs 253 earlier sessions; the session's own bar is never read.
    [Fact]
    public void Return12Minus1_UsesOnlyEarlierClosesAndNeedsTheFullLookback()
    {
        var bars = Sessions().Take(300).Select((day, i) => Bar("X", day, 10m + i)).ToArray();
        // At index 260: prior closes end at index 259; 21 before it is index 238 (close 248), 252 before is index 7 (close 17).
        Assert.Equal(248m / 17m - 1m, DailyScreenCommand.Return12Minus1(bars, [], 260));
        Assert.Null(DailyScreenCommand.Return12Minus1(bars, [], 252));
        var changedToday = bars.Select((bar, i) => i == 260 ? bar with { Close = 1m, Open = 1m } : bar).ToArray();
        Assert.Equal(DailyScreenCommand.Return12Minus1(bars, [], 260), DailyScreenCommand.Return12Minus1(changedToday, [], 260));
    }

    // A momentum tier keeps the highest returns per session (ties by symbol) and leaves out rows that cannot be ranked.
    [Fact]
    public async Task MomentumTier_KeepsTheTopRanksAndSkipsRowsWithoutAReturn()
    {
        using var folder = new TemporaryFolder();
        var path = Path.Combine(folder.Path, "eligible.csv");
        await File.WriteAllLinesAsync(path, [
            "session_date,ticker,price,average_volume,average_dollar_volume,atr,history_sessions,return_12_1",
            "2026-07-01,AAA,10,1,1,,300,0.10", "2026-07-01,BBB,10,1,1,,300,0.30", "2026-07-01,CCC,10,1,1,,300,0.30",
            "2026-07-01,DDD,10,1,1,,300,", "2026-07-02,AAA,10,1,1,,300,-0.20", "2026-07-02,BBB,10,1,1,,300,0.05"]);
        var counts = await DailyScreenCommand.ApplyLiquidityTierAsync(path,
            new ResearchScreenLiquidityTier(1, 2, ResearchScreenLiquidityTier.Return12Minus1), default);

        var kept = File.ReadAllLines(path).Skip(1).Select(line => string.Join(':', line.Split(',')[..2])).ToArray();
        Assert.Equal(["2026-07-01:BBB", "2026-07-01:CCC", "2026-07-02:AAA", "2026-07-02:BBB"], kept);
        Assert.Equal(2, counts[new DateOnly(2026, 7, 1)]);
    }

    // With both settings off capacity is the long-standing one; a same-open sale frees its slot and marked value; marked
    // equity adds held positions' gains at their last close.
    [Fact]
    public void AdmissionCapacity_IsUnchangedWhenOffAndCountsSwapsAndMarksWhenOn()
    {
        var open = new DateTimeOffset(2026, 8, 3, 4, 0, 0, TimeSpan.Zero);
        ActiveBacktestExecution Held(string ticker, DateTimeOffset? exit, string? reason) =>
            new(ticker, "s", "s", open.AddDays(-30), 100, 10_000m) { PlannedExitTimestamp = exit, PlannedExitReason = reason, LastClose = 110m };
        var held = new[] { Held("A", null, null), Held("B", open, "technical_exit_off_list"), Held("C", open, "stop_loss") };
        var off = new PortfolioConfig(100_000m, 1m, 32.5m, 3, 0m, 0m, 1, true);

        Assert.Equal(new AdmissionCapacity(3, 100_000m / 3, 70_000m, 100_000m),
            BacktestRunner.MeasureAdmissionCapacity(off, 100_000m, held, open));
        var on = off with { SameOpenExitsReleaseCapacity = true, SizeFromMarkedEquity = true };
        // B (an open-fill sale) is released; C (a stop inside the bar) is not. Marked equity adds 3 x 100 x (110 - 100).
        Assert.Equal(new AdmissionCapacity(2, 103_000m / 3, 81_000m, 103_000m),
            BacktestRunner.MeasureAdmissionCapacity(on, 100_000m, held, open));
    }

    // etf.rotation-12-1@1.0.0 with its profile: AAA, BBB and CCC are bought at the 1 July open; CCC leaves the list for 3
    // August, so it is sold at the 3 August open and DDD is bought at that same open with the freed slot, sized from marked
    // equity (above the 32,500 a slot started at).
    [Fact]
    public async Task Rotation_SellsAFundThatLeftTheListAndBuysItsReplacementAtTheSameOpen()
    {
        var result = await RunAsync(portfolio => portfolio);

        Assert.True(result.EconomicResultsComplete);
        var sold = Assert.Single(result.CompletedTrades);
        Assert.Equal(("CCC", BacktestRunner.OffListExitReason), (sold.Ticker, sold.ExitReason));
        Assert.Equal(new Dictionary<string, DateOnly> { ["AAA"] = new(2026, 7, 1), ["BBB"] = new(2026, 7, 1), ["CCC"] = new(2026, 7, 1), ["DDD"] = Swap },
            EntrySessions(result));
        var exit = Assert.Single(result.PortfolioState!.Fills, fill => !fill.IsEntry);
        Assert.Equal(("CCC", Swap), (exit.Ticker, exit.SourceSessionDate));
        var replacement = Assert.Single(result.PortfolioState.Fills, fill => fill.IsEntry && fill.Ticker == "DDD");
        // July's rise (about 1.7%) lifts marked equity to about 101,700, so the 32.5% cap allows about 33,050 against the
        // 32,500 a slot is worth on starting capital.
        Assert.InRange(replacement.Quantity * replacement.Price, 32_700m, 33_100m);
        // 30 October is the last month-end; the list (and run) has no session after it, so nothing is marked as off the list.
        Assert.Empty(result.PortfolioState.PendingExits);
        Assert.Equal(["AAA", "BBB", "DDD"], result.PortfolioState.OpenPositions.Select(position => position.Ticker).Order());
    }

    // Paper/live paths and the signal study refuse the off-list exit; the file needs a decision schedule for it.
    [Fact]
    public void OffListExit_IsBacktestOnlyAndNeedsADecisionSchedule()
    {
        var repo = TestRepository.FindRoot();
        var file = Path.Combine(repo, "configs", "backtest", "strategies", "etf-rotation-12-1.bt-v1.yaml");
        var rotation = new SimpleYamlReader().ReadStrategy(file);
        var withoutOtherRules = rotation with
        {
            EntryRules = rotation.EntryRules with { DecisionSchedule = null },
            ExitRules = rotation.ExitRules with { ExitOnEntryFactorAtOrBelow = null, EntryFactorExitSchedule = null }
        };
        Assert.Throws<NotSupportedException>(() => TradingFlow.Engine.Execution.TechnicalExecutionEngine.RequireNoBacktestOnlyRules(withoutOtherRules));
        Assert.Throws<NotSupportedException>(() => TradingFlow.Research.Signals.SignalStudyOutcomes.RequireSupported(withoutOtherRules));
        var path = Path.Combine(Path.GetTempPath(), "etf-rotation-invalid-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            File.WriteAllText(path, File.ReadAllText(file).Replace("\r\n", "\n").Replace("  decision_schedule: month_end\n", ""));
            Assert.Throws<InvalidOperationException>(() => new SimpleYamlReader().ReadStrategy(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // With both settings off the 3-slot portfolio refuses DDD at the swap open (CCC still counts as held) and buys it only at
    // the next month-end decision, as every earlier run would have.
    [Fact]
    public async Task Rotation_WithTheSettingsOff_KeepsTheOldAdmission()
    {
        var result = await RunAsync(portfolio => portfolio with { SameOpenExitsReleaseCapacity = false, SizeFromMarkedEquity = false });

        Assert.Equal(new DateOnly(2026, 9, 1), EntrySessions(result)["DDD"]);
    }

    // The session of each fund's single entry fill.
    private static Dictionary<string, DateOnly> EntrySessions(BacktestResult result) =>
        result.PortfolioState!.Fills.Where(fill => fill.IsEntry).ToDictionary(fill => fill.Ticker, fill => fill.SourceSessionDate);

    // Run the catalog rotation with its profile (portfolio adjusted by the caller) on the fixture list for July to October 2026.
    private static async Task<BacktestResult> RunAsync(Func<PortfolioConfig, PortfolioConfig> portfolio)
    {
        using var root = new TemporaryFolder();
        var repo = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new StrategyArtifactCatalog(repo, Path.Combine(repo, "configs", "strategy-catalog.json"), reader);
        var validated = new StrategyRunArtifactValidator(reader, catalog).ReadAndValidateRuntimeArtifact(
            Path.Combine(repo, "configs", "backtest", "strategies", "etf-rotation-12-1.bt-v1.yaml"), StrategySelectionMode.Backtest);
        var runtime = StrategyDecisionRequestAssembler.CreateBacktestRuntimeStrategy(validated.Definition, catalog);
        var profile = reader.ReadBacktestRun(Path.Combine(repo, "configs", "backtest", "etf-rotation-profile.yaml"));
        var screen = await PublishListAsync(Path.Combine(root.Path, "screen"));
        var start = Midnight(new(2026, 7, 1));
        var run = profile with
        {
            RunName = "synthetic-etf-rotation", Provider = "synthetic-etf-rotation", Tickers = Pool, Intervals = ["1d"],
            RawRoot = Path.Combine(root.Path, "raw"), NormalizedRoot = Path.Combine(root.Path, "normalized"), ResultsRoot = Path.Combine(root.Path, "results"),
            Portfolio = portfolio(profile.Portfolio),
            TimeWindow = profile.TimeWindow with { Type = "fixed", Start = start, End = Midnight(new(2026, 10, 31)) },
            Universe = UniverseConfig.StaticUniverse with { Mode = UniverseConfig.ResolvedSnapshotMode, ResolvedSource = "research_screen",
                SelectedSourceTickers = Pool, ResolvedAtUtc = start, RankedMembership = new(screen.ManifestPath, screen.ManifestSha256) }
        };
        var runner = new BacktestRunner(reader, catalog, marketDataProviderFactory: _ => new FixtureProvider());
        return await runner.RunValidatedAsync(run, [runtime], DateTimeOffset.UtcNow, Path.Combine(run.ResultsRoot, "result.json"), default);
    }

    // The list: AAA, BBB and CCC on every session from 1 June 2026 to 31 July 2026, then AAA, BBB and DDD.
    private static async Task<PublishedResearchScreen> PublishListAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var sessions = Sessions().Where(day => day >= new DateOnly(2026, 6, 1)).ToArray();
        await File.WriteAllLinesAsync(Path.Combine(folder, "eligible.csv"),
            new[] { "session_date,ticker,price,average_volume,average_dollar_volume,atr,history_sessions" }.Concat(sessions.SelectMany(day =>
                (day < Swap ? new[] { "AAA", "BBB", "CCC" } : ["AAA", "BBB", "DDD"])
                    .Select(ticker => $"{day:yyyy-MM-dd},{ticker},60,2000000,120000000,1,300"))));
        await File.WriteAllLinesAsync(Path.Combine(folder, DailyScreenCommand.SplitExclusionsFileName), ["session_date,ticker,reason"]);
        await ResearchScreenManifest.PublishAsync(folder, new("daily", "actions", null, "sessions"),
            SyntheticScreen.Rules with { PriceBasis = DailyScreenPriceBasis.PriorClose }, null, sessions[0], sessions[^1],
            "alpaca_security_id", null, default, new ResearchScreenSplitRule(210, 0));
        return PublishedResearchScreen.Read(folder);
    }

    // Weekday sessions of the fixture (no holidays).
    private static DateOnly[] Sessions() => Enumerable.Range(new DateOnly(2025, 6, 2).DayNumber,
            new DateOnly(2026, 10, 30).DayNumber - new DateOnly(2025, 6, 2).DayNumber + 1).Select(DateOnly.FromDayNumber)
        .Where(day => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToArray();

    // New York midnight of a date in UTC, the opening stamp of that date's daily bar.
    private static DateTimeOffset Midnight(DateOnly day) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), NewYork), TimeSpan.Zero);

    // Each fund rises 0.05 a session from its own starting level.
    private static IEnumerable<OhlcvBar> DailyBars() => Sessions().SelectMany((day, i) =>
        Pool.Select((ticker, k) => Bar(ticker, day, 50m + 5m * k + 0.05m * i)));

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
            CancellationToken token) => Task.FromResult<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>(
                Enumerable.Range(start.DayNumber, end.DayNumber - start.DayNumber + 1).Select(DateOnly.FromDayNumber).ToDictionary(day => day,
                    day => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? MarketSessionSchedule.Closed(day) : MarketSessionSchedule.RegularDay(day)));
    }
}

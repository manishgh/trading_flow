using System.Runtime.CompilerServices;
using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Cli;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Tests;

/// <summary>
/// Connors RSI(2) 9.1.0 (as-traded bars) through the ordinary runner on a whole-market research screen (an eligible list).
/// Synthetic fixture, weekday sessions from 2 June 2025 to 30 October 2026, daily bars at New York midnight:
/// AAA rises 0.05 a session, dips 1.00 on each of 10, 11 and 14 September 2026 (RSI(2) 0, still above SMA200), fills at the
/// 15 September open and keeps closing under SMA5 until its last bar on 16 September, after which it has no bars.
/// EEE dips the same way, closes lower again on 17 September and has a recorded 2-for-1 split with ex-date 18 September, after
/// which it trades at half the price. BBB rises all the way and never signals. CCC is listed but has no bars at all.
/// </summary>
public sealed class ResearchScreenDailyRunTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateOnly LastAaaSession = new(2026, 9, 16);
    private static readonly DateOnly SplitExDate = new(2026, 9, 18);
    private static readonly string[] Pool = ["AAA", "BBB", "CCC", "EEE"];

    // (a) AAA is closed at its last real close with the labelled reason and October is still valued; (b) CCC is skipped and
    // listed against the pool; (c) EEE is closed at the 17 September close before its split; the run completes and its
    // economics stay published.
    [Fact]
    public async Task HeldStockWhoseBarsEnd_IsClosedAtItsLastClose_AndAStockWithoutDataIsSkipped()
    {
        using var root = new TemporaryFolder();
        var repo = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = RetiredStrategyFixtures.Catalog(reader);
        var validated = new StrategyRunArtifactValidator(reader, catalog).ReadAndValidateRuntimeArtifact(
            RetiredStrategyFixtures.Strategy("swing-connors-rsi2-published-as-traded.bt-v9-1.yaml"),
            TradingFlow.Domain.Strategies.StrategySelectionMode.Backtest);
        var runtime = StrategyDecisionRequestAssembler.CreateBacktestRuntimeStrategy(validated.Definition, catalog);
        var profile = reader.ReadBacktestRun(RetiredStrategyFixtures.Profile("connors-whole-market-daily-profile.yaml"));
        var screen = await PublishEligibleScreenAsync(Path.Combine(root.Path, "screen"));
        var start = Midnight(new(2026, 9, 1));
        var run = profile with
        {
            RunName = "synthetic-daily-screen", Provider = "synthetic-daily-screen", Tickers = Pool, Intervals = ["1d"],
            RawRoot = Path.Combine(root.Path, "raw"), NormalizedRoot = Path.Combine(root.Path, "normalized"), ResultsRoot = Path.Combine(root.Path, "results"),
            News = profile.News with { Enabled = false },
            TimeWindow = profile.TimeWindow with { Type = "fixed", Start = start, End = Midnight(new(2026, 10, 31)), WarmupLookbackDays = 400 },
            Universe = UniverseConfig.StaticUniverse with { Mode = UniverseConfig.ResolvedSnapshotMode, ResolvedSource = "research_screen",
                SelectedSourceTickers = Pool, ResolvedAtUtc = start, RankedMembership = new(screen.ManifestPath, screen.ManifestSha256) }
        };
        var runner = new BacktestRunner(reader, catalog, marketDataProviderFactory: _ => new DailyScreenProvider());

        var result = await runner.RunValidatedAsync(run, [runtime], DateTimeOffset.UtcNow, Path.Combine(run.ResultsRoot, "result.json"), default);

        Assert.True(result.EconomicResultsComplete);
        Assert.Equal(2, result.CompletedTrades.Count);
        var trade = result.CompletedTrades.Single(item => item.Ticker == "AAA");
        Assert.Equal(("AAA", ChronologicalBacktestExecutionCoordinator.DataEndedExitReason), (trade.Ticker, trade.ExitReason));
        Assert.Equal(Midnight(LastAaaSession.AddDays(1)), trade.ExitTimestamp); // the last bar's completion, not a later session
        var lastClose = DailyBars().Last(bar => bar.Ticker == "AAA").Close;
        Assert.Equal(lastClose * (1 - 0.001m), trade.ExitPrice, 4); // the last real close less the strategy's 10 bps slippage
        var split = result.CompletedTrades.Single(item => item.Ticker == "EEE");
        Assert.Equal((ChronologicalBacktestExecutionCoordinator.SplitExitReason, Midnight(SplitExDate)), (split.ExitReason, split.ExitTimestamp));
        var preSplitClose = DailyBars().Single(bar => bar.Ticker == "EEE" && bar.Timestamp == Midnight(SplitExDate.AddDays(-1))).Close;
        Assert.Equal(preSplitClose * (1 - 0.001m), split.ExitPrice, 4);
        var october = result.PortfolioState!.Sessions.Where(s => s.SessionDate.Month == 10).ToArray();
        Assert.NotEmpty(october);
        Assert.All(october, session => Assert.NotNull(session.Equity));
        Assert.True(result.PortfolioState.OpenPositions.Count == 0, System.Text.Json.JsonSerializer.Serialize(result.PortfolioState.OpenPositions));
        Assert.Equal(result.PortfolioState.Sessions[^1].Cash, result.PortfolioState.Sessions[^1].Equity);
        var gaps = Assert.Single(Directory.EnumerateFiles(root.Path, "*-data-gaps.json", SearchOption.AllDirectories));
        using var json = JsonDocument.Parse(File.ReadAllText(gaps));
        Assert.Equal(1, json.RootElement.GetProperty("skippedStockWorkItems").GetInt32());
        Assert.Equal("CCC", json.RootElement.GetProperty("items")[0].GetProperty("ticker").GetString());
        Assert.Equal((4, 1, 25m, true), (json.RootElement.GetProperty("poolStocks").GetInt32(), json.RootElement.GetProperty("skippedStocks").GetInt32(),
            json.RootElement.GetProperty("skippedStocksPct").GetDecimal(), json.RootElement.GetProperty("skippedAboveFivePercent").GetBoolean()));
        Assert.Equal((1, 1), (json.RootElement.GetProperty("dataEndedLastCloseExits").GetInt32(), json.RootElement.GetProperty("splitExitsBeforeExDate").GetInt32()));
        Assert.Equal(["AAA", "EEE"], json.RootElement.GetProperty("forcedExits").EnumerateArray().Select(item => item.GetProperty("ticker").GetString()).Order());
    }

    // An eligible list (prior-close price rule) naming every stock on each evaluated session, except EEE from its split ex-date
    // on, as split_in_lookback leaves it out.
    private static async Task<PublishedResearchScreen> PublishEligibleScreenAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var sessions = Sessions().Where(day => day >= new DateOnly(2026, 9, 1)).ToArray();
        await File.WriteAllLinesAsync(Path.Combine(folder, "eligible.csv"),
            new[] { "session_date,ticker,price,average_volume,average_dollar_volume,atr,history_sessions" }.Concat(sessions.SelectMany(day =>
                Pool.Where(ticker => ticker != "EEE" || day < SplitExDate).Select(ticker => $"{day:yyyy-MM-dd},{ticker},60,2000000,120000000,1,300"))));
        await File.WriteAllLinesAsync(Path.Combine(folder, DailyScreenCommand.SplitExclusionsFileName), ["session_date,ticker,reason"]);
        await ResearchScreenManifest.PublishAsync(folder, new("daily", "actions", null, "sessions"),
            SyntheticScreen.Rules with { PriceBasis = DailyScreenPriceBasis.PriorClose }, null, sessions[0], sessions[^1],
            "alpaca_security_id", null, default, new ResearchScreenSplitRule(200, 0));
        return PublishedResearchScreen.Read(folder);
    }

    // Weekday sessions of the fixture (no holidays).
    private static DateOnly[] Sessions() => Enumerable.Range(new DateOnly(2025, 6, 2).DayNumber,
            new DateOnly(2026, 10, 30).DayNumber - new DateOnly(2025, 6, 2).DayNumber + 1).Select(DateOnly.FromDayNumber)
        .Where(day => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToArray();

    // New York midnight of a date in UTC, the opening stamp of that date's daily bar.
    private static DateTimeOffset Midnight(DateOnly day) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), NewYork), TimeSpan.Zero);

    // AAA's, BBB's and EEE's as-traded daily bars; CCC has none.
    private static IEnumerable<OhlcvBar> DailyBars()
    {
        var sessions = Sessions();
        var dip = Array.IndexOf(sessions, new DateOnly(2026, 9, 10));
        var trend = 50m + 0.05m * (dip - 1);
        var aaa = sessions.Select((day, i) => (day, close: i < dip ? 50m + 0.05m * i : i switch
        {
            _ when i == dip => trend - 1m, _ when i == dip + 1 => trend - 2m, _ when i == dip + 2 => trend - 3m,
            _ when i == dip + 3 => trend - 3.2m, _ => trend - 3.4m
        })).Where(item => item.day <= LastAaaSession).ToArray();
        foreach (var (day, close) in aaa.Select((item, i) => (item.day, item.close)))
            yield return Bar("AAA", day, close);
        foreach (var (day, i) in sessions.Select((day, i) => (day, i)))
            yield return Bar("BBB", day, 30m + 0.05m * i);
        foreach (var (day, i) in sessions.Select((day, i) => (day, i)))
            yield return Bar("EEE", day, i <= dip + 4 ? aaa[i].close : i == dip + 5 ? trend - 3.6m : (trend - 3.6m) / 2m);
    }

    // One as-traded daily bar opening at the close (no gap), with a one-point range and two million shares.
    private static OhlcvBar Bar(string ticker, DateOnly day, decimal close) =>
        new(ticker, Midnight(day), "1d", close, close + 0.5m, close - 0.5m, close, 2_000_000m, "sip", "raw");

    /// <summary>Serves the fixture's daily bars and its weekday calendar; no network and no real data.</summary>
    private sealed class DailyScreenProvider : IMarketDataProvider, IMarketSessionScheduleProvider, IShareSplitProvider
    {
        private readonly OhlcvBar[] bars = DailyBars().ToArray();

        /// <summary>EEE's recorded 2-for-1 split, the only one in the fixture's corporate actions.</summary>
        public Task<IReadOnlyDictionary<string, IReadOnlyList<ShareSplit>>> LoadShareSplitsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ShareSplit>>>(
                new Dictionary<string, IReadOnlyList<ShareSplit>> { ["EEE"] = [new ShareSplit(SplitExDate, 2m)] });

        /// <summary>The fixture's bars of the requested stocks and timeframes inside [start, end).</summary>
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var bar in bars.Where(bar => tickers.Contains(bar.Ticker) && timeframes.Contains(bar.Timeframe) &&
                         bar.Timestamp >= start && bar.Timestamp < end))
                yield return bar;
        }

        /// <summary>Weekdays are regular sessions and weekends are closed; the fixture declares no holidays.</summary>
        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(DateOnly start, DateOnly end,
            CancellationToken token) => Task.FromResult<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>(
                Enumerable.Range(start.DayNumber, end.DayNumber - start.DayNumber + 1).Select(DateOnly.FromDayNumber).ToDictionary(day => day,
                    day => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? MarketSessionSchedule.Closed(day) : MarketSessionSchedule.RegularDay(day)));
    }
}

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
/// Connors 9.2.0 and 7.2.0 (decision-hour timing) through the ordinary runner on a whole-market research screen with daily and
/// hourly bars. Synthetic fixture: weekday sessions from 2 June 2025 to 30 October 2026, 25 September 2026 a half day closing at
/// 13:00. Each stock rises 0.05 a session; its hourly bars (10:00 to the last hour) move linearly from the open to the 15:00
/// decision price, and the last hour runs from its open to the close. Scenario days (2026):
/// AAA dips 3.00 by 15:00 on 14 September (RSI(2) under 5 on the 15:00 price) but closes 0.50 above trend (RSI(2) high on the
/// final close), then closes 0.60 above trend; BBB is on trend at 15:00 on 14 September and crashes 3.00 in the last hour (RSI(2)
/// under 5 only on the final close); CCC misses its 14:00 bar on 16 September and its 15:00 bar on 17 September; EEE dips 3.00 on
/// 14 September, keeps falling on 15 and 16 September and splits 2-for-1 with ex-date 17 September; FFF dips 3.00 by the 12:00
/// decision on the 25 September half day and closes 0.60 above trend from 28 September. For 7.2.0, GGG, HHH and III dip like EEE
/// on 14 September; on 15 September their 11:00 bar falls 5.00 below the decision price (GGG), spans both 5.00 below and 10.00
/// above (HHH), or rises 10.00 above (III).
/// </summary>
public sealed class DecisionHourBacktestTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateOnly DipDay = new(2026, 9, 14);
    private static readonly DateOnly SplitExDate = new(2026, 9, 17);
    private static readonly DateOnly HalfDay = new(2026, 9, 25);
    private static readonly DateOnly[] AllSessions = Sessions();

    // 9.2.0: the decision follows the 15:00 price (AAA enters, BBB does not), entries and the SMA5 exit fill on the last-hour
    // bar, a half-day entry fills on the 12:00 bar, a split exit fills at the open of the last hour before the ex-date, missing
    // hourly bars skip their stock-days with named reasons, and every fill is booked to its own New York session.
    [Fact]
    public async Task PublishedRules_DecideAtFifteenHundredAndFillInTheLastHour()
    {
        using var root = new TemporaryFolder();
        var result = await RunAsync(root.Path, "swing-connors-rsi2-published-decision-hour.bt-v9-2.yaml", ["AAA", "BBB", "CCC", "EEE", "FFF", "LLL"]);

        Assert.True(result.EconomicResultsComplete, Why(result));
        Assert.DoesNotContain(result.CompletedTrades, trade => trade.Ticker is "BBB" or "CCC" or "LLL");
        // The final 16:00 closes disagree with the 15:00 prices: AAA's would not signal and BBB's would.
        Assert.True(FinalCloseRsi2("AAA") > 5m && FinalCloseRsi2("BBB") < 5m);
        var aaa = Assert.Single(result.CompletedTrades, trade => trade.Ticker == "AAA");
        Assert.Equal((At(DipDay, 16), At(DipDay.AddDays(1), 16), "technical_exit_above_sma5"), (aaa.EntryTimestamp, aaa.ExitTimestamp, aaa.ExitReason));
        Assert.Equal(Fixture("AAA", DipDay).LastHourOpen * 1.001m, aaa.EntryPrice, 3);
        Assert.Equal(Fixture("AAA", DipDay.AddDays(1)).LastHourOpen * 0.999m, aaa.ExitPrice, 3);
        var split = Assert.Single(result.CompletedTrades, trade => trade.Ticker == "EEE");
        Assert.Equal((ChronologicalBacktestExecutionCoordinator.SplitExitReason, At(SplitExDate.AddDays(-1), 15).AddTicks(1)),
            (split.ExitReason, split.ExitTimestamp));
        Assert.Equal(Fixture("EEE", SplitExDate.AddDays(-1)).LastHourOpen * 0.999m, split.ExitPrice, 3);
        var halfDay = Assert.Single(result.CompletedTrades, trade => trade.Ticker == "FFF");
        Assert.Equal(At(HalfDay, 13), halfDay.EntryTimestamp);
        Assert.Equal(Fixture("FFF", HalfDay).LastHourOpen * 1.001m, halfDay.EntryPrice, 3);
        Assert.All(result.PortfolioState!.Fills, fill => Assert.Equal(
            (ExecutionDate(fill.TimestampUtc), "1h"), (fill.SourceSessionDate, fill.FillTimeframe)));
        Assert.All(result.PortfolioState.Sessions, session => Assert.NotNull(session.Equity));
        using var gaps = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.EnumerateFiles(root.Path, "*-data-gaps.json", SearchOption.AllDirectories))));
        var skipped = gaps.RootElement.GetProperty("decisionHourSkippedStockDays").EnumerateObject().Single().Value;
        Assert.Equal((1, 1), (skipped.GetProperty(DecisionHourDailyBars.DecisionBarMissingReason).GetInt32(),
            skipped.GetProperty(DecisionHourDailyBars.LastHourBarMissingReason).GetInt32()));
        // FFF's four bars outside the half day's 09:30-13:00 are dropped before use; LLL's and EEE's 16 September entries
        // (eve of their 17 September ex-dates) are refused at admission.
        var execution = gaps.RootElement.GetProperty("decisionHourExecution");
        var counts = execution.Deserialize<Dictionary<string, int>>()!;
        Assert.True(counts.GetValueOrDefault("hourly_bars_outside_session") == 4 &&
            counts.GetValueOrDefault(BacktestRunner.SplitEveEntryRefusedReason) == 2, execution.GetRawText());
    }

    // 7.2.0: the ATR stop and the 2.5R target are checked on the hourly bars after the entry fill. GGG stops out on the 11:00
    // bar of 15 September, HHH touches both in that bar and stops out (worst case), III reaches its target in it.
    [Fact]
    public async Task RiskManagedRules_CheckStopsAndTargetsOnHourlyBars()
    {
        using var root = new TemporaryFolder();
        var result = await RunAsync(root.Path, "swing-connors-rsi2-sma5-risk-managed-decision-hour.bt-v7-2.yaml", ["GGG", "HHH", "III", "JJJ"]);

        Assert.True(result.EconomicResultsComplete, Why(result));
        var exitBarEnd = At(DipDay.AddDays(1), 12);
        foreach (var (ticker, reason) in new[] { ("GGG", "stop_loss"), ("HHH", "stop_loss"), ("III", "take_profit") })
        {
            var trade = Assert.Single(result.CompletedTrades, item => item.Ticker == ticker);
            Assert.Equal((At(DipDay, 16), exitBarEnd, reason), (trade.EntryTimestamp, trade.ExitTimestamp, trade.ExitReason));
            Assert.True(trade.StopLossPrice > 0m && trade.TakeProfitPrice > trade.StopLossPrice);
        }
        var stopped = result.CompletedTrades.Single(item => item.Ticker == "GGG");
        Assert.True(stopped.ExitPrice <= stopped.StopLossPrice, $"stop fill {stopped.ExitPrice} above stop {stopped.StopLossPrice}");
        // JJJ has no hourly bars on 16 September: that held session still counts towards the five-bar limit, so the limit
        // exit is decided on 21 September (the fifth held session) and fills in its last hour.
        var limited = result.CompletedTrades.Where(item => item.Ticker == "JJJ").MinBy(item => item.EntryTimestamp)!;
        Assert.Equal((At(DipDay, 16), At(new(2026, 9, 21), 16), "max_hold_bars"), (limited.EntryTimestamp, limited.ExitTimestamp, limited.ExitReason));
    }

    // Parity of the shared per-stock-session cache with the full computation: a second strategy asking for the same session
    // gets the same evidence, computed once, equal to the provisional snapshot over every prior completed daily bar.
    [Fact]
    public void SessionEvidenceCache_ComputesEachStockSessionOnceAndMatchesTheFullComputation()
    {
        var day = DipDay;
        var index = Array.IndexOf(AllSessions, day) - 1; // daily bars start at the second session
        var daily = AllSessions.Skip(1).Select(session => DailyBar("AAA", session)).ToArray();
        var hourly = HourlyBars("AAA", day).ToArray();
        var engine = new IndicatorEngine();
        var schedule = MarketSessionSchedule.RegularDay(day);
        var cache = new DecisionHourEvidenceCache();

        var first = cache.Get("AAA", day, () => DecisionHourEvidenceCache.Compute(engine, daily, index, hourly, schedule, NewYork));
        var second = cache.Get("aaa", day, () => throw new InvalidOperationException("computed twice"));

        Assert.Same(first, second);
        Assert.Equal(1, cache.ComputedCount);
        var full = DecisionHourDailyBars.ProvisionalSnapshot(engine, daily.Take(index).ToArray(), first.Decision!.ProvisionalDailyBar);
        Assert.Equal(JsonSerializer.Serialize(full), JsonSerializer.Serialize(first.Snapshot));
        Assert.True(first.Snapshot!.Rsi2 < 5m);
    }

    // Why a run's economics are withheld: failed stocks, rejection counts and the ledger's unavailable reason (assertion text).
    private static string Why(BacktestResult result) => JsonSerializer.Serialize(new
    {
        failed = result.TickerResults.Where(ticker => !ticker.Succeeded).Select(ticker => ticker.ErrorMessage),
        rejections = result.Diagnostics.Select(item => item.RejectionCounts),
        unavailable = result.PortfolioState?.UnavailableReason
    });

    // Runs one decision-hour strategy over the fixture with the hourly research profile and an eligible-list screen.
    private static async Task<BacktestResult> RunAsync(string folder, string strategyFile, string[] pool)
    {
        var repo = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new StrategyArtifactCatalog(repo, Path.Combine(repo, "configs", "strategy-catalog.json"), reader);
        var validated = new StrategyRunArtifactValidator(reader, catalog).ReadAndValidateRuntimeArtifact(
            Path.Combine(repo, "configs", "backtest", "strategies", strategyFile), TradingFlow.Domain.Strategies.StrategySelectionMode.Backtest);
        var runtime = StrategyDecisionRequestAssembler.CreateBacktestRuntimeStrategy(validated.Definition, catalog);
        var profile = reader.ReadBacktestRun(Path.Combine(repo, "configs", "backtest", "connors-whole-market-hourly-profile.yaml"));
        var screen = await PublishEligibleScreenAsync(Path.Combine(folder, "screen"), pool);
        var start = At(new(2026, 9, 1), 0);
        var run = profile with
        {
            RunName = "synthetic-decision-hour", Provider = "synthetic-decision-hour", Tickers = pool, Intervals = ["1d", "1h"],
            RawRoot = Path.Combine(folder, "raw"), NormalizedRoot = Path.Combine(folder, "normalized"), ResultsRoot = Path.Combine(folder, "results"),
            News = profile.News with { Enabled = false },
            TimeWindow = profile.TimeWindow with { Type = "fixed", Start = start, End = At(new(2026, 10, 31), 0), WarmupLookbackDays = 400 },
            Universe = UniverseConfig.StaticUniverse with { Mode = UniverseConfig.ResolvedSnapshotMode, ResolvedSource = "research_screen",
                SelectedSourceTickers = pool, ResolvedAtUtc = start, RankedMembership = new(screen.ManifestPath, screen.ManifestSha256) }
        };
        var runner = new BacktestRunner(reader, catalog, marketDataProviderFactory: _ => new DecisionHourProvider());
        return await runner.RunValidatedAsync(run, [runtime], DateTimeOffset.UtcNow, Path.Combine(run.ResultsRoot, "result.json"), default);
    }

    // An eligible list (prior-close price rule) naming every pool stock on each evaluated session, except EEE from its ex-date.
    private static async Task<PublishedResearchScreen> PublishEligibleScreenAsync(string folder, string[] pool)
    {
        Directory.CreateDirectory(folder);
        var sessions = Sessions().Where(day => day >= new DateOnly(2026, 9, 1)).ToArray();
        await File.WriteAllLinesAsync(Path.Combine(folder, "eligible.csv"),
            new[] { "session_date,ticker,price,average_volume,average_dollar_volume,atr,history_sessions" }.Concat(sessions.SelectMany(day =>
                pool.Where(ticker => ticker is not ("EEE" or "LLL") || day < SplitExDate).Select(ticker => $"{day:yyyy-MM-dd},{ticker},60,2000000,120000000,1,300"))));
        await File.WriteAllLinesAsync(Path.Combine(folder, DailyScreenCommand.SplitExclusionsFileName), ["session_date,ticker,reason"]);
        await ResearchScreenManifest.PublishAsync(folder, new("daily", "actions", null, "sessions"),
            SyntheticScreen.Rules with { PriceBasis = DailyScreenPriceBasis.PriorClose }, null, sessions[0], sessions[^1],
            "alpaca_security_id", null, default, new ResearchScreenSplitRule(200, 0));
        return PublishedResearchScreen.Read(folder);
    }

    /// <summary>One session's path: the open, the 15:00 decision price, the last hour's open and the close.</summary>
    private sealed record SessionPath(decimal Open, decimal Decision, decimal LastHourOpen, decimal Close)
    {
        /// <summary>Hourly bars (by starting hour) replaced by explicit open, high, low and close.</summary>
        public Dictionary<int, (decimal Open, decimal High, decimal Low, decimal Close)> Overrides { get; init; } = [];

        /// <summary>Starting hours of hourly bars that are missing.</summary>
        public int[] Missing { get; init; } = [];
    }

    // Weekday sessions of the fixture (no holidays; 25 September 2026 closes early).
    private static DateOnly[] Sessions() => Enumerable.Range(new DateOnly(2025, 6, 2).DayNumber,
            new DateOnly(2026, 10, 30).DayNumber - new DateOnly(2025, 6, 2).DayNumber + 1).Select(DateOnly.FromDayNumber)
        .Where(day => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToArray();

    // The closing hour of a session: 13 on the half day, 16 otherwise.
    private static int CloseHour(DateOnly day) => day == HalfDay ? 13 : 16;

    // A stock's trend close at session index i, before its scenario offsets.
    private static decimal Trend(string ticker, int i) => ticker switch
    {
        "BBB" => 30m, "CCC" => 40m, "FFF" => 70m, "SPY" => 400m, _ => 50m
    } + 0.05m * i;

    // A stock's scenario path on one session (see the class summary); an ordinary session opens at the prior close and stays on trend.
    private static SessionPath Fixture(string ticker, DateOnly day)
    {
        var i = Array.IndexOf(AllSessions, day);
        var close = Close(ticker, i);
        var plain = new SessionPath(Close(ticker, i - 1), close, close, close);
        if (day == DipDay && ticker == "AAA") return plain with { Decision = Trend(ticker, i) - 3m, LastHourOpen = Trend(ticker, i) - 3m };
        if (day == DipDay && ticker == "BBB") return plain with { Decision = Trend(ticker, i), LastHourOpen = Trend(ticker, i) };
        if (ticker == "CCC" && day == new DateOnly(2026, 9, 16)) return plain with { Missing = [14] };
        if (ticker == "CCC" && day == SplitExDate) return plain with { Missing = [15] };
        if (ticker == "JJJ" && day == DipDay.AddDays(2)) return plain with { Missing = [10, 11, 12, 13, 14, 15] };
        if (day == HalfDay && ticker == "FFF") return plain;
        if (day == DipDay.AddDays(1) && ticker is "GGG" or "HHH" or "III")
        {
            var prior = plain.Open;
            var bar = ticker switch
            {
                "GGG" => (prior, prior, prior - 5m, prior), "HHH" => (prior, prior + 10m, prior - 5m, prior),
                _ => (prior, prior + 10m, prior, prior)
            };
            return plain with { Overrides = new() { [11] = bar } };
        }
        return plain;
    }

    // A stock's close at session index i with its scenario offsets (see the class summary).
    private static decimal Close(string ticker, int i)
    {
        var day = AllSessions[i];
        var trend = Trend(ticker, i);
        return ticker switch
        {
            "AAA" when day == DipDay => trend + 0.5m,
            "AAA" when day > DipDay => trend + 0.6m,
            "BBB" when day == DipDay => trend - 3m,
            "EEE" when day == DipDay => trend - 3m,
            "EEE" when day == DipDay.AddDays(1) => trend - 3.2m,
            "EEE" when day == DipDay.AddDays(2) => trend - 3.4m,
            "EEE" when day >= SplitExDate => (trend - 3.6m) / 2m,
            "FFF" when day == HalfDay => trend - 3m,
            "FFF" when day > HalfDay => trend + 0.6m,
            "GGG" or "HHH" or "III" when day == DipDay => trend - 3m,
            "GGG" or "HHH" or "III" when day > DipDay => trend + 1m,
            "JJJ" when day >= DipDay => Trend(ticker, Array.IndexOf(AllSessions, DipDay)) - 3m,
            "LLL" when day == DipDay.AddDays(2) => trend - 3m,
            "LLL" when day >= SplitExDate => (trend - 3.2m) / 2m,
            _ => trend
        };
    }

    // The hourly bars of a session from 10:00 to its last hour: linear from the open to the decision price, then the last hour.
    private static IEnumerable<OhlcvBar> HourlyBars(string ticker, DateOnly day)
    {
        var path = Fixture(ticker, day);
        var lastHour = CloseHour(day) - 1;
        for (var hour = 10; hour <= lastHour; hour++)
        {
            if (path.Missing.Contains(hour)) continue;
            var (open, close) = hour == lastHour ? (path.LastHourOpen, path.Close)
                : (Step(path, hour - 10, lastHour - 10), Step(path, hour - 9, lastHour - 10));
            var (o, h, l, c) = path.Overrides.TryGetValue(hour, out var custom) ? custom
                : (open, Math.Max(open, close) + 0.05m, Math.Min(open, close) - 0.05m, close);
            yield return new OhlcvBar(ticker, At(day, hour), "1h", o, h, l, c, 200_000m, "sip", "raw");
        }
        // FFF's bars outside the half day's regular session (pre-market 08:00, after the 13:00 close), far off its prices.
        if (ticker == "FFF" && day == HalfDay)
            foreach (var hour in new[] { 8, 13, 14, 15 })
                yield return new OhlcvBar(ticker, At(day, hour), "1h", 1m, 1m, 1m, 1m, 200_000m, "sip", "raw");
    }

    // The price after the given number of the session's steps from its open towards its decision price.
    private static decimal Step(SessionPath path, int step, int steps) => path.Open + (path.Decision - path.Open) * step / steps;

    // The session's daily bar: its open and close, with a range covering its hourly bars.
    private static OhlcvBar DailyBar(string ticker, DateOnly day)
    {
        var path = Fixture(ticker, day);
        var hourly = day >= new DateOnly(2026, 8, 24) ? HourlyBars(ticker, day).Where(bar => bar.Timestamp >= At(day, 10) && bar.Timestamp < At(day, CloseHour(day))).ToArray() : [];
        var high = hourly.Select(bar => bar.High).Append(Math.Max(path.Open, path.Close) + 0.5m).Max();
        var low = hourly.Select(bar => bar.Low).Append(Math.Min(path.Open, path.Close) - 0.5m).Min();
        return new OhlcvBar(ticker, At(day, 0), "1d", path.Open, high, low, path.Close, 2_000_000m, "sip", "raw");
    }

    // RSI(2) on the dip day's final daily close, as the daily (16:00) timing would read it.
    private static decimal? FinalCloseRsi2(string ticker) => new IndicatorEngine().Compute(
        AllSessions.Skip(1).Where(day => day <= DipDay).Select(day => DailyBar(ticker, day)).ToArray())[^1].Rsi2;

    // The New York session an execution stamped at the end of its slice belongs to.
    private static DateOnly ExecutionDate(DateTimeOffset stamp) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(stamp.AddTicks(-1), NewYork).DateTime);

    private static DateTimeOffset At(DateOnly day, int hour) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, 0)), NewYork), TimeSpan.Zero);

    /// <summary>Serves the fixture's daily bars (from the second session), hourly bars (from 24 August 2026), calendar and EEE's split.</summary>
    private sealed class DecisionHourProvider : IMarketDataProvider, IMarketSessionScheduleProvider, IShareSplitProvider
    {
        private static readonly string[] Tickers = ["AAA", "BBB", "CCC", "EEE", "FFF", "GGG", "HHH", "III", "JJJ", "LLL", "SPY"];
        private readonly Lazy<OhlcvBar[]> bars = new(() => Tickers.SelectMany(ticker => Sessions().Skip(1).SelectMany(day =>
            new[] { DailyBar(ticker, day) }.Concat(day >= new DateOnly(2026, 8, 24) ? HourlyBars(ticker, day) : []))).ToArray());

        /// <summary>EEE's recorded 2-for-1 split, the only one in the fixture's corporate actions.</summary>
        public Task<IReadOnlyDictionary<string, IReadOnlyList<ShareSplit>>> LoadShareSplitsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ShareSplit>>>(
                new Dictionary<string, IReadOnlyList<ShareSplit>> { ["EEE"] = [new ShareSplit(SplitExDate, 2m)], ["LLL"] = [new ShareSplit(SplitExDate, 2m)] });

        /// <summary>The fixture's bars of the requested stocks and timeframes inside [start, end).</summary>
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var bar in bars.Value.Where(bar => tickers.Contains(bar.Ticker) && timeframes.Contains(bar.Timeframe) &&
                         bar.Timestamp >= start && bar.Timestamp < end))
                yield return bar;
        }

        /// <summary>Weekdays are sessions (25 September 2026 closes at 13:00) and weekends are closed; no holidays.</summary>
        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(DateOnly start, DateOnly end,
            CancellationToken token) => Task.FromResult<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>(
                Enumerable.Range(start.DayNumber, end.DayNumber - start.DayNumber + 1).Select(DateOnly.FromDayNumber).ToDictionary(day => day,
                    day => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? MarketSessionSchedule.Closed(day)
                        : day == HalfDay ? new MarketSessionSchedule(day, true, new TimeOnly(9, 30), new TimeOnly(13, 0))
                        : MarketSessionSchedule.RegularDay(day)));
    }
}

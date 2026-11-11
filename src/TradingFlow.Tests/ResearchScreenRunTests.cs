using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Data.Csv;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Universe;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

/// <summary>
/// ORB 2.1.0 through the ordinary prepared runner with a frozen research-screen ranked list (universe.ranked_membership):
/// one-minute bars settle entry and stop inside the entry's five-minute bar, coverage is checked only where the ranked list
/// makes the run read bars, and two runs publish identical trade files. Fixture: stock ABC is selected on 1 October 2026;
/// twenty earlier sessions trade 95-100 (ATR 5); the 09:30 bar (99-100-98-99.5, 200,000 shares) sets a long stop order at 100,
/// which triggers inside the 09:35 bar (high 100, low 98), whose low also reaches the stop measured from the fill; later bars
/// stay at 99.80-100.20, above the stop. The bars are labelled adjusted ("all"), the price basis ORB 2.1.0's admission profile
/// requires; the evidence catalog's as-traded ("raw") bars are refused by that profile (an open decision, see the handoff).
/// </summary>
public sealed class ResearchScreenRunTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-01T13:30:00Z");
    private static readonly DateTimeOffset EntryBar = Start.AddMinutes(5);

    [Theory]
    [InlineData("low_before_entry", "session_close", 390, IntradayPortfolioExecution.MinutesSettledNoStopAfterEntry)]
    [InlineData("low_after_entry", "stop_loss", 10, IntradayPortfolioExecution.MinutesSettledStopAfterEntry)]
    [InlineData("same_minute", "stop_loss_entry_minute_order_unknown", 10, IntradayPortfolioExecution.SameMinuteLoss)]
    [InlineData("incomplete", "stop_loss_entry_bar_order_unknown", 10, IntradayPortfolioExecution.UnknownMinutesDoNotMatch)]
    [InlineData("none", "stop_loss_entry_bar_order_unknown", 10, IntradayPortfolioExecution.UnknownNoMinuteBars)]
    public async Task MinuteBars_SettleTheEntryBarOrLeaveTheCountedWorstCase(string scenario, string reason, int exitMinute, string counted)
    {
        await WithScreenRunAsync(Minutes(scenario), null, null, (result, root) =>
        {
            Assert.True(result.EconomicResultsComplete);
            var trade = Assert.Single(result.CompletedTrades);
            Assert.Equal(reason, trade.ExitReason.Split(';')[0]);
            // Stopped in the entry bar (ends 09:40), or, when minutes show the low came before the entry, held to the 16:00 close.
            Assert.Equal(Start.AddMinutes(exitMinute), trade.ExitTimestamp);
            var file = Assert.Single(Directory.EnumerateFiles(root, "*-entry-bar-minute-order.json", SearchOption.AllDirectories));
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            Assert.Equal(1, json.RootElement.GetProperty("byReason").GetProperty(counted).GetInt32());
            Assert.Equal(1, json.RootElement.GetProperty("orders").GetArrayLength());
            var eligibility = Assert.Single(Directory.EnumerateFiles(root, "*-daily-eligibility.json", SearchOption.AllDirectories));
            using var members = JsonDocument.Parse(File.ReadAllText(eligibility));
            Assert.Equal(64, members.RootElement.GetProperty("manifestSha256").GetString()!.Length);
            Assert.Equal("2026-10-01", members.RootElement.GetProperty("eligibleDays").GetProperty("ABC")[0].GetString());
        });
    }

    // A bar missing on an earlier session that neither the ATR nor the opening-volume baseline reads (18 sessions back) is
    // never checked or sent to the trade-record source; the same gap on the selected session, or on a baseline session the
    // ATR reads (10 sessions back), goes to the trade-record check and, unconfirmed, withholds the economics.
    [Theory]
    [InlineData(-18, true)]
    [InlineData(-10, false)]
    [InlineData(0, false)]
    public async Task Coverage_IsCheckedOnlyOnSessionsTheRankedListMakesTheRunRead(int sessionsBack, bool complete)
    {
        var trades = new RecordingTradeSource();
        await WithScreenRunAsync(Minutes("low_after_entry"), sessionsBack, trades, (result, _) =>
        {
            Assert.Equal(complete, result.EconomicResultsComplete);
            Assert.Equal(complete ? 0 : 1, trades.Requests.Count);
        });
    }

    // The same frozen inputs give byte-identical trade files in two separate runs. The second run starts from an empty folder
    // at the same path, because the run settings (and so the candidate identities) include their data paths.
    [Fact]
    public async Task Replay_TwoRunsPublishIdenticalTradeFiles()
    {
        var hashes = new List<string>();
        var folderName = "tf-research-screen-replay-" + Guid.NewGuid().ToString("N");
        for (var run = 0; run < 2; run++)
            await WithScreenRunAsync(Minutes("low_after_entry"), null, null, (result, root) =>
            {
                Assert.Single(result.CompletedTrades);
                var file = Assert.Single(Directory.EnumerateFiles(root, "*.unified-completed-trades.json", SearchOption.AllDirectories));
                hashes.Add(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));
            }, folderName);
        Assert.Equal(hashes[0], hashes[1]);
    }

    // One-minute bars inside the 09:35 entry bar for each scenario; every complete set reproduces its high 100 and low 98.
    private static OhlcvBar[] Minutes(string scenario)
    {
        OhlcvBar Minute(int minute, decimal open, decimal high, decimal low, decimal close) =>
            new("ABC", EntryBar.AddMinutes(minute), "1m", open, high, low, close, 40000m, "sip", "all");
        return scenario switch
        {
            "low_before_entry" => [Minute(0, 99m, 99.2m, 98m, 99.1m), Minute(1, 99.85m, 100m, 99.8m, 99.9m), Minute(2, 99.9m, 99.95m, 99.8m, 99.9m)],
            "low_after_entry" => [Minute(0, 99.85m, 100m, 99.8m, 99.9m), Minute(1, 99.9m, 99.9m, 98m, 98.5m)],
            "same_minute" => [Minute(0, 99m, 100m, 98m, 99.5m)],
            "incomplete" => [Minute(0, 99m, 100m, 98.5m, 99.5m)],
            _ => []
        };
    }

    // Publishes a screen selecting ABC on 1 October and runs ORB 2.1.0 with the whole-market profile through the ordinary
    // prepared runner on synthetic bars, optionally omitting one five-minute bar at 15:00 UTC on a session counted back from
    // 1 October, then deletes the isolated folder.
    private static async Task WithScreenRunAsync(IReadOnlyList<OhlcvBar> minutes, int? omitSessionsBack, IIntervalTradePrintSource? trades,
        Action<BacktestResult, string> verify, string? folderName = null)
    {
        using var root = new TemporaryFolder(folderName);
        var repo = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var artifacts = new StrategyArtifactCatalog(repo, Path.Combine(repo, "configs", "strategy-catalog.json"), reader);
        var strategyPath = Path.Combine(repo, "configs", "backtest", "strategies", "opening-range-breakout.bt-v5.yaml");
        var runtime = new StrategyRunArtifactValidator(reader, artifacts).ReadAndValidateRuntimeArtifact(strategyPath, StrategySelectionMode.Backtest);
        var profile = reader.ReadBacktestRun(Path.Combine(repo, "configs", "backtest", "orb-published-recipe-whole-market-profile.yaml"));
        var screen = PublishedResearchScreen.Read(await SyntheticScreen.PublishAsync(Path.Combine(root.Path, "screens", "screen-a"), [("2026-10-01", "ABC", "sec-abc")]));
        var end = DateTimeOffset.Parse("2026-10-01T20:00:00Z");
        var run = BacktestExecutionProfileResolver.Resolve(profile, [runtime.Definition]) with
        {
            RunName = "synthetic-research-screen",
            Provider = "synthetic-screen",
            TimeWindow = new("fixed", 1, Start, end, 1),
            Tickers = ["ABC"],
            Strategies = [strategyPath],
            RawRoot = Path.Combine(root.Path, "raw"), NormalizedRoot = Path.Combine(root.Path, "normalized"), ResultsRoot = Path.Combine(root.Path, "results"),
            CachePolicy = "use_cache",
            Universe = UniverseConfig.StaticUniverse with { Mode = UniverseConfig.ResolvedSnapshotMode, ResolvedSource = "research_screen",
                SelectedSourceTickers = ["ABC"], ResolvedAtUtc = Start, RankedMembership = new(screen.ManifestPath, screen.ManifestSha256) },
            Artifacts = new("full")
        };
        var provider = new ScreenRunProvider(run.RawRoot, minutes, omitSessionsBack);
        var calendar = await provider.LoadMarketSessionSchedulesAsync(new(2026, 8, 1), new(2026, 10, 1), default);
        run = BacktestSessionContextResolver.IncludeCompletedSessions(run, Start, calendar, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), 20);
        var request = new PreparedMarketDataRequest(1, "synthetic-research-screen", end, [new(run.RunName, run with { Strategies = [] }, [runtime.Definition])]);
        var reference = await PreparedMarketDataService.WriteRequestAsync(request, Path.Combine(root.Path, "admission", "request.json"), default);
        var runner = new BacktestRunner(reader, artifacts, marketDataProviderFactory: _ => provider, intervalTradePrintSourceFactory: _ => trades);

        var result = await runner.RunAsync(run, [runtime.Definition], DateTimeOffset.UtcNow, Path.Combine(run.ResultsRoot, "result.json"), default,
            preparedDataReference: reference);

        verify(result, root.Path);
    }

    // Answers every trade request with no prints (the gap stays unresolved) and records the requested ranges.
    private sealed class RecordingTradeSource : IIntervalTradePrintSource
    {
        public List<(DateTimeOffset Start, DateTimeOffset End)> Requests { get; } = [];

        public Task<IntervalTradePrints?> GetTradesAsync(string ticker, DateTimeOffset startUtc, DateTimeOffset endUtc, DateOnly sessionDate,
            CancellationToken cancellationToken)
        {
            Requests.Add((startUtc, endUtc));
            return Task.FromResult<IntervalTradePrints?>(new([], "synthetic-trades"));
        }
    }

    /// <summary>
    /// Archives complete synthetic as-traded sessions (twenty prior regular sessions plus 1 October) with daily bars and the
    /// given one-minute bars, like the evidence catalog serves them; no network and no real data.
    /// </summary>
    private sealed class ScreenRunProvider(string rawRoot, IReadOnlyList<OhlcvBar> minutes, int? omitSessionsBack) : IMarketDataProvider,
        IMarketSessionScheduleProvider, IMarketDataProvenanceProvider, IMarketDataCacheSourceDescriptorProvider, IHistoricalMarketDataEvidenceProvider
    {
        private readonly IRawArchiveWriter archive = new FileSystemRawArchiveWriter(new(rawRoot));
        private readonly IReadOnlyList<DateOnly> sessionDates = SessionDates();
        public MarketDataProvenance MarketDataProvenance => new("synthetic-screen-v1", "sip", "all");
        public MarketDataCacheSourceDescriptor CacheSourceDescriptor => new("synthetic-screen-v1", "sip", "all", "synthetic-v1");
        public string HistoricalEndpointIdentity => "synthetic://screen-bars-v1";
        public string HistoricalParserVersion => "synthetic-screen-v1";
        public string HistoricalCalendarIdentity => "synthetic://twenty-prior-plus-current-sessions-v1";

        /// <summary>Captures exactly the requested bars and archives them as a fixture receipt.</summary>
        public async Task<HistoricalBarsCapture> CaptureHistoricalBarsAsync(string ticker, string timeframe, DateTimeOffset startUtc,
            DateTimeOffset endUtc, long maximumBars, CancellationToken cancellationToken)
        {
            var started = DateTimeOffset.UtcNow;
            var bars = new List<OhlcvBar>();
            await foreach (var bar in GetBarsAsync([ticker], [timeframe], startUtc, endUtc, cancellationToken)) bars.Add(bar);
            return new(bars, await ArchiveAsync("bars", bars, started, cancellationToken));
        }

        /// <summary>Captures every requested calendar date, closed dates included.</summary>
        public async Task<HistoricalCalendarCapture> CaptureHistoricalCalendarAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken)
        {
            var started = DateTimeOffset.UtcNow;
            var sessions = await LoadMarketSessionSchedulesAsync(startDate, endDate, cancellationToken);
            return new(sessions, await ArchiveAsync("calendar", sessions, started, cancellationToken));
        }

        /// <summary>Daily, five-minute and one-minute bars of the fixture inside [start, end).</summary>
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var timeframe in timeframes)
            foreach (var bar in Bars(timeframe).Where(bar => tickers.Contains(bar.Ticker) && bar.Timestamp >= start && bar.Timestamp < end))
                yield return bar;
        }

        /// <summary>Every declared trading date plus explicit closed dates.</summary>
        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(DateOnly startDateInclusive,
            DateOnly endDateInclusive, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>(
                Enumerable.Range(startDateInclusive.DayNumber, endDateInclusive.DayNumber - startDateInclusive.DayNumber + 1)
                    .Select(DateOnly.FromDayNumber).ToDictionary(date => date, date =>
                        sessionDates.Contains(date) ? MarketSessionSchedule.RegularDay(date) : MarketSessionSchedule.Closed(date)));

        // The fixture's bars of one timeframe, with the optional omitted five-minute bar.
        private IEnumerable<OhlcvBar> Bars(string timeframe)
        {
            if (timeframe == "1m") return minutes;
            if (timeframe == "1d")
                return sessionDates.Select(date => new DateTimeOffset(date.ToDateTime(new TimeOnly(4, 0), DateTimeKind.Utc)))
                    .Select(at => new OhlcvBar("ABC", at, "1d", 99m, 100.2m, 95m, 99m, 7_800_000m, "sip", "all"));
            Assert.Equal("5m", timeframe);
            DateTimeOffset? omitted = omitSessionsBack is { } back
                ? new DateTimeOffset(sessionDates[sessionDates.Count - 1 + back].ToDateTime(new TimeOnly(15, 0), DateTimeKind.Utc)) : null;
            return sessionDates.SelectMany(date => Enumerable.Range(0, 78).Select(slot =>
            {
                var at = new DateTimeOffset(date.ToDateTime(new TimeOnly(13, 30), DateTimeKind.Utc)).AddMinutes(slot * 5);
                var current = date == new DateOnly(2026, 10, 1);
                // On 1 October: the opening range (slot 0), the entry bar (slot 1) and later bars that stay above the stop.
                var (open, high, low, close) = !current ? (99m, 100m, 95m, 99m) : slot switch
                {
                    < 2 => (99m, 100m, 98m, 99.5m),
                    _ => (100.1m, 100.2m, 99.8m, 100.1m)
                };
                return new OhlcvBar("ABC", at, "5m", open, high, low, close, current && slot == 0 ? 200000m : 100000m, "sip", "all");
            })).Where(bar => bar.Timestamp != omitted);
        }

        // Twenty prior weekday sessions plus 1 October 2026.
        private static IReadOnlyList<DateOnly> SessionDates()
        {
            var current = new DateOnly(2026, 10, 1);
            var dates = new List<DateOnly> { current };
            for (var date = current.AddDays(-1); dates.Count < 21; date = date.AddDays(-1))
                if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) dates.Add(date);
            return dates.Order().ToArray();
        }

        // Labels synthetic bytes and actual collection clocks rather than asserting historical provider receipts.
        private async Task<HistoricalQueryEvidence> ArchiveAsync<T>(string kind, T payload, DateTimeOffset started, CancellationToken token)
        {
            var receipt = await archive.ArchiveAsync(new("synthetic-screen-v1", kind, DateTimeOffset.UtcNow, "json", "application/json",
                Attributes: new Dictionary<string, string> { ["syntheticFixture"] = "true" }), JsonSerializer.SerializeToUtf8Bytes(payload), token);
            return new(started, DateTimeOffset.UtcNow, true, [receipt]);
        }
    }
}

using System.Runtime.CompilerServices;
using System.Text.Json;
using TradingFlow.Backtesting;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Data.Csv;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed partial class IntradayBacktestIntegrationTests
{
    /// <summary>The ordinary prepared runner retains all ORB versions, fractional source evidence, actual-fill 3R and monthly identity.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PreparedRunner_PublishesVerifiedIntradayArtifactsAndMonthlyResults(bool outwardRounding, bool completedBreakout)
    {
        var root = Path.Combine(Path.GetTempPath(), "tf-intraday-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = TestRepository.FindRoot();
            var reader = new SimpleYamlReader();
            var artifacts = new StrategyArtifactCatalog(repo, Path.Combine(repo, "configs", "strategy-catalog.json"), reader);
            var strategyPath = Path.Combine(repo, "configs", "backtest", "strategies", "opening-range-breakout.bt-v3.yaml");
            if (!completedBreakout) strategyPath = Path.Combine(repo, "src", "TradingFlow.Tests", "Fixtures", "Strategies",
                outwardRounding ? "opening-range-breakout-stop-market.v2.fixture.yaml" : "opening-range-breakout-stop-market.v1.fixture.yaml");
            var runtime = !completedBreakout
                ? StrategyRunArtifactValidator.CreateDiagnosticRuntimeStrategy(reader.ReadStrategy(strategyPath)) // The stop-market path has no catalog identity.
                : new StrategyRunArtifactValidator(reader, artifacts).ReadAndValidateRuntimeArtifact(strategyPath, StrategySelectionMode.Backtest);
            var profile = reader.ReadBacktestRun(Path.Combine(repo, "configs", "backtest", "swing-backtest-profile.yaml"));
            var start = DateTimeOffset.Parse("2026-10-01T13:30:00Z");
            var end = DateTimeOffset.Parse("2026-10-01T20:00:00Z");
            var run = BacktestExecutionProfileResolver.Resolve(profile, [runtime.Definition]) with
            {
                RunName = "synthetic-intraday-integration",
                Provider = "synthetic-intraday",
                TimeWindow = new("fixed", 1, start, end, 1),
                Tickers = ["ABC"],
                Strategies = [strategyPath],
                RawRoot = Path.Combine(root, "raw"), NormalizedRoot = Path.Combine(root, "normalized"), ResultsRoot = Path.Combine(root, "results"),
                CachePolicy = "use_cache",
                Universe = UniverseConfig.StaticUniverse with { Mode = UniverseConfig.ResolvedSnapshotMode,
                    ResolvedSource = "synthetic_fixture", SelectedSourceTickers = ["ABC"], ResolvedAtUtc = start },
                Portfolio = profile.Portfolio with { MaxConcurrentPositions = 1, PreventOverlappingTickerPositions = true },
                Artifacts = new("full")
            };
            var provider = new SyntheticIntradayProvider(run.RawRoot, outwardRounding);
            var calendar = await provider.LoadMarketSessionSchedulesAsync(new(2026, 8, 1), new(2026, 10, 1), default);
            run = BacktestSessionContextResolver.IncludeCompletedSessions(run, start, calendar,
                TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), 20);
            var request = new PreparedMarketDataRequest(1, "synthetic-intraday-integration", end,
                [new(run.RunName, run with { Strategies = [] }, [runtime.Definition])]);
            var reference = await PreparedMarketDataService.WriteRequestAsync(request, Path.Combine(root, "admission", "request.json"), default);
            var runner = new BacktestRunner(reader, artifacts, marketDataProviderFactory: _ => provider);
            var result = await runner.RunAsync(run, [runtime.Definition], DateTimeOffset.UtcNow,
                Path.Combine(run.ResultsRoot, "result.json"), default, preparedDataReference: reference);
            Assert.True(result.EconomicResultsComplete);
            Assert.NotNull(result.PreparedMarketData);
            Assert.Equal(new[] { "all" }, result.PreparedMarketData.PriceAdjustments);
            var trade = Assert.Single(result.CompletedTrades);
            Assert.Equal(start.AddMinutes(completedBreakout ? 20 : 15), trade.EntryTimestamp);
            Assert.Equal(start.AddHours(6).AddMinutes(25), trade.ExitTimestamp);
            Assert.Equal("session_deadline", trade.ExitReason);
            Assert.True(trade.ShareQuantity > 0);
            Assert.True(trade.Fees > 0);
            var state = Assert.IsType<BacktestPortfolioState>(result.PortfolioState);
            Assert.Equal(IntradayPortfolioExecution.ValuationBasis, state.ValuationBasis);
            Assert.Empty(state.OpenPositions);
            Assert.Empty(state.PendingOrders);
            Assert.Equal(1, state.ClosedTradeCount);
            Assert.Equal(2, state.Fills.Count);
            Assert.All(state.Fills, fill => Assert.Equal(new DateOnly(2026, 10, 1), fill.SourceSessionDate));
            Assert.Equal(1, provider.BarCaptures);
            Assert.Equal(21 * 78, provider.CapturedBars);
            Assert.All(provider.RequestedTimeframes, timeframe => Assert.Equal("5m", timeframe));
            await BacktestArtifactIntegrity.VerifyManifestAsync(result.ArtifactManifestPath!);
            await AssertOpeningRangeArtifactsAsync(result, state, start, outwardRounding, completedBreakout);
            // Monthly publication requires a selectable catalog identity, which the stop-market variant does not have.
            if (!completedBreakout) return;
            var saved = JsonSerializer.Deserialize<BacktestResult>(await File.ReadAllTextAsync(result.ResultPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var identity = new StrategyReference(runtime.Identity.StrategyId, runtime.Identity.SemanticVersion, runtime.Identity.ContentSha256);
            var report = new MonthlyRunResultWriter(new StrategyRunArtifactValidator(reader, artifacts))
                .Create(run, saved, end, "synthetic-cohort", [identity]);
            Assert.Null(report.UnavailableReason);
            var month = Assert.Single(report.Results);
            Assert.Equal("2026-10", month.Month);
            Assert.Equal(identity, month.Strategy);
            Assert.Equal(1, month.ClosedTrades);
            Assert.Equal(run.Portfolio.StartingCapital, month.OpeningEquity);
            Assert.Equal(state.Sessions[^1].Equity, month.ClosingEquity);
            Assert.Equal(state.Sessions[^1].Fees, month.Fees);
        }
        finally
        {
            // These files are isolated synthetic fixtures; verify ownership before recursive cleanup.
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(root), ignoreCase: true);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// ORB 2.0.0 (published recipe) through the ordinary prepared runner. Fixture: twenty prior sessions with daily range
    /// 95-100 (daily ATR 5) and 100,000 shares per bar; the current first bar opens 99, closes 99.5 (long), high 100, on
    /// 200,000 shares (relative volume 2). Expected: one stop order at 100 placed at 09:35, protective stop 100 - 0.1 x 5 =
    /// 99.50, no target; the next bar (09:35-09:40) trades 98-100, so the order fills inside it (stamped 09:40) and the stop is hit.
    /// </summary>
    [Fact]
    public async Task PublishedRecipe_PlacesOneStopOrderAtTheRangeWithAtrStopAndNoTarget()
    {
        await WithRecipeRunAsync(null, (result, _) =>
        {
            Assert.True(result.EconomicResultsComplete);
            var trade = Assert.Single(result.CompletedTrades);
            Assert.Equal("long", trade.Direction);
            // The 09:35-09:40 bar trades through the trigger after its open; an intrabar stop fill is stamped at that bar's end.
            Assert.Equal(DateTimeOffset.Parse("2026-10-01T13:40:00Z"), trade.EntryTimestamp);
            Assert.Equal(99.5m, trade.StopLossPrice);
            Assert.Equal(0m, trade.TakeProfitPrice);
            // Entry and stop fall in the same five-minute bar: counted as a loss and labelled because their order is unknown.
            Assert.Equal("stop_loss_entry_bar_order_unknown", trade.ExitReason);
            Assert.Equal(1, result.CandidateTradeCount);
        });
    }

    // A saved plan's daily eligibility reaches the five-minute ORB replay: the stock (prior close $99, 7.8M shares a day)
    // trades when it passes, and its candidate is refused as universe_membership_missing when the minimum price is $200.
    [Theory]
    [InlineData(5, true)]
    [InlineData(200, false)]
    public async Task PublishedRecipe_TradesOnlyOnDaysTheStockIsEligible(int minPrice, bool eligible)
    {
        var rules = new DailyScreenRules(minPrice, DailyScreenPriceBasis.PriorClose, 5, MinAverageShareVolume: 1_000_000m, MinHistorySessions: 5);
        await WithRecipeRunAsync(rules, (result, root) =>
        {
            Assert.True(result.EconomicResultsComplete);
            Assert.Equal(1, result.CandidateTradeCount);
            Assert.Equal(eligible ? 1 : 0, result.CompletedTrades.Count);
            var refusals = result.UnifiedPortfolio!.AdmissionRejectionCounts ?? new Dictionary<string, int>();
            Assert.Equal(eligible ? 0 : 1, refusals.GetValueOrDefault("portfolio_universe_membership_missing"));
            // The published record lists the days the stock qualified on, so each refusal can be checked afterwards.
            var record = Assert.Single(Directory.EnumerateFiles(root, "synthetic-orb-recipe-daily-eligibility.json", SearchOption.AllDirectories));
            using var json = JsonDocument.Parse(File.ReadAllText(record));
            var days = json.RootElement.GetProperty("eligibleDays").GetProperty("ABC").EnumerateArray().Select(day => day.GetString()).ToArray();
            Assert.Equal(eligible ? "2026-10-01" : "none", days.SingleOrDefault() ?? "none");
        });
    }

    // Runs the published ORB recipe on one synthetic stock and session through the ordinary prepared runner, optionally with
    // a plan's daily eligibility rules on its resolved stock list, then deletes the isolated temporary folder.
    private static async Task WithRecipeRunAsync(DailyScreenRules? eligibility, Action<BacktestResult, string> verify)
    {
        var root = Path.Combine(Path.GetTempPath(), "tf-orb-recipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = TestRepository.FindRoot();
            var reader = new SimpleYamlReader();
            var artifacts = new StrategyArtifactCatalog(repo, Path.Combine(repo, "configs", "strategy-catalog.json"), reader);
            var strategyPath = Path.Combine(repo, "configs", "backtest", "strategies", "opening-range-breakout.bt-v4.yaml");
            var runtime = new StrategyRunArtifactValidator(reader, artifacts).ReadAndValidateRuntimeArtifact(strategyPath, StrategySelectionMode.Backtest);
            var profile = reader.ReadBacktestRun(Path.Combine(repo, "configs", "backtest", "swing-backtest-profile.yaml"));
            var start = DateTimeOffset.Parse("2026-10-01T13:30:00Z");
            var end = DateTimeOffset.Parse("2026-10-01T20:00:00Z");
            var run = BacktestExecutionProfileResolver.Resolve(profile, [runtime.Definition]) with
            {
                RunName = "synthetic-orb-recipe",
                Provider = "synthetic-intraday",
                TimeWindow = new("fixed", 1, start, end, 1),
                Tickers = ["ABC"],
                Strategies = [strategyPath],
                RawRoot = Path.Combine(root, "raw"), NormalizedRoot = Path.Combine(root, "normalized"), ResultsRoot = Path.Combine(root, "results"),
                CachePolicy = "use_cache",
                Universe = UniverseConfig.StaticUniverse with { Mode = UniverseConfig.ResolvedSnapshotMode,
                    ResolvedSource = "synthetic_fixture", SelectedSourceTickers = ["ABC"], ResolvedAtUtc = start, DailyEligibility = eligibility },
                Portfolio = profile.Portfolio with { MaxConcurrentPositions = 1, PreventOverlappingTickerPositions = true },
                Artifacts = new("full")
            };
            Assert.Equal(ExecutionReplayPolicy.IntradayRthFiveMinuteStopMarketV1, run.Engine.ExecutionReplayPolicyId);
            var provider = new SyntheticIntradayProvider(run.RawRoot, outwardRounding: false);
            var calendar = await provider.LoadMarketSessionSchedulesAsync(new(2026, 8, 1), new(2026, 10, 1), default);
            run = BacktestSessionContextResolver.IncludeCompletedSessions(run, start, calendar,
                TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), 20);
            var request = new PreparedMarketDataRequest(1, "synthetic-orb-recipe", end,
                [new(run.RunName, run with { Strategies = [] }, [runtime.Definition])]);
            var reference = await PreparedMarketDataService.WriteRequestAsync(request, Path.Combine(root, "admission", "request.json"), default);
            var runner = new BacktestRunner(reader, artifacts, marketDataProviderFactory: _ => provider);

            var result = await runner.RunAsync(run, [runtime.Definition], DateTimeOffset.UtcNow,
                Path.Combine(run.ResultsRoot, "result.json"), default, preparedDataReference: reference);

            verify(result, root);
        }
        finally
        {
            // These files are isolated synthetic fixtures; verify ownership before recursive cleanup.
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(root), ignoreCase: true);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Reads retained ordinary artifacts to prove that the shared decision, typed stop intent and real fills survived publication.</summary>
    private static async Task AssertOpeningRangeArtifactsAsync(BacktestResult result, BacktestPortfolioState state,
        DateTimeOffset start, bool outwardRounding, bool completedBreakout)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var hypotheses = JsonSerializer.Deserialize<BacktestExecutionAuditEvent[]>(
            await File.ReadAllTextAsync(result.CandidateHypothesisAuditPath!), options)!;
        Assert.NotEmpty(hypotheses);
        var candidate = hypotheses.Select(item => JsonSerializer.Deserialize<BacktestCandidateTrade>(item.EvidenceJson!, options)!)
            .Single(item => item.CandidateId == result.CompletedTrades.Single().CandidateId);
        Assert.Equal(completedBreakout ? ExecutionReplayPolicy.IntradayRthFiveMinuteNextBarMarketV1 : ExecutionReplayPolicy.IntradayRthFiveMinuteStopMarketV1, candidate.ExecutionReplay!.PolicyId);
        var intent = Assert.IsType<IntradayExecutionEvidence>(candidate.IntradayExecution);
        Assert.Equal(completedBreakout ? IntradayEntryOrderMode.NextBarMarket : IntradayEntryOrderMode.StopMarket, intent.EntryOrderMode);
        Assert.Equal(start.AddMinutes(15), intent.OpeningRangeCompletedAtUtc);
        Assert.Equal(2m, intent.OpeningRelativeVolume);
        Assert.Equal(20, intent.OpeningVolumeBaselineSampleCount);
        Assert.Equal(outwardRounding ? 100.0051m : 100m, intent.OpeningRangeHigh);
        Assert.Equal(outwardRounding ? 97.9976m : 98m, intent.OpeningRangeLow);
        Assert.Equal(completedBreakout ? 100.1m : outwardRounding ? 100.02m : 100.01m, intent.LimitPrice);
        Assert.Equal(completedBreakout ? 99.5m : outwardRounding ? 97.99m : 98m, candidate.StopLossPrice);
        Assert.True(state.Fills.Single(fill => fill.IsEntry).Price >= intent.LimitPrice);
        var trade = Assert.Single(result.CompletedTrades);
        var originalRisk = trade.EntryPrice - candidate.StopLossPrice;
        var expectedTarget = decimal.Ceiling((trade.EntryPrice + 3m * originalRisk) / intent.TickSize) * intent.TickSize;
        Assert.Equal(expectedTarget, trade.TakeProfitPrice);
        Assert.True(trade.ShareQuantity * intent.LimitPrice <= 2500m);
        // The ordinary latest-fifty display may omit the early filled hypothesis; complete retained journals above preserve it.
        var detail = state.IntradayDecisions.First();
        Assert.Equal(completedBreakout ? "next_bar_market" : "stop_market", detail.EntryOrderMode);
        Assert.Equal(2m, detail.OpeningRelativeVolume);
        Assert.Equal(20, detail.OpeningVolumeBaselineSampleCount);
        var executions = JsonSerializer.Deserialize<BacktestExecutionAuditEvent[]>(
            await File.ReadAllTextAsync(result.PortfolioExecutionAuditPath!), options)!;
        Assert.Contains(executions, item => item.State == "OrderFilled" && item.ReferenceId == candidate.CandidateId);
        using var decisions = JsonDocument.Parse(await File.ReadAllTextAsync(result.CandidateDecisionAuditPath!));
        Assert.True(decisions.RootElement.GetArrayLength() > 0);
        Assert.Contains("opening_range_breakout", decisions.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>Archives complete synthetic sessions: twenty prior regular sessions plus the current opening-range session.</summary>
    private sealed class SyntheticIntradayProvider(string rawRoot, bool outwardRounding, string adjustmentPolicy = "all") : IMarketDataProvider, IMarketSessionScheduleProvider,
        IMarketDataProvenanceProvider, IMarketDataCacheSourceDescriptorProvider, IHistoricalMarketDataEvidenceProvider
    {
        private readonly IRawArchiveWriter archive = new FileSystemRawArchiveWriter(new(rawRoot));
        private readonly IReadOnlyList<DateOnly> sessionDates = SessionDates();
        public int BarCaptures { get; private set; }
        public int CapturedBars { get; private set; }
        public List<string> RequestedTimeframes { get; } = [];
        public MarketDataProvenance MarketDataProvenance => new("synthetic-intraday-v1", "sip", adjustmentPolicy);
        public MarketDataCacheSourceDescriptor CacheSourceDescriptor => new("synthetic-intraday-v1", "sip", adjustmentPolicy, "synthetic-v1");
        public string HistoricalEndpointIdentity => "synthetic://intraday-bars-v1";
        public string HistoricalParserVersion => "synthetic-intraday-v1";
        public string HistoricalCalendarIdentity => "synthetic://twenty-prior-plus-current-sessions-v1";

        /// <summary>Captures exactly the requested bars and retains a real fixture receipt, with no historical-receipt claim.</summary>
        public async Task<HistoricalBarsCapture> CaptureHistoricalBarsAsync(string ticker, string timeframe,
            DateTimeOffset startUtc, DateTimeOffset endUtc, long maximumBars, CancellationToken cancellationToken)
        {
            var started = DateTimeOffset.UtcNow;
            var bars = new List<OhlcvBar>();
            await foreach (var bar in GetBarsAsync([ticker], [timeframe], startUtc, endUtc, cancellationToken))
            {
                if (bars.Count >= maximumBars) throw new InvalidOperationException("Synthetic source exceeds its accepted row allowance.");
                bars.Add(bar);
            }
            BarCaptures++;
            CapturedBars += bars.Count;
            return new(bars, await ArchiveAsync("bars", bars, started, cancellationToken));
        }

        /// <summary>Captures every requested calendar date, preserving closed dates as explicit fixture evidence.</summary>
        public async Task<HistoricalCalendarCapture> CaptureHistoricalCalendarAsync(DateOnly startDate, DateOnly endDate,
            CancellationToken cancellationToken)
        {
            var started = DateTimeOffset.UtcNow;
            var sessions = await LoadMarketSessionSchedulesAsync(startDate, endDate, cancellationToken);
            return new(sessions, await ArchiveAsync("calendar", sessions, started, cancellationToken));
        }

        /// <summary>Provides full session bars with a high-volume opening range on the current session.</summary>
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var timeframe in timeframes)
            {
                RequestedTimeframes.Add(timeframe);
                if (timeframe == "1d")
                {
                    // Daily candles for the daily eligibility check: one per session dated midnight New York, matching the
                    // regular-session five-minute candles below (78 bars of 100,000 shares, open and close $99).
                    foreach (var ticker in tickers)
                    foreach (var date in sessionDates)
                    {
                        var at = new DateTimeOffset(date.ToDateTime(new TimeOnly(4, 0), DateTimeKind.Utc));
                        if (at < start || at >= end) continue;
                        yield return new(ticker, at, timeframe, 99m, 100.2m, 95m, 99m, 7_800_000m, "sip", adjustmentPolicy, at.AddHours(16), end);
                    }
                    continue;
                }
                Assert.Equal("5m", timeframe);
                foreach (var ticker in tickers)
                foreach (var date in sessionDates)
                for (var slot = 0; slot < 78; slot++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var at = new DateTimeOffset(date.ToDateTime(new TimeOnly(13, 30), DateTimeKind.Utc)).AddMinutes(slot * 5);
                    if (at < start || at >= end) continue;
                    var current = date == new DateOnly(2026, 10, 1);
                    var (open, high, low, close) = !current ? (99m, 100m, 95m, 99m) : slot switch {
                        < 3 => (99m, outwardRounding ? 100.0051m : 100m, outwardRounding ? 97.9976m : 98m, 99.5m),
                        3 => (100.05m, 100.2m, 99.5m, 100.1m),
                        _ => (100.1m, 100.2m, 99.8m, 100.1m)
                    };
                    var volume = current && slot < 3 ? 200000m : 100000m;
                    yield return new(ticker, at, timeframe, open, high, low, close, volume, "sip", adjustmentPolicy, at.AddMinutes(5), end);
                }
            }
        }

        /// <summary>Returns every declared synthetic trading date plus explicit closed dates, so baseline selection never guesses weekdays.</summary>
        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(DateOnly startDateInclusive,
            DateOnly endDateInclusive, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>(
                Enumerable.Range(startDateInclusive.DayNumber, endDateInclusive.DayNumber - startDateInclusive.DayNumber + 1)
                    .Select(DateOnly.FromDayNumber).ToDictionary(date => date, date =>
                        sessionDates.Contains(date)
                            ? MarketSessionSchedule.RegularDay(date) : MarketSessionSchedule.Closed(date)));

        /// <summary>Declares exactly twenty completed prior sessions for the ORB volume baseline.</summary>
        private static IReadOnlyList<DateOnly> SessionDates()
        {
            var current = new DateOnly(2026, 10, 1);
            var dates = new List<DateOnly> { current };
            for (var date = current.AddDays(-1); dates.Count < 21; date = date.AddDays(-1))
                if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) dates.Add(date);
            return dates.Order().ToArray();
        }

        /// <summary>Labels synthetic bytes and actual collection clocks rather than asserting historical provider receipts.</summary>
        private async Task<HistoricalQueryEvidence> ArchiveAsync<T>(string kind, T payload, DateTimeOffset started, CancellationToken token)
        {
            var receipt = await archive.ArchiveAsync(new("synthetic-intraday-v1", kind, DateTimeOffset.UtcNow, "json", "application/json",
                Attributes: new Dictionary<string, string> { ["syntheticFixture"] = "true", ["originalHistoricalReceiptClaim"] = "false" }),
                JsonSerializer.SerializeToUtf8Bytes(payload), token);
            return new(started, DateTimeOffset.UtcNow, true, [receipt]);
        }
    }
}

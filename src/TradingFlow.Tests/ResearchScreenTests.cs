using System.Runtime.CompilerServices;
using Moq;
using TradingFlow.Cli;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Universe;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;

namespace TradingFlow.Tests;

/// <summary>
/// The whole-market research screen as a backtest stock source (design docs/design-review/2026-11-10/orb-stocks-in-play-full-test-design.md,
/// Part 1): the published manifest and its fingerprints, the read-only preview, and membership parity with the in-run ranking.
/// Every screen here is a synthetic fixture in its own temporary folder.
/// </summary>
public sealed class ResearchScreenTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DailyScreenRules Rules = new(5m, DailyScreenPriceBasis.SessionOpen, 14,
        MinAverageShareVolume: 1_000_000m, AtrPeriod: 14, MinAtr: 0.5m, MinHistorySessions: 14);
    private static readonly DailyRankingRules Ranking = new(DailyRankingRules.FirstFiveMinuteRelativeVolume, 14, VolumeBaselineAverage.Mean, 1m, 2);

    // The manifest names every input dataset, rule value and output fingerprint; one changed byte in the ranked list, or a
    // manifest other than the one a plan recorded, is refused.
    [Fact]
    public async Task Manifest_FingerprintsEveryOutputAndAChangedRankedListIsRefused()
    {
        using var root = new TemporaryFolder();
        var folder = await SyntheticScreen.PublishAsync(Path.Combine(root.Path, "screen-a"), [("2026-10-01", "ABC", "sec-abc")]);

        var screen = PublishedResearchScreen.Read(folder);

        Assert.Equal(new ResearchScreenInputs("daily", "actions", "opening", "sessions"), screen.Manifest.Inputs);
        Assert.Equal(ResearchScreenRuleValues.From(Rules), screen.Manifest.ScreenRules);
        Assert.Equal(ResearchRankingRuleValues.From(Ranking), screen.Manifest.RankingRules);
        Assert.Equal(await ResearchScreenManifest.Sha256FileAsync(Path.Combine(folder, "ranked-daily-list.csv"), default), screen.RankedListSha256);
        Assert.Equal(["ranked-daily-counts.csv", "ranked-daily-exclusions.csv", "ranked-daily-list.csv"], screen.Manifest.Outputs.Keys.Order());
        var wrongPlan = Assert.Throws<InvalidDataException>(() => PublishedResearchScreen.Read(folder, new string('0', 64)));
        Assert.Contains("ranked list changed since the plan was saved", wrongPlan.Message, StringComparison.Ordinal);
        await File.AppendAllTextAsync(Path.Combine(folder, "ranked-daily-list.csv"), "2026-10-02,1,XYZ,sec-xyz,alpaca_security_id,1,1,1,0\n");
        Assert.Throws<InvalidDataException>(() => PublishedResearchScreen.Read(folder));
        Assert.Throws<InvalidDataException>(() => PublishedResearchScreen.Read(folder, screen.ManifestSha256));
    }

    // Previewing a research screen only reads its files: it lists every ranked stock with the manifest and ranked-list
    // fingerprints and never touches a wishlist or the Finviz screener. Only folders with a ranked list are offered.
    [Fact]
    public async Task Preview_ReadsTheScreenAndWritesNothingToOperatorLists()
    {
        using var root = new TemporaryFolder();
        var paths = new ProjectPaths(root.Path, root.Path, Path.Combine(root.Path, "cache"));
        var folder = await SyntheticScreen.PublishAsync(Path.Combine(paths.ResearchScreensRoot, "screen-a"),
            [("2026-10-01", "ABC", "sec-abc"), ("2026-10-02", "ABC", "sec-abc"), ("2026-10-02", "XYZ", "sec-xyz")]);
        Directory.CreateDirectory(Path.Combine(paths.ResearchScreensRoot, "not-published"));
        var wishlists = new Mock<IWishlistRepository>(MockBehavior.Strict);
        var screener = new Mock<IScreenerSnapshotSource>(MockBehavior.Strict);
        var screens = new ResearchScreenSources(paths);
        var resolver = new UniverseSourceResolver(wishlists.Object, screener.Object, TimeProvider.System, screens);

        var capture = await resolver.ResolveAsync(new(DiscoverySourceKinds.ResearchScreen, ResearchScreenSources.KeyPrefix + "screen-a"), "day");

        Assert.Equal(["screen-a"], screens.List());
        Assert.Equal(DiscoverySourceKinds.ResearchScreen, capture.SourceKind);
        Assert.Equal(["ABC", "XYZ"], capture.Symbols.Select(item => item.Symbol));
        var screen = PublishedResearchScreen.Read(folder);
        Assert.Equal(("screen-a", screen.ManifestSha256, screen.RankedListSha256), ResearchScreenSources.ParseProviderReference(capture.ProviderReference));
        Assert.Equal(["ABC"], screen.PoolSymbols(new(2026, 10, 1), new(2026, 10, 1)));
        Assert.Throws<InvalidOperationException>(() => screen.PoolSymbols(new(2026, 9, 30), new(2026, 10, 2)));
        Assert.Throws<InvalidOperationException>(() => screen.ToMembership(new(2026, 10, 1), new(2026, 10, 5)));
        wishlists.VerifyNoOtherCalls();
        screener.VerifyNoOtherCalls();
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(new(DiscoverySourceKinds.ResearchScreen, "..\\screen-a"), "day"));
    }

    // The datasets a research-screen run reads: the manifest's daily bars and sessions, plus the five-minute and one-minute
    // datasets collected for this ranked list; a list without five-minute bars or a manifest without sessions is refused.
    [Fact]
    public async Task DatasetReference_UsesOnlyDatasetsCollectedForTheRankedList()
    {
        using var root = new TemporaryFolder();
        var screen = PublishedResearchScreen.Read(await SyntheticScreen.PublishAsync(Path.Combine(root.Path, "screen-a"), [("2026-10-01", "ABC", "sec-abc")]));
        using var catalog = new SyntheticEvidenceCatalog();
        var five = await catalog.BarsAsync("5m", screen.RankedListSha256, [SyntheticEvidenceCatalog.Row("ABC", "5m", new(2026, 10, 1, 13, 30, 0, TimeSpan.Zero), 10m)]);
        var other = await catalog.BarsAsync("5m", new string('e', 64), [SyntheticEvidenceCatalog.Row("ABC", "5m", new(2026, 10, 1, 13, 35, 0, TimeSpan.Zero), 10m)]);
        var minute = await catalog.BarsAsync("1m", screen.RankedListSha256, [SyntheticEvidenceCatalog.Row("ABC", "1m", new(2026, 10, 1, 13, 30, 0, TimeSpan.Zero), 10m)]);

        var reference = ResearchScreenSources.ResolveReference(screen, [five, other, minute]);

        Assert.Equal(new[] { five.DatasetId }, reference.FiveMinuteDatasetIds);
        Assert.Equal(new[] { minute.DatasetId }, reference.OneMinuteDatasetIds);
        Assert.Equal(("daily", "sessions"), (reference.DailyBarsDatasetId, reference.ExchangeSessionsDatasetId));
        Assert.Throws<InvalidOperationException>(() => ResearchScreenSources.ResolveReference(screen, [other, minute]));
        Assert.Throws<InvalidOperationException>(() => ResearchScreenSources.ResolveReference(screen, [five, other]));
    }

    // A day is left out when a split known by that day (ex-date on or before it) falls inside its look-back sessions; a split
    // after the day, or before the look-back, leaves it in.
    [Theory]
    [InlineData("2026-03-10", 3, true)]
    [InlineData("2026-03-12", 3, true)]
    [InlineData("2026-03-13", 3, false)]
    [InlineData("2026-03-09", 3, false)]
    public void SplitInLookback_LeavesOutDaysWhoseLookbackHoldsAKnownSplit(string exDate, int lookback, bool excluded)
    {
        DateOnly[] sessions = [new(2026, 3, 9), new(2026, 3, 10), new(2026, 3, 11), new(2026, 3, 12), new(2026, 3, 13), new(2026, 3, 16)];

        var result = DailyScreenCommand.SplitInLookback(sessions, new DateOnly(2026, 3, 12), [new ShareSplit(DateOnly.Parse(exDate), 2m)], lookback);

        Assert.Equal(excluded, result);
        Assert.False(DailyScreenCommand.SplitInLookback(sessions, new DateOnly(2026, 3, 12), [], lookback));
    }

    // A screen without a ranking serves daily strategies: membership is each session's eligible.csv rows, known before the
    // open, and no five-minute or one-minute dataset is required; the split rule and its count are recorded in the manifest.
    [Fact]
    public async Task EligibleListScreen_GivesDailyMembershipWithoutIntradayDatasets()
    {
        using var root = new TemporaryFolder();
        var folder = Path.Combine(root.Path, "daily-screen");
        Directory.CreateDirectory(folder);
        await File.WriteAllLinesAsync(Path.Combine(folder, "eligible.csv"), ["session_date,ticker,price,average_volume,average_dollar_volume,atr,history_sessions",
            "2026-10-01,ABC,20,2000000,40000000,1,200", "2026-10-02,ABC,20,2000000,40000000,1,200", "2026-10-02,XYZ,30,2000000,60000000,1,200"]);
        await File.WriteAllLinesAsync(Path.Combine(folder, DailyScreenCommand.SplitExclusionsFileName), ["session_date,ticker,reason", "2026-10-01,XYZ,split_in_lookback"]);
        await ResearchScreenManifest.PublishAsync(folder, new("daily", "actions", null, "sessions"), Rules with { PriceBasis = DailyScreenPriceBasis.PriorClose },
            null, new(2026, 10, 1), new(2026, 10, 2), "alpaca_security_id", null, default, new ResearchScreenSplitRule(200, 1));

        var screen = PublishedResearchScreen.Read(folder);
        var membership = screen.ToMembership(new(2026, 10, 1), new(2026, 10, 2));
        var reference = ResearchScreenSources.ResolveReference(screen, []);

        Assert.False(screen.IsRanked);
        Assert.Equal(new ResearchScreenSplitRule(200, 1), screen.Manifest.SplitRule);
        Assert.Contains("split-exclusions.csv", screen.Manifest.Outputs.Keys);
        Assert.Equal(["ABC", "XYZ"], screen.PoolSymbols(new(2026, 10, 1), new(2026, 10, 2)));
        Assert.Equal([new DateOnly(2026, 10, 2)], membership.EligibleDays("XYZ"));
        Assert.Null(membership.KnownFromNewYorkTime);
        Assert.Equal((0, 0), (reference.FiveMinuteDatasetIds.Count, reference.OneMinuteDatasetIds.Count));
        Assert.Equal(screen.Manifest.Outputs["eligible.csv"], reference.RankedListSha256);
    }

    // The evidence catalog opened read-only answers reads without changing the database file or creating a journal.
    [Fact]
    public async Task ReadOnlyCatalog_ReadsWithoutWriting()
    {
        using var root = new TemporaryFolder();
        var path = Path.Combine(root.Path, "catalog.db");
        var store = new TradingFlow.Data.Evidence.FileSystemImmutableArtifactStore(new TradingFlow.Data.Evidence.ImmutableArtifactStoreOptions(Path.Combine(root.Path, "objects")));
        var created = new TradingFlow.Data.Evidence.SqliteEvidenceCatalog(new(path, TradingFlow.Data.Evidence.EvidenceCatalogOpenMode.BootstrapNew), store);
        Assert.Null(await created.GetDatasetAsync("absent"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var before = await ResearchScreenManifest.Sha256FileAsync(path, default);

        var readOnly = new TradingFlow.Data.Evidence.SqliteEvidenceCatalog(new(path, TradingFlow.Data.Evidence.EvidenceCatalogOpenMode.ReadOnly), store);
        var connection = (string)typeof(TradingFlow.Data.Evidence.SqliteEvidenceCatalog).GetField("connectionString",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(readOnly)!;
        Assert.Equal(Microsoft.Data.Sqlite.SqliteCacheMode.Private, new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connection).Cache);
        Assert.Null(await readOnly.GetDatasetAsync("absent"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        Assert.Equal(before, await ResearchScreenManifest.Sha256FileAsync(path, default));
    }

    // A research-screen run never stops because one stock's data is unavailable: its failed work items are skipped (and
    // published); any other run keeps withholding its results for a failed item, exactly as before.
    [Fact]
    public void FailedStockWorkItems_ResearchScreensSkipOnlyNamedDataUnavailableReasons()
    {
        var profile = new TradingFlow.Engine.Configuration.SimpleYamlReader().ReadBacktestRun(
            Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "connors-whole-market-daily-profile.yaml"));
        TradingFlow.Backtesting.StrategyTickerBacktestBatch Batch(string ticker, string? error) => new(ticker, "s", "S", error is null,
            error, [], [], null, new TradingFlow.Backtesting.StrategyCandidateDiagnostics("s", "S"));
        TradingFlow.Backtesting.StrategyTickerBacktestBatch[] batches = [Batch("AAA", null),
            Batch("BBB", "market_bars_missing: No market data bars were returned for ticker BBB."),
            Batch("CCC", "insufficient_indicator_warmup: CCC 1d has 12 completed pre-evaluation bars; 260 required."),
            Batch("DDD", "market_bars_unusable: Invalid candle for DDD 1d at 2026-01-02T05:00:00.0000000+00:00: high below low"),
            Batch("EEE", "strategy_ticker_timeout_after_1800s"), Batch("FFF", "Object reference not set to an instance of an object.")];
        var screen = profile with { Universe = UniverseConfig.StaticUniverse with { Mode = UniverseConfig.ResolvedSnapshotMode,
            RankedMembership = new(Path.Combine(Path.GetTempPath(), "screen-manifest.json"), new string('a', 64)) } };

        Assert.Equal(["EEE", "FFF"], TradingFlow.Backtesting.BacktestRunner.StoppingBatches(screen, batches).Select(batch => batch.Ticker));
        Assert.Equal(["BBB", "CCC", "DDD", "EEE", "FFF"],
            TradingFlow.Backtesting.BacktestRunner.StoppingBatches(profile, batches).Select(batch => batch.Ticker));
    }

    // An overnight jump of 45% down or 90% up with no recorded split marks a suspected unrecorded split; a recorded split on
    // that day does not, and a day is left out only when the jump lies inside its look-back (never the day's own open).
    [Fact]
    public void SuspectedUnrecordedSplit_FlagsUnexplainedOvernightJumpsInsideTheLookback()
    {
        var days = Enumerable.Range(0, 10).Select(i => new DateOnly(2026, 3, 2).AddDays(i)).ToArray();
        OhlcvBar Bar(int i, decimal open, decimal close) =>
            new("ABC", new DateTimeOffset(days[i].ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), "1d", open, open, close, close, 1000m);
        var series = new[] { Bar(0, 100m, 100m), Bar(1, 54m, 54m), Bar(2, 54m, 54m), Bar(3, 103m, 103m), Bar(4, 103m, 103m),
            Bar(5, 50m, 50m), Bar(6, 50m, 50m) };

        var suspects = DailyScreenCommand.UnrecordedSplitSessions(series, [new ShareSplit(days[5], 0.5m)]);

        Assert.Equal([days[1], days[3]], suspects);
        Assert.True(DailyScreenCommand.SuspectInLookback(days, days[4], suspects, 3));
        Assert.False(DailyScreenCommand.SuspectInLookback(days, days[3], [days[3]], 3));
        Assert.False(DailyScreenCommand.SuspectInLookback(days, days[9], suspects, 3));
    }

    // An as-traded strategy refuses an eligible-list screen whose split look-back is shorter than its SMA200; 200 passes.
    [Theory]
    [InlineData(null, false)]
    [InlineData(150, false)]
    [InlineData(200, true)]
    public async Task AsTradedStrategy_NeedsASplitLookbackCoveringItsLongestIndicator(int? lookback, bool accepted)
    {
        using var root = new TemporaryFolder();
        var folder = Path.Combine(root.Path, "daily-screen");
        Directory.CreateDirectory(folder);
        await File.WriteAllLinesAsync(Path.Combine(folder, "eligible.csv"), ["session_date,ticker,price,average_volume,average_dollar_volume,atr,history_sessions",
            "2026-10-01,ABC,20,2000000,40000000,1,200"]);
        await ResearchScreenManifest.PublishAsync(folder, new("daily", "actions", null, "sessions"), Rules with { PriceBasis = DailyScreenPriceBasis.PriorClose },
            null, new(2026, 10, 1), new(2026, 10, 1), "alpaca_security_id", null, default, new ResearchScreenSplitRule(lookback, 0));
        var screen = PublishedResearchScreen.Read(folder);
        var strategy = new TradingFlow.Engine.Configuration.SimpleYamlReader().ReadStrategy(Path.Combine(TestRepository.FindRoot(), "configs",
            "backtest", "strategies", "swing-connors-rsi2-published-as-traded.bt-v9-1.yaml"));

        var refused = Record.Exception(() => TradingFlow.Backtesting.BacktestRunner.RequireSplitLookbackCoversStrategies(screen, [strategy]));

        Assert.Equal(200, TradingFlow.Backtesting.BacktestRunner.LongestIndicatorLookback(strategy));
        Assert.Equal(accepted, refused is null);
    }

    // The approved whole-market daily set-up reads back exactly: daily bars only, $100,000, 10 positions of 10% each, no
    // commission, and a replay allowance for about 2,000 stocks over 7.5 years plus warm-up.
    [Fact]
    public void ConnorsDailyProfile_ReadsBackTheApprovedRunValues()
    {
        var run = new TradingFlow.Engine.Configuration.SimpleYamlReader().ReadBacktestRun(
            Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "connors-whole-market-daily-profile.yaml"));

        Assert.Equal(["1d"], run.Intervals);
        Assert.Equal("1d", run.DerivedTimeframes.Source);
        Assert.Equal((100_000m, 10, 10m, 0m, 0m), (run.Portfolio.StartingCapital, run.Portfolio.MaxConcurrentPositions,
            run.Portfolio.MaxPositionNotionalPct, run.Portfolio.FixedBuyFee, run.Portfolio.FixedSellFee));
        Assert.Equal(5_200_000, run.Engine.MaxRetainedReplayBars);
        // Only the as-traded 7.1.0 file; the adjusted 7.0.0 file would refuse as-traded catalog bars.
        Assert.Equal(["swing-connors-rsi2-sma5-risk-managed-as-traded.bt-v7-1.yaml"], run.Strategies.Select(Path.GetFileName));
    }

    // An eligible-list screen gives each stock's first eligible session and the pool stock-sessions requested; a daily run
    // requests each stock from that day minus the warm-up, never before the run's data start.
    [Fact]
    public async Task EligibleListScreen_RequestsEachStockFromItsFirstEligibleDayMinusWarmup()
    {
        using var root = new TemporaryFolder();
        var folder = Path.Combine(root.Path, "daily-screen");
        Directory.CreateDirectory(folder);
        await File.WriteAllLinesAsync(Path.Combine(folder, "eligible.csv"), ["session_date,ticker,price,average_volume,average_dollar_volume,atr,history_sessions",
            "2026-10-01,ABC,20,2000000,40000000,1,200", "2026-10-02,ABC,20,2000000,40000000,1,200", "2026-10-02,XYZ,30,2000000,60000000,1,200"]);
        await ResearchScreenManifest.PublishAsync(folder, new("daily", "actions", null, "sessions"), Rules with { PriceBasis = DailyScreenPriceBasis.PriorClose },
            null, new(2026, 10, 1), new(2026, 10, 2), "alpaca_security_id", null, default, new ResearchScreenSplitRule(200, 0));
        var screen = PublishedResearchScreen.Read(folder);
        var windowStart = DateTimeOffset.Parse("2026-10-01T04:00:00Z");
        var windowEnd = DateTimeOffset.Parse("2026-10-03T04:00:00Z");
        var run = new TradingFlow.Engine.Configuration.SimpleYamlReader().ReadBacktestRun(
            Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "connors-whole-market-daily-profile.yaml")) with
        {
            TimeWindow = new("fixed", 1, windowStart, windowEnd, 10),
            Universe = UniverseConfig.StaticUniverse with { Mode = UniverseConfig.ResolvedSnapshotMode,
                RankedMembership = new(screen.ManifestPath, screen.ManifestSha256) }
        };

        var (first, stockSessions) = screen.EligibleSpan(new(2026, 10, 1), new(2026, 10, 2));
        var starts = TradingFlow.Backtesting.ResearchScreenDataWindows.Starts(run, windowStart.AddDays(-10), windowStart, windowEnd);

        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), 3L), (first["ABC"], first["XYZ"], stockSessions));
        Assert.Equal(windowStart.AddDays(-10), starts["ABC"]);
        Assert.Equal(windowStart.AddDays(-9), starts["XYZ"]);
    }

    // The five-minute variant of the minute planner asks for the full 09:30-16:00 session of each selected stock-session and
    // of the stock's own earlier traded sessions.
    [Fact]
    public void SelectedSessionPlan_RequestsFullSessionsOfSelectedAndPriorTradedSessions()
    {
        DateOnly[] sessions = [new(2026, 9, 28), new(2026, 9, 29), new(2026, 9, 30), new(2026, 10, 1)];
        var traded = new Dictionary<string, (string, DateOnly[])> { ["ABC"] = ("sec-abc", sessions) };
        var selected = new Dictionary<DateOnly, IReadOnlyCollection<string>> { [sessions[3]] = ["ABC"] };

        var needed = IntradayBarsCollectionPlanCommand.WithBaselineSessions(selected, traded, 2);
        var request = IntradayBarsCollectionPlanCommand.CreateRequest(needed, IntradayBarsCollectionPlanCommand.RegularSessionFiveMinutes, 50,
            new Dictionary<string, string> { ["ranked_list_sha256"] = new string('a', 64) });

        Assert.Equal(sessions[1..], needed.Keys.Order());
        var first = request.Requests.Single(item => item.AsOfDate == sessions[1]);
        Assert.Equal((new DateTimeOffset(2026, 9, 29, 13, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 29, 20, 0, 0, TimeSpan.Zero), "5Min"),
            (first.RequestedStartUtc!.Value, first.RequestedEndUtc!.Value, first.Parameters["timeframe"]));
        Assert.Equal(new string('a', 64), request.Attributes["ranked_list_sha256"]);
    }

    // On a fixture without splits the published ranked list and the in-run ranking choose the same members with the same
    // ranks on every session (both use OpeningVolumeRanking), and no entry before 09:35 New York may use the frozen list.
    [Fact]
    public async Task FrozenList_MatchesTheInRunRankingAndIsUnknownBefore0935()
    {
        using var root = new TemporaryFolder();
        var (daily, opening, sessions) = ParityMarket(["AAA", "BBB", "CCC", "DDD", "EEE"], 230);
        var from = sessions[200];
        var to = sessions[^1];
        var output = new DailyRankingOutput(Ranking, opening.ToDictionary(pair => (pair.Key.Ticker, pair.Key.Day), pair => pair.Value),
            opening.Keys.Select(key => (key.Ticker, key.Day)).ToHashSet());
        foreach (var (ticker, series) in daily)
        foreach (var (date, _) in DailyScreenCommand.Screen(ticker, series, [], Rules, from, to))
            output.Add(date, ticker, ticker, sessions);
        var folder = Path.Combine(root.Path, "parity");
        Directory.CreateDirectory(folder);
        await output.WriteAsync(folder, default);
        await ResearchScreenManifest.PublishAsync(folder, new("daily", "actions", "opening", "sessions"), Rules, Ranking, from, to,
            DailyRankingOutput.IssuerIdentityBasis, null, default);
        var frozen = PublishedResearchScreen.Read(folder).ToMembership(from, to);

        var universe = UniverseConfig.StaticUniverse with { Mode = UniverseConfig.ResolvedSnapshotMode, DailyEligibility = Rules, DailyRanking = Ranking };
        var inRun = await new HistoricalScreenerUniverseProvider(new ParityProvider(daily, opening)).ResolveMembershipAsync(
            new UniverseRequest(daily.Keys.ToArray(), universe, At(from, 9, 30), "1d", At(to, 16, 0)), default);

        static string[] Lists(UniverseMembership membership) => membership.SessionRankings.Select(session => session.Session.ToString("yyyy-MM-dd") + ":" +
            String.Join(';', session.Members.Select(member => $"{member.Rank}={member.Security.Symbol}@{decimal.Round(member.RelativeVolume, 6)}"))).ToArray();
        Assert.Equal(Lists(inRun), Lists(frozen));
        Assert.True(frozen.SessionRankings.Sum(session => session.Members.Count) > 20);
        Assert.Empty(frozen.EligibleDays("EEE"));
        foreach (var ticker in daily.Keys) Assert.Equal(inRun.EligibleDays(ticker), frozen.EligibleDays(ticker));
        var memberDay = frozen.EligibleDays("AAA")[0];
        Assert.False(frozen.IsMemberAt("AAA", At(memberDay, 9, 34)));
        Assert.True(frozen.IsMemberAt("AAA", At(memberDay, 9, 35)));
    }

    // Daily bars (midnight New York), every session's 09:30 opening volume and the session list of a split-free market.
    // EEE trades 500,000 shares a day and never passes the volume rule; the others pass and are ranked by opening volume.
    private static (Dictionary<string, OhlcvBar[]> Daily, Dictionary<(string Ticker, DateOnly Day), decimal> Opening, DateOnly[] Sessions) ParityMarket(
        string[] tickers, int count)
    {
        var sessions = new List<DateOnly>();
        for (var date = new DateOnly(2025, 11, 3); sessions.Count < count; date = date.AddDays(1))
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) sessions.Add(date);
        var daily = new Dictionary<string, OhlcvBar[]>(StringComparer.Ordinal);
        var opening = new Dictionary<(string, DateOnly), decimal>();
        for (var t = 0; t < tickers.Length; t++)
        {
            var volume = tickers[t] == "EEE" ? 500_000m : 2_000_000m + t * 10_000m;
            daily[tickers[t]] = sessions.Select((day, index) => new OhlcvBar(tickers[t], At(day, 0, 0), "1d", 20m + t, 21m + t, 19.5m + t,
                20m + t + (index % 3) * 0.1m, volume)).ToArray();
            for (var index = 0; index < sessions.Count; index++)
                opening[(tickers[t], sessions[index])] = 10_000m + (t * 7919 + index * 104729) % 5000 * 3m;
        }
        return (daily, opening, sessions.ToArray());
    }

    // A New York wall-clock time on a date, as UTC.
    private static DateTimeOffset At(DateOnly day, int hour, int minute) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, minute)), NewYork), TimeSpan.Zero);

    // Serves the parity market to the in-run screen: daily bars and, per session, the 09:30 and 09:35 five-minute bars.
    private sealed class ParityProvider(IReadOnlyDictionary<string, OhlcvBar[]> daily, IReadOnlyDictionary<(string Ticker, DateOnly Day), decimal> opening)
        : IMarketDataProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var ticker in tickers)
            {
                if (timeframes.Contains("1d"))
                    foreach (var bar in daily[ticker].Where(bar => bar.Timestamp >= start && bar.Timestamp <= end)) yield return bar;
                if (!timeframes.Contains("5m")) continue;
                foreach (var ((symbol, day), volume) in opening.Where(pair => pair.Key.Ticker == ticker).OrderBy(pair => pair.Key.Day))
                {
                    var open = At(day, 9, 30);
                    if (open < start || open > end) continue;
                    yield return new(symbol, open, "5m", 20m, 20.5m, 19.5m, 20m, volume);
                    yield return new(symbol, open.AddMinutes(5), "5m", 20m, 20.5m, 19.5m, 20m, volume);
                }
            }
        }
    }
}

/// <summary>Publishes a small synthetic research screen (ranked list, counts, exclusions and manifest) into a folder.</summary>
internal static class SyntheticScreen
{
    internal static readonly DailyScreenRules Rules = new(5m, DailyScreenPriceBasis.SessionOpen, 14,
        MinAverageShareVolume: 1_000_000m, AtrPeriod: 14, MinAtr: 0.5m, MinHistorySessions: 14);
    internal static readonly DailyRankingRules Ranking = new(DailyRankingRules.FirstFiveMinuteRelativeVolume, 14, VolumeBaselineAverage.Mean, 1m, 2);

    /// <summary>Writes the ranked list (rank by order within each session) and the manifest; returns the folder.</summary>
    public static async Task<string> PublishAsync(string folder, IReadOnlyList<(string Session, string Symbol, string SecurityId)> members)
    {
        Directory.CreateDirectory(folder);
        var lines = members.GroupBy(member => member.Session).SelectMany(group => group.Select((member, index) =>
            $"{member.Session},{index + 1},{member.Symbol},{member.SecurityId},alpaca_security_id,200000,100000,2,0"));
        await File.WriteAllLinesAsync(Path.Combine(folder, "ranked-daily-list.csv"),
            new[] { "session_date,rank,symbol,issuer_id,issuer_identity_basis,opening_volume,baseline_volume,relative_volume,proven_zero_baseline_sessions" }.Concat(lines));
        await File.WriteAllLinesAsync(Path.Combine(folder, "ranked-daily-counts.csv"), new[] { "session_date,eligible,qualified,selected" }
            .Concat(members.GroupBy(member => member.Session).Select(group => $"{group.Key},{group.Count()},{group.Count()},{group.Count()}")));
        await File.WriteAllLinesAsync(Path.Combine(folder, "ranked-daily-exclusions.csv"), ["session_date,symbol,reason"]);
        var dates = members.Select(member => DateOnly.Parse(member.Session)).ToArray();
        await ResearchScreenManifest.PublishAsync(folder, new("daily", "actions", "opening", "sessions"), Rules, Ranking, dates.Min(), dates.Max(),
            "alpaca_security_id", null, default);
        return folder;
    }
}

/// <summary>A uniquely named folder under the system temporary directory, deleted with everything in it on disposal.</summary>
internal sealed class TemporaryFolder : IDisposable
{
    /// <summary>Creates the folder, named <paramref name="name"/> when given (a repeated run at the same path) or uniquely.</summary>
    public TemporaryFolder(string? name = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name ?? "tf-research-screen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Deletes the folder after checking it is the one this fixture created directly under the temporary directory.</summary>
    public void Dispose()
    {
        if (System.IO.Path.GetDirectoryName(Path) != System.IO.Path.GetTempPath().TrimEnd(System.IO.Path.DirectorySeparatorChar))
            throw new InvalidOperationException("Fixture cleanup escaped the temporary directory.");
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

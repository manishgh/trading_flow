using System.Globalization;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class EffectiveRunSettingsTests
{
    /// <summary>The preserved previous-session strategy shares runtime-root calendar coverage between review and generation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DayWorkflow_FreezesPriorSessionWarmupWithoutCollectingCandlesDuringReview(bool preparedCalendar)
    {
        using var fixture = await Fixture.CreateAsync();
        // This fixture's swing-only terminal override is deliberately removed before selecting day research.
        var profileYaml = await File.ReadAllTextAsync(fixture.ProfilePath);
        await File.WriteAllTextAsync(fixture.ProfilePath, profileYaml.Replace(
            "  precommitted_liquidation_source_utc: 2026-05-21T04:00:00+00:00\n", String.Empty, StringComparison.Ordinal));
        var day = Assert.Single(await fixture.Catalog.GetStrategiesAsync(StrategySelectionMode.Backtest),
            item => item.Identity.StrategyId == "technical.opening-range-breakout" && item.Identity.SemanticVersion == "1.2.0");
        var handler = new CalendarOnlyHandler();
        var providers = new TradingFlow.Backtesting.ConfiguredDataProviders(
            () => TradingFlow.Alpaca.AlpacaOptions.Create(ProductionProfile.Paper) with {
                KeyId = "synthetic-test", SecretKey = "synthetic-test" },
            TradingFlow.Finviz.FinvizOptions.CreateDefault, () => null, () => new HttpClient(handler, disposeHandler: false));
        var profile = fixture.Reader.ReadBacktestRun(fixture.ProfilePath);
        var preparedRun = fixture.Writer.ResolveBacktestDataRoots(BacktestExecutionProfileResolver.Resolve(profile, [day.Definition]));
        Assert.NotEqual(profile.NormalizedRoot, preparedRun.NormalizedRoot);
        Assert.Equal(Path.Combine(fixture.Paths.DataRoot, "backtest", "normalized"), preparedRun.NormalizedRoot);
        if (preparedCalendar)
        {
            var market = providers.CreateMarket(preparedRun, new FileSystemRawArchiveWriter(new RawArchiveOptions(preparedRun.RawRoot)));
            await new TradingFlow.Data.Csv.CachedMarketDataProvider(market, preparedRun.NormalizedRoot, "use_cache")
                .SelectPreparedCalendarAsync(new(2026, 9, 1), new(2026, 10, 6), default);
            Assert.Equal(1, handler.CalendarRequests);
        }
        var adapter = new WorkflowRunConfiguration(fixture.Catalog, fixture.Writer, fixture.Reader,
            Moq.Mock.Of<TradingFlow.Domain.Execution.IOperationalTradingLimitStore>(), providers);
        var now = DateTimeOffset.UtcNow;
        var source = new UniversePreviewResponse("1.0", Guid.NewGuid(), "day", now, now.AddMinutes(5), new string('a', 64),
            [new("MSFT", "watching", [new("wishlist", "synthetic-test", now, now.AddMinutes(5), new string('b', 64), false)])], [], now);
        var request = new StartBacktestRequest("1.0", "day-workflow", source.UniverseSnapshotId,
            [new(day.Identity.StrategyId, day.Identity.SemanticVersion, day.Identity.ContentSha256)],
            DateTimeOffset.Parse("2026-10-05T13:30:00Z"), DateTimeOffset.Parse("2026-10-06T20:00:00Z"),
            10000, 1, 25, 5, "use_cache", "full", "synthetic-day", Path.GetFileName(fixture.ProfilePath));
        var normalized = await adapter.NormalizeBacktestRequestAsync(request, default);
        Assert.Equal(ExecutionReplayPolicy.IntradayRthFiveMinuteNextBarMarketV1, normalized.ExecutionReplayPolicyId);
        var settings = (await adapter.GetBacktestResolvedSettingsAsync(normalized, default))!;
        Assert.Equal("day", settings.Horizon);
        Assert.Contains("warmup 31 days", settings.RequiredMarketDataContext);
        var generated = await adapter.WriteAsync(normalized, source, "synthetic-day", default);
        var run = fixture.Reader.ReadBacktestRun(generated[0]);
        Assert.Equal(31, run.TimeWindow.WarmupLookbackDays);
        Assert.Equal(0, run.Engine.IndicatorWarmupBars);
        Assert.Equal(new[] { "5m" }, run.Intervals);
        Assert.Equal("5m", run.DerivedTimeframes.Source);
        Assert.False(run.News.Enabled);
        Assert.False(run.Validation.Benchmark.Enabled);
        Assert.Equal(preparedRun.RawRoot, run.RawRoot);
        Assert.Equal(preparedRun.NormalizedRoot, run.NormalizedRoot);
        Assert.Equal(preparedRun.ResultsRoot, run.ResultsRoot);
        Assert.False(Directory.Exists(profile.RawRoot));
        Assert.False(Directory.Exists(profile.NormalizedRoot));
        Assert.Equal(1, handler.CalendarRequests);
        Assert.Equal(400, fixture.Reader.ReadBacktestRun(fixture.ProfilePath).TimeWindow.WarmupLookbackDays);
    }

    /// <summary>Supplies synthetic regular sessions (1 Sep - 5 Oct 2026, Labor Day closed) and rejects any bar or news request during plan review.</summary>
    private sealed class CalendarOnlyHandler : HttpMessageHandler
    {
        public int CalendarRequests { get; private set; }

        /// <summary>Answers only the ordinary exchange-calendar route; every other provider request is a test failure.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/v2/calendar", request.RequestUri!.AbsolutePath);
            CalendarRequests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                Content = new StringContent(SyntheticCalendarJson(), System.Text.Encoding.UTF8, "application/json")
            });
        }

        // Twenty-four weekday sessions give the ORB its twenty-session opening-volume baseline before 5 October.
        private static string SyntheticCalendarJson() => "[" + String.Join(",", Enumerable.Range(0, 35)
            .Select(offset => new DateOnly(2026, 9, 1).AddDays(offset))
            .Where(date => date <= new DateOnly(2026, 10, 5) && date != new DateOnly(2026, 9, 7) &&
                date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .Select(date => $"{{\"date\":\"{date:yyyy-MM-dd}\",\"open\":\"09:30\",\"close\":\"16:00\"}}")) + "]";
    }

    /// <summary>Rejected attempt cleanup removes its generated copies without deleting another run or shared input evidence.</summary>
    [Fact]
    public async Task RejectedAttemptCleanup_DeletesOnlyItsOwnGeneratedFiles()
    {
        using var fixture = await Fixture.CreateAsync(ExecutionReplayPolicy.DailyRthFiveMinuteV1);
        var rejected = fixture.Writer.WriteBacktestConfig(fixture.Request);
        var retained = fixture.Writer.WriteBacktestConfig(fixture.Request);
        var rejectedFiles = TradingFlow.Backtesting.BacktestRunInputFiles.Resolve(rejected, fixture.Reader.ReadBacktestRun(rejected));
        var retainedFiles = TradingFlow.Backtesting.BacktestRunInputFiles.Resolve(retained, fixture.Reader.ReadBacktestRun(retained));
        fixture.Writer.DiscardUnacceptedBacktest(rejected, [.. rejectedFiles, .. retainedFiles, fixture.ProfilePath]);
        Assert.All(rejectedFiles.Where(path => path.StartsWith(fixture.Paths.GeneratedBacktestConfigsRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)), path => Assert.False(File.Exists(path)));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "manifest.json")));
        Assert.All(retainedFiles, path => Assert.True(File.Exists(path)));
        Assert.True(File.Exists(fixture.ProfilePath));
    }

    /// <summary>Source evidence rejection after generation must remove its private inputs even though no durable job exists yet.</summary>
    [Fact]
    public async Task InvalidSourceBinding_AfterGeneration_LeavesNoGeneratedInputs()
    {
        using var fixture = await Fixture.CreateAsync(ExecutionReplayPolicy.DailyRthSipPortfolioV1);
        var yaml = await File.ReadAllTextAsync(fixture.ProfilePath);
        var absent = Path.Combine(fixture.Root, "missing-source.json").Replace('\\', '/');
        await File.WriteAllTextAsync(fixture.ProfilePath, yaml.Replace("engine:",
            $"engine:\n  execution_source_manifest_path: {absent}\n  execution_source_manifest_sha256: {new string('a', 64)}", StringComparison.Ordinal));
        var now = DateTimeOffset.UtcNow;
        var identity = fixture.Strategy.Identity;
        var source = new UniversePreviewResponse("1.0", Guid.NewGuid(), "swing", now, now.AddMinutes(5), new string('b', 64),
            [new("MSFT", "watching", [new("wishlist", "fixture", now, now.AddMinutes(5), new string('c', 64), false)])], [], now);
        var request = new StartBacktestRequest("1.0", "invalid-source", source.UniverseSnapshotId,
            [new(identity.StrategyId, identity.SemanticVersion, identity.ContentSha256)], now.AddMonths(-1), now.AddDays(-1),
            10000, 1, 25, 5, "use_cache", "full", "invalid-source", Path.GetFileName(fixture.ProfilePath));
        var adapter = new WorkflowRunConfiguration(fixture.Catalog, fixture.Writer, fixture.Reader,
            Moq.Mock.Of<TradingFlow.Domain.Execution.IOperationalTradingLimitStore>());
        await Assert.ThrowsAsync<FileNotFoundException>(() => adapter.WriteAsync(request, source, "invalid-source", default));
        Assert.Empty(Directory.GetFiles(fixture.Paths.GeneratedBacktestConfigsRoot, "*", SearchOption.AllDirectories));
        Assert.True(File.Exists(fixture.ProfilePath));
        Assert.False(File.Exists(absent));
    }

    // A generated API/Web/Android run must keep every engine and portfolio assumption from its selected profile.
    [Theory]
    [InlineData(ExecutionReplayPolicy.DailyRthFiveMinuteV1)]
    [InlineData(ExecutionReplayPolicy.DailyRthSipPortfolioV1)]
    public async Task GeneratedRun_PreservesProfileAndExplicitPortfolioChoices(string policy)
    {
        using var fixture = await Fixture.CreateAsync(policy);
        var profile = fixture.Reader.ReadBacktestRun(fixture.ProfilePath);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var parsed = fixture.Reader.ReadBacktestRun(fixture.Writer.WriteBacktestConfig(fixture.Request));

            Assert.Equal(profile.Engine, parsed.Engine);
            Assert.Equal(profile.Portfolio with
            {
                StartingCapital = fixture.Request.StartingCapital,
                AccountRiskBudgetPct = fixture.Request.AccountRiskBudgetPct,
                MaxPositionNotionalPct = fixture.Request.MaxPositionNotionalPct,
                MaxConcurrentPositions = fixture.Request.MaxConcurrentPositions
            }, parsed.Portfolio);
            Assert.Equal(profile.Execution, parsed.Execution);
            Assert.Equal(profile.Validation, parsed.Validation);
            Assert.Equal(fixture.Request.TestWindowStartUtc, parsed.TimeWindow.Start);
            Assert.Equal(fixture.Request.TestWindowEndUtc, parsed.TimeWindow.End);
            Assert.Equal(fixture.Request.CachePolicy, parsed.CachePolicy);
            Assert.Equal("wishlist+finviz", parsed.Universe!.ResolvedSource);
            Assert.Equal(37, parsed.Engine.TickerTimeoutSeconds);
            Assert.All(parsed.Strategies, path => Assert.True(File.Exists(path)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    // A saved plan's daily eligibility rules are written under the resolved stock list and read back exactly; a run without
    // rules has none, and a partial block written by hand fails instead of silently trading every stock every day.
    [Fact]
    public async Task GeneratedRun_RoundTripsDailyEligibility()
    {
        using var fixture = await Fixture.CreateAsync();
        var rules = new TradingFlow.Domain.Market.DailyScreenRules(5m, TradingFlow.Domain.Market.DailyScreenPriceBasis.SessionOpen, 14,
            MinAverageShareVolume: 1_000_000m, AtrPeriod: 14, MinAtr: 0.5m, MinHistorySessions: 14);

        var withRules = fixture.Reader.ReadBacktestRun(fixture.Writer.WriteBacktestConfig(fixture.Request with { DailyEligibility = rules }));
        var withoutRules = fixture.Reader.ReadBacktestRun(fixture.Writer.WriteBacktestConfig(fixture.Request));

        Assert.Equal(rules, withRules.Universe!.DailyEligibility);
        Assert.Equal(UniverseConfig.ResolvedSnapshotMode, withRules.Universe.Mode);
        Assert.Null(withoutRules.Universe!.DailyEligibility);
        var path = fixture.Writer.WriteBacktestConfig(fixture.Request with { DailyEligibility = rules });
        File.WriteAllText(path, File.ReadAllText(path).Replace("    min_history_sessions: 14", String.Empty, StringComparison.Ordinal));
        Assert.ThrowsAny<Exception>(() => fixture.Reader.ReadBacktestRun(path));
    }

    // Reject a typo before writing snapshots, instead of accidentally falling back to native daily execution.
    [Fact]
    public async Task UnknownReplayPolicy_DoesNotCreateRunFiles()
    {
        using var fixture = await Fixture.CreateAsync("unsupported-replay-policy");
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Writer.WriteBacktestConfig(fixture.Request));
        Assert.Contains("Unsupported execution replay policy", exception.Message);
        Assert.False(Directory.Exists(fixture.Paths.GeneratedBacktestConfigsRoot));
    }

    // Invalid operator choices must be reported rather than silently corrected by server defaults.
    [Theory]
    [InlineData("capital")]
    [InlineData("risk")]
    [InlineData("position-size")]
    [InlineData("open-positions")]
    [InlineData("cache")]
    [InlineData("dates")]
    public async Task InvalidRequest_DoesNotCreateRunFiles(string field)
    {
        using var fixture = await Fixture.CreateAsync();
        var request = field switch
        {
            "capital" => fixture.Request with { StartingCapital = 0 },
            "risk" => fixture.Request with { AccountRiskBudgetPct = 101 },
            "position-size" => fixture.Request with { MaxPositionNotionalPct = 0 },
            "open-positions" => fixture.Request with { MaxConcurrentPositions = 0 },
            "cache" => fixture.Request with { CachePolicy = "guess" },
            "dates" => fixture.Request with { TestWindowEndUtc = fixture.Request.TestWindowStartUtc },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.Throws<InvalidOperationException>(() => fixture.Writer.WriteBacktestConfig(request));
        Assert.False(Directory.Exists(fixture.Paths.GeneratedBacktestConfigsRoot));
    }

    // The current lightweight reader cannot preserve these values, so reject them instead of injecting or truncating YAML.
    [Theory]
    [InlineData("wishlist\nportfolio:\n  starting_capital: 1")]
    [InlineData("wishlist#truncated")]
    public async Task UnsupportedScalar_DoesNotCreateRunFiles(string source)
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.Throws<InvalidDataException>(() => fixture.Writer.WriteBacktestConfig(fixture.Request with { UniverseSource = source }));
        Assert.False(Directory.Exists(fixture.Paths.GeneratedBacktestConfigsRoot));
    }

    // An omitted or unknown server profile is an error for both modes, even when catalog entries exist.
    [Theory]
    [InlineData(null)]
    [InlineData("missing-profile.yaml")]
    public async Task WorkflowRequiresExplicitProfile(string? key)
    {
        using var fixture = await Fixture.CreateAsync();
        var adapter = new WorkflowRunConfiguration(fixture.Catalog, fixture.Writer, fixture.Reader,
            Moq.Mock.Of<TradingFlow.Domain.Execution.IOperationalTradingLimitStore>());
        var identity = new StrategyReference(fixture.Strategy.Identity.StrategyId,
            fixture.Strategy.Identity.SemanticVersion, fixture.Strategy.Identity.ContentSha256);
        var now = DateTimeOffset.UtcNow;
        var universe = new UniversePreviewResponse(ContractVersions.VersionOne, Guid.NewGuid(), "swing", now,
            now.AddMinutes(1), new string('a', 64), [], [], now);
        var backtest = new StartBacktestRequest(ContractVersions.VersionOne, "test", universe.UniverseSnapshotId,
            [identity], now.AddMonths(-1), now.AddDays(-1), 10000, 1, 25, 4, "use_cache", "full", "test", key);
        var paper = new StartPaperRunRequest(ContractVersions.VersionOne, "test", universe.UniverseSnapshotId,
            identity, false, false, "limit", "day", "strategy_validated", "test", "paper_shadow", key);

        Assert.Contains("configuration explicitly", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.WriteAsync(backtest, universe, "test", default))).Message);
        Assert.Contains("configuration explicitly", (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.WriteAsync(paper, universe, "test", default))).Message);
        Assert.False(Directory.Exists(fixture.Paths.GeneratedBacktestConfigsRoot));
        Assert.False(Directory.Exists(fixture.Paths.GeneratedPaperConfigsRoot));
    }

    // A saved plan cannot silently start with edited base settings; reviewing and saving their new hash permits them.
    [Fact]
    public async Task ChangedProfile_IsRejectedByRealAdapterUntilNewHashIsAccepted()
    {
        using var fixture = await Fixture.CreateAsync();
        var adapter = new WorkflowRunConfiguration(fixture.Catalog, fixture.Writer, fixture.Reader,
            Moq.Mock.Of<TradingFlow.Domain.Execution.IOperationalTradingLimitStore>());
        var key = Path.GetFileName(fixture.ProfilePath);
        var savedHash = await adapter.GetBacktestConfigurationHashAsync(key, default);
        var now = DateTimeOffset.UtcNow;
        var preview = new UniversePreviewResponse(ContractVersions.VersionOne, Guid.NewGuid(), "swing", now,
            now.AddMinutes(5), new string('a', 64),
            [new("MSFT", "watching", [new("wishlist", "watch", now, now.AddMinutes(5), new string('b', 64), false)])], [], now);
        var request = new StartBacktestRequest(ContractVersions.VersionOne, "profile-change", preview.UniverseSnapshotId,
            [new(fixture.Strategy.Identity.StrategyId, fixture.Strategy.Identity.SemanticVersion, fixture.Strategy.Identity.ContentSha256)],
            now.AddMonths(-1), now.AddDays(-1), 10000, 1, 25, 4, "use_cache", "full", "profile-change", key, savedHash);
        var yaml = await File.ReadAllTextAsync(fixture.ProfilePath);
        await File.WriteAllTextAsync(fixture.ProfilePath,
            yaml.Replace("ticker_timeout_seconds: 37", "ticker_timeout_seconds: 43", StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.WriteAsync(request, preview, "changed", default));
        Assert.Contains("profile changed", error.Message);
        Assert.False(Directory.Exists(fixture.Paths.GeneratedBacktestConfigsRoot));

        var reviewedHash = await adapter.GetBacktestConfigurationHashAsync(key, default);
        Assert.NotEqual(savedHash, reviewedHash);
        var files = await adapter.WriteAsync(request with { ConfigurationSha256 = reviewedHash }, preview, "reviewed", default);
        Assert.Equal(43, fixture.Reader.ReadBacktestRun(files[0]).Engine.TickerTimeoutSeconds);
    }

    private sealed class Fixture : IDisposable
    {
        public required string Root { get; init; }
        public required string ProfilePath { get; init; }
        public required ProjectPaths Paths { get; init; }
        public required SimpleYamlReader Reader { get; init; }
        public required RunConfigWriter Writer { get; init; }
        public required ConfigCatalogService Catalog { get; init; }
        public required StrategyOption Strategy { get; init; }
        public required BacktestRunRequest Request { get; init; }

        // Read only source-controlled catalogs; write every generated artifact under one disposable test root.
        public static async Task<Fixture> CreateAsync(string policy = ExecutionReplayPolicy.DailyRthSipPortfolioV1)
        {
            var repo = TestRepository.FindRoot();
            var root = Path.Combine(Path.GetTempPath(), "tradingflow-effective-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var paths = new ProjectPaths(root);
                var reader = new SimpleYamlReader();
                var artifacts = new StrategyArtifactCatalog(repo, Path.Combine(repo, "configs", "strategy-catalog.json"), reader);
                var store = new StrategyExperimentArtifactStore(Path.Combine(root, "experiments"), artifacts, reader, AtomicFileArtifactWriter.Instance);
                var authorization = new StrategyAuthorizationTestRegistry();
                var catalog = new ConfigCatalogService(paths, reader, artifacts, store, authorization);
                var strategy = (await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest)).First(item => item.Definition.Timeframe == "1d");
                var normalizedRoot = root.Replace('\\', '/');
                // This empty synthetic graph tests setting preservation only; it cannot qualify an evidence-required recipe.
                await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"),
                    """{"SchemaVersion":"drawdown-context-manifest.v1","Provider":"synthetic-settings-test","Version":"1","Sources":[],"Contexts":[]}""");
                var yaml = (await File.ReadAllTextAsync(Path.Combine(repo, "configs", "backtest", "swing-backtest-profile.yaml")))
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace("  raw_root: data/backtest/raw", $"  raw_root: {normalizedRoot}/raw", StringComparison.Ordinal)
                    .Replace("  normalized_root: data/backtest/normalized", $"  normalized_root: {normalizedRoot}/normalized", StringComparison.Ordinal)
                    .Replace("  results_root: data/backtest/results", $"  results_root: {normalizedRoot}/results", StringComparison.Ordinal)
                    .Replace("  ticker_timeout_seconds: 1800", "  ticker_timeout_seconds: 37", StringComparison.Ordinal)
                    .Replace("  max_retained_replay_bars: 1000000", "  max_retained_replay_bars: 12345", StringComparison.Ordinal)
                    .Replace("  max_strategy_work_items: 10000", "  max_strategy_work_items: 321", StringComparison.Ordinal)
                    .Replace("  max_retained_candidates: 1000000", "  max_retained_candidates: 45678", StringComparison.Ordinal)
                    .Replace("  fail_fast: false", $"  fail_fast: true\n  precommitted_liquidation_source_utc: 2026-05-21T04:00:00+00:00\n  execution_replay_policy_id: {policy}\n  evidence_manifest_path: {normalizedRoot}/manifest.json\n  evidence_research_root: {normalizedRoot}", StringComparison.Ordinal)
                    .Replace("  max_execution_order_history: 1000000", "  max_execution_order_history: 9876\n  max_open_trades_per_ticker: 3\n  impact_bps_at_max_participation: 7.5", StringComparison.Ordinal)
                    .Replace("    universe_as_of_date:", "    universe_as_of_date: 2026-01-02", StringComparison.Ordinal)
                    .Replace("  allow_extended_hours_trading: false", "  allow_extended_hours_trading: true\n  order_expiration: day\n  entry_order_type: limit", StringComparison.Ordinal);
                var profilePath = Path.Combine(paths.BacktestConfigsRoot, "profile.yaml");
                Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
                await File.WriteAllTextAsync(profilePath, yaml);
                // Reject any profile-template drift before a synthetic adapter can reach a shared data root.
                var isolatedProfile = reader.ReadBacktestRun(profilePath);
                Assert.Equal(Path.GetFullPath(Path.Combine(root, "raw")), isolatedProfile.RawRoot);
                Assert.Equal(Path.GetFullPath(Path.Combine(root, "normalized")), isolatedProfile.NormalizedRoot);
                Assert.Equal(Path.GetFullPath(Path.Combine(root, "results")), isolatedProfile.ResultsRoot);
                var request = new BacktestRunRequest(profilePath, "test-profile", 180, null, "My stocks",
                    ["MSFT", "AAPL"], [strategy.Path], 17250.50m, 1.25m, 30.5m, 7, "refresh", [],
                    new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero), UniverseSource: "wishlist+finviz");
                return new Fixture
                {
                    Root = root, Paths = paths, ProfilePath = profilePath, Reader = reader, Catalog = catalog,
                    Strategy = strategy, Request = request,
                    Writer = new RunConfigWriter(paths, reader, AtomicFileArtifactWriter.Instance, artifacts, store, authorization)
                };
            }
            catch
            {
                Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetDirectoryName(root), ignoreCase: true);
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        // Remove fixtures and snapshots after every test without touching the operator's database or stock lists.
        public void Dispose()
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetDirectoryName(Root), ignoreCase: true);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}

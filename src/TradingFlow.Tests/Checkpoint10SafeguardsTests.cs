using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using TradingFlow.Alpaca;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Strategies;
using TradingFlow.Finviz;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

/// <summary>
/// Focused offline tests for the four Checkpoint 10 Top-500 pre-run safeguards (20 Nov 2026):
/// selective IndicatorTimeframes (omitting unused 5m snapshots on daily runs), evidence_catalog calendar cutoff bounds,
/// swing-research-screen-profile.yaml registration, and skipping news provider calls for pure-bar strategies.
/// </summary>
public sealed class Checkpoint10SafeguardsTests
{
    private static readonly TimeZoneInfo NewYorkZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public async Task CandlePipeline_IndicatorTimeframes_OmitsFiveMinuteSnapshots_WhileNewsOrbSwingTriggersIdentically()
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), reader);
        var newsOrbArtifact = catalog.GetSnapshot().ExecutableArtifacts
            .Single(a => a.Identity.StrategyId == "technical.news-orb-swing" && a.Identity.SemanticVersion == "1.0.0");
        var newsOrb = newsOrbArtifact.ResolvedStrategy with { Regime = null };

        Assert.Equal(["5m", "1d"], StrategyDecisionRequestAssembler.ResolveRequiredTimeframes(newsOrb));
        Assert.Equal(["1d"], StrategyDecisionRequestAssembler.ResolveIndicatorTimeframes(newsOrb));

        var tradingDays = BuildWeekdays(new DateOnly(2026, 3, 2), 35);
        var (dailyBars, fiveMinuteBars, schedules) = BuildNewsOrbFixtureBars("ORBX", tradingDays);
        var signalDay = tradingDays[^1];
        var asOfUtc = SessionMidnightUtc(signalDay.AddDays(1));
        var provider = new SyntheticMultiTimeframeProvider(dailyBars, fiveMinuteBars, schedules);

        // 1. Full pipeline (default IndicatorTimeframes: null) computes both 1d and 5m snapshots.
        var fullResult = await new CandlePipelineEngine().RunAsync(
            new CandlePipelineRequest(
                ["ORBX"],
                ["5m", "1d"],
                ["5m", "1d"],
                "5m",
                SessionMidnightUtc(tradingDays[0]),
                asOfUtc,
                BoundedCapacity: 256,
                WorkerCount: 1,
                MaxRetainedBars: 20_000,
                OperationalRules: MarketEvidenceOperationalRules.ForExecutionTimingPolicy(newsOrbArtifact.AdmissionProfile.ExecutionTimingPolicy)),
            provider,
            default);

        // 2. Selective pipeline (IndicatorTimeframes: ["1d"]) retains 5m bars but skips all 5m IndicatorSnapshot allocations.
        var selectiveResult = await new CandlePipelineEngine().RunAsync(
            new CandlePipelineRequest(
                ["ORBX"],
                ["5m", "1d"],
                ["5m", "1d"],
                "5m",
                SessionMidnightUtc(tradingDays[0]),
                asOfUtc,
                BoundedCapacity: 256,
                WorkerCount: 1,
                MaxRetainedBars: 20_000,
                OperationalRules: MarketEvidenceOperationalRules.ForExecutionTimingPolicy(newsOrbArtifact.AdmissionProfile.ExecutionTimingPolicy),
                IndicatorTimeframes: ["1d"]),
            provider,
            default);

        var fullState = fullResult.TickerStates["ORBX"];
        var selectiveState = selectiveResult.TickerStates["ORBX"];
        Assert.Equal(fullState.BarsByTimeframe["5m"].Count, selectiveState.BarsByTimeframe["5m"].Count);
        Assert.Equal(fullState.BarsByTimeframe["1d"].Count, selectiveState.BarsByTimeframe["1d"].Count);
        Assert.True(fullState.SnapshotsByTimeframe.ContainsKey("5m"));
        Assert.False(selectiveState.SnapshotsByTimeframe.ContainsKey("5m"));
        Assert.True(selectiveState.SnapshotsByTimeframe.ContainsKey("1d"));

        // 3. Evaluate News ORB Swing through StrategyDecisionRequestAssembler + StrategyDecisionKernel on BOTH states.
        var catalystTime = SessionLocalUtc(signalDay, new TimeOnly(8, 30));
        var catalyst = new CatalystEvent(
            Ticker: "ORBX",
            Timestamp: catalystTime,
            Type: CatalystType.EarningsRelease,
            Headline: "ORBX beats quarterly earnings and raises full-year revenue guidance",
            SentimentScore: 0.85m,
            Provider: "alpaca",
            ExternalId: "orbx-er-1",
            Url: "https://example.test/orbx-earnings",
            ReceivedAt: catalystTime.AddMinutes(1),
            UpdatedAt: catalystTime,
            AvailabilityEvidence: "test_fixture",
            DecisionAvailableAt: catalystTime.AddMinutes(1),
            DecisionAvailabilityEvidence: "test_fixture",
            ClassificationVersion: "finbert-v1");

        var runtime = WithoutRegime(newsOrbArtifact);
        var setupKey = StrategyDecisionRequestAssembler.CreateSetupKey(runtime.Identity.StrategyId, "1d", dailyBars[^1].Timestamp);
        var discovery = StrategyDecisionRequestAssembler.CreateBacktestDiscovery("ORBX", setupKey, asOfUtc, asOfUtc.AddDays(1)) with
        {
            IsPersisted = true,
            IsActive = true
        };
        var eligible = new StrategyEligibilityEvidence(true, "test", "ok", asOfUtc, "{}");
        var runId = Guid.NewGuid();
        var kernel = new StrategyDecisionKernel();

        var fullRequest = StrategyDecisionRequestAssembler.Create(
            runtime, "ORBX", fullState.BarsByTimeframe, fullState.SnapshotsByTimeframe, [catalyst], asOfUtc,
            discovery, eligible, eligible, StrategyCandidateState.DataWarming, 0, setupKey, asOfUtc, asOfUtc.AddDays(1),
            requiredWarmupBars: 21, runId, sessionSchedules: schedules);
        var selectiveRequest = StrategyDecisionRequestAssembler.Create(
            runtime, "ORBX", selectiveState.BarsByTimeframe, selectiveState.SnapshotsByTimeframe, [catalyst], asOfUtc,
            discovery, eligible, eligible, StrategyCandidateState.DataWarming, 0, setupKey, asOfUtc, asOfUtc.AddDays(1),
            requiredWarmupBars: 21, runId, sessionSchedules: schedules);

        var fullDecision = kernel.Evaluate(fullRequest);
        var selectiveDecision = kernel.Evaluate(selectiveRequest);

        Assert.True(fullDecision.IsTriggered, $"Expected full decision triggered; state={fullDecision.State}, reason={fullDecision.NoEntryReason}");
        Assert.True(selectiveDecision.IsTriggered, $"Expected selective decision triggered; state={selectiveDecision.State}, reason={selectiveDecision.NoEntryReason}");
        Assert.Equal(fullDecision.Direction, selectiveDecision.Direction);
        Assert.Equal(fullDecision.OrderPlan, selectiveDecision.OrderPlan);
        Assert.Equal(fullDecision.Signal!.OpeningRangeHigh, selectiveDecision.Signal!.OpeningRangeHigh);
        Assert.Equal(fullDecision.Signal.OpeningRangeLow, selectiveDecision.Signal.OpeningRangeLow);
        Assert.Equal(fullDecision.Signal.OpeningRelativeVolume, selectiveDecision.Signal.OpeningRelativeVolume);
        Assert.Equal(fullDecision.Signal.SessionVwap, selectiveDecision.Signal.SessionVwap);
    }

    [Fact]
    public async Task EvidenceCatalogCalendarBounds_CapsAtCutoffAndBacktestCompletesThroughFinalSession()
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), reader);
        var baseProfile = reader.ReadBacktestRun(Path.Combine(root, "configs/backtest/swing-backtest-profile.yaml"));

        var exclusiveEndUtc = DateTimeOffset.Parse("2026-10-31T04:00:00Z"); // Exclusive midnight after Fri 2026-10-30.
        var evidenceCatalogRun = baseProfile with
        {
            Provider = "evidence_catalog",
            Engine = baseProfile.Engine with { ExecutionReplayPolicyId = ExecutionReplayPolicy.DailyRthFiveMinuteV1 }
        };
        Assert.Equal(
            new DateOnly(2026, 10, 30),
            BacktestCalendarBounds.IntradayLastRequiredDate(evidenceCatalogRun, exclusiveEndUtc, "America/New_York"));
        Assert.Null(
            BacktestCalendarBounds.IntradayLastRequiredDate(
                baseProfile with { Engine = baseProfile.Engine with { ExecutionReplayPolicyId = ExecutionReplayPolicy.DailyRthFiveMinuteV1 } },
                exclusiveEndUtc,
                "America/New_York"));

        // Build a synthetic daily + 5m dataset ending on Fri 2026-10-30 whose calendar throws if asked for > 2026-10-30.
        var tradingDays = BuildWeekdaysEndingOn(new DateOnly(2026, 10, 30), 95);
        var (dailyBars, fiveMinuteBars, schedules) = BuildSimpleDailyAndFiveMinuteFixture("CALX", tradingDays);
        var strictCalendarCutoff = new DateOnly(2026, 10, 30);
        var provider = new StrictCutoffCalendarProvider(dailyBars, fiveMinuteBars, schedules, strictCalendarCutoff);

        var tempDir = Path.Combine(Path.GetTempPath(), $"tf-cp10-cal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var run = evidenceCatalogRun with
            {
                RunName = "cp10-cal-test",
                Tickers = ["CALX"],
                Intervals = ["5m", "1d"],
                DerivedTimeframes = new DerivedTimeframeConfig("1d"),
                RawRoot = Path.Combine(tempDir, "raw"),
                NormalizedRoot = Path.Combine(tempDir, "normalized"),
                ResultsRoot = Path.Combine(tempDir, "results"),
                CachePolicy = "bypass",
                Engine = evidenceCatalogRun.Engine with
                {
                    WorkerCount = 1,
                    IndicatorWarmupBars = 80,
                    FailFast = true,
                    ExecutionReplayPolicyId = ExecutionReplayPolicy.DailyRthFiveMinuteV1
                },
                TimeWindow = new TimeWindowConfig(
                    "fixed",
                    LookbackDays: 30,
                    Start: SessionMidnightUtc(tradingDays[^5]),
                    End: exclusiveEndUtc,
                    WarmupLookbackDays: 160),
                News = baseProfile.News with { Enabled = false, ProviderName = "none" },
                Universe = UniverseConfig.StaticUniverse with
                {
                    Mode = UniverseConfig.ResolvedSnapshotMode,
                    ResolvedSource = "test_fixture",
                    SelectedSourceTickers = ["CALX"],
                    ResolvedAtUtc = exclusiveEndUtc
                }
            };
            var squeezeArtifact = catalog.GetSnapshot().ExecutableArtifacts
                .Single(a => a.Identity.StrategyId == "technical.bollinger-squeeze-breakout" && a.Identity.SemanticVersion == "1.0.0");
            var runtimeStrategy = WithoutRegime(squeezeArtifact);
            var runner = new BacktestRunner(
                reader,
                catalog,
                marketDataProviderFactory: _ => provider);
            var resultPath = Path.Combine(tempDir, "results", "result.json");

            var result = await runner.RunValidatedAsync(run, [runtimeStrategy], DateTimeOffset.UtcNow, resultPath, default);

            Assert.All(result.TickerResults, ticker => Assert.Null(ticker.ErrorMessage));
            Assert.True(provider.MaxRequestedCalendarEndDate <= strictCalendarCutoff);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.True(diagnostic.EvaluatedBarCount >= 5);
            Assert.True(diagnostic.RejectionCounts.GetValueOrDefault("candidate_window_not_available") >= 1);

            // Weekend/holiday cutoff: exclusiveEndUtc = Mon 2026-11-02 00:00 EST (05:00Z) caps calendar at Sun 2026-11-01
            // (Sat 10-31 and Sun 11-01 closed). Friday 10-30's bar must complete cleanly without querying Mon 2026-11-02.
            var weekendEndUtc = DateTimeOffset.Parse("2026-11-02T05:00:00Z");
            var weekendCutoff = new DateOnly(2026, 11, 1);
            Assert.Equal(
                weekendCutoff,
                BacktestCalendarBounds.IntradayLastRequiredDate(evidenceCatalogRun, weekendEndUtc, "America/New_York"));
            var weekendSchedules = new Dictionary<DateOnly, MarketSessionSchedule>(schedules)
            {
                [new DateOnly(2026, 10, 31)] = new(new DateOnly(2026, 10, 31), false, new TimeOnly(9, 30), new TimeOnly(16, 0)),
                [new DateOnly(2026, 11, 1)] = new(new DateOnly(2026, 11, 1), false, new TimeOnly(9, 30), new TimeOnly(16, 0))
            };
            var weekendProvider = new StrictCutoffCalendarProvider(dailyBars, fiveMinuteBars, weekendSchedules, weekendCutoff);
            var weekendRun = run with
            {
                RunName = "cp10-cal-weekend-test",
                TimeWindow = run.TimeWindow with { End = weekendEndUtc }
            };
            var weekendRunner = new BacktestRunner(reader, catalog, marketDataProviderFactory: _ => weekendProvider);
            var weekendResult = await weekendRunner.RunValidatedAsync(
                weekendRun,
                [runtimeStrategy],
                DateTimeOffset.UtcNow,
                Path.Combine(tempDir, "results", "weekend-result.json"),
                default);

            Assert.All(weekendResult.TickerResults, ticker => Assert.Null(ticker.ErrorMessage));
            Assert.True(weekendProvider.MaxRequestedCalendarEndDate <= weekendCutoff);
            var weekendDiagnostic = Assert.Single(weekendResult.Diagnostics);
            Assert.True(weekendDiagnostic.EvaluatedBarCount >= 5);
            Assert.True(weekendDiagnostic.RejectionCounts.GetValueOrDefault("candidate_window_not_available") >= 1);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SwingResearchScreenProfile_LoadsInReaderAndCatalog_AndPreservesAllTenSwingStrategyHashes()
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var profilePath = Path.Combine(root, "configs/backtest/swing-research-screen-profile.yaml");
        Assert.True(File.Exists(profilePath));

        var config = reader.ReadBacktestRun(profilePath);
        Assert.Equal("swing-research-screen-profile", config.RunName);
        Assert.Equal("backtest", config.Mode);
        Assert.Equal(4, config.Engine.WorkerCount);
        Assert.Equal(25_000_000, config.Engine.MaxRetainedReplayBars);
        Assert.Equal(ExecutionReplayPolicy.DailyRthFiveMinuteV1, config.Engine.ExecutionReplayPolicyId);
        Assert.Equal(100_000m, config.Portfolio.StartingCapital);
        Assert.Equal(0.5m, config.Portfolio.AccountRiskBudgetPct);
        Assert.Equal(20m, config.Portfolio.MaxPositionNotionalPct);
        Assert.Equal(10, config.Portfolio.MaxConcurrentPositions);

        var paths = new ProjectPaths(root);
        var artifactCatalog = new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), reader);
        var experiments = new StrategyExperimentArtifactStore(
            Path.Combine(root, "data", "research", "strategy-experiments"),
            artifactCatalog,
            reader,
            TradingFlow.Engine.Storage.AtomicFileArtifactWriter.Instance);
        var configCatalog = new ConfigCatalogService(
            paths,
            reader,
            artifactCatalog,
            experiments,
            Moq.Mock.Of<IStrategyAuthorizationPolicy>());
        var backtestConfigs = configCatalog.GetBacktestConfigs();
        Assert.Contains(backtestConfigs, item => item.FileName.Equals("swing-research-screen-profile.yaml", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(backtestConfigs, item => item.FileName.Equals("swing-backtest-profile.yaml", StringComparison.OrdinalIgnoreCase));

        var squeeze = reader.ReadStrategy(Path.Combine(root, "configs/backtest/strategies/bollinger-squeeze-breakout.bt-v1.yaml"));
        var resolvedProfile = BacktestExecutionProfileResolver.Resolve(config, [squeeze]);
        Assert.Equal(["5m", "1d"], RunConfigWriter.ResolveDownloadTimeframes(resolvedProfile, [squeeze]));

        // Verify all 10 swing strategy artifacts in configs/strategy-catalog.json keep their exact immutable hashes.
        var swingIds = new[]
        {
            ("technical.bollinger-squeeze-breakout", "1.0.0"),
            ("technical.news-orb-swing", "1.0.0"),
            ("technical.ema21-trend-pullback", "1.0.0"),
            ("technical.ema21-trend-pullback", "1.1.0"),
            ("technical.rubber-band-reclaim", "1.0.0"),
            ("technical.bollinger-squeeze-breakdown", "1.0.0"),
            ("technical.news-orb-breakdown", "1.0.0"),
            ("technical.ema21-rejection-short", "1.0.0"),
            ("technical.ema21-rejection-short", "1.1.0"),
            ("technical.parabolic-exhaustion-short", "1.0.0")
        };
        var executables = artifactCatalog.GetSnapshot().ExecutableArtifacts;
        foreach (var (id, version) in swingIds)
        {
            Assert.Contains(executables, a => a.Identity.StrategyId == id && a.Identity.SemanticVersion == version);
        }
    }

    [Fact]
    public async Task RunPreparedCoreAsync_SkipsNewsProviderForPureBarStrategies_AndQueriesForNewsAwareStrategies()
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new StrategyArtifactCatalog(root, Path.Combine(root, "configs", "strategy-catalog.json"), reader);
        var baseProfile = reader.ReadBacktestRun(Path.Combine(root, "configs/backtest/swing-backtest-profile.yaml"));

        var tradingDays = BuildWeekdaysEndingOn(new DateOnly(2026, 10, 30), 95);
        var (dailyBars, fiveMinuteBars, schedules) = BuildSimpleDailyAndFiveMinuteFixture("NEWSX", tradingDays);
        var provider = new SyntheticMultiTimeframeProvider(dailyBars, fiveMinuteBars, schedules);

        var tempDir = Path.Combine(Path.GetTempPath(), $"tf-cp10-news-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var newsHttpRequests = 0;
            var configuredProviders = new ConfiguredDataProviders(
                () => AlpacaOptions.Create(ProductionProfile.Paper) with { KeyId = "k", SecretKey = "s" },
                () => new FinvizOptions(new Uri("https://elite.finviz.com"), "t"),
                () => null,
                () => new HttpClient(new CountingNewsHttpHandler(() => Interlocked.Increment(ref newsHttpRequests))));

            var run = baseProfile with
            {
                RunName = "cp10-news-skip-test",
                Provider = "evidence_catalog",
                Tickers = ["NEWSX"],
                Intervals = ["5m", "1d"],
                DerivedTimeframes = new DerivedTimeframeConfig("1d"),
                RawRoot = Path.Combine(tempDir, "raw"),
                NormalizedRoot = Path.Combine(tempDir, "normalized"),
                ResultsRoot = Path.Combine(tempDir, "results"),
                CachePolicy = "bypass",
                Engine = baseProfile.Engine with
                {
                    WorkerCount = 1,
                    IndicatorWarmupBars = 80,
                    FailFast = true,
                    ExecutionReplayPolicyId = ExecutionReplayPolicy.DailyRthFiveMinuteV1
                },
                TimeWindow = new TimeWindowConfig(
                    "fixed",
                    LookbackDays: 25,
                    Start: SessionMidnightUtc(tradingDays[^5]),
                    End: SessionMidnightUtc(tradingDays[^1].AddDays(1)),
                    WarmupLookbackDays: 160),
                News = baseProfile.News with { Enabled = true, ProviderName = "alpaca" },
                Universe = UniverseConfig.StaticUniverse with
                {
                    Mode = UniverseConfig.ResolvedSnapshotMode,
                    ResolvedSource = "test_fixture",
                    SelectedSourceTickers = ["NEWSX"],
                    ResolvedAtUtc = SessionMidnightUtc(tradingDays[^5])
                }
            };

            var runner = new BacktestRunner(
                reader,
                catalog,
                marketDataProviderFactory: _ => provider,
                dataProviders: configuredProviders);

            // 1. Pure-bar strategy (bollinger-squeeze-breakout, even with news.enabled: true): zero news HTTP requests.
            var squeezeArtifact = catalog.GetSnapshot().ExecutableArtifacts
                .Single(a => a.Identity.StrategyId == "technical.bollinger-squeeze-breakout" && a.Identity.SemanticVersion == "1.0.0");
            var pureBarRuntime = WithoutRegime(squeezeArtifact);
            Assert.False(HistoricalNewsPresenceEvaluation.StrategyUsesNews(pureBarRuntime.Definition));
            await runner.RunValidatedAsync(run, [pureBarRuntime], DateTimeOffset.UtcNow, Path.Combine(tempDir, "results", "pure.json"), default);
            Assert.Equal(0, Volatile.Read(ref newsHttpRequests));

            // 2. News-aware strategy (rubber-band-reclaim with VetoFreshNewsHours = 48m): queries the configured news provider.
            var rubberBandArtifact = catalog.GetSnapshot().ExecutableArtifacts
                .Single(a => a.Identity.StrategyId == "technical.rubber-band-reclaim" && a.Identity.SemanticVersion == "1.0.0");
            var newsAwareRuntime = WithoutRegime(rubberBandArtifact);
            Assert.True(HistoricalNewsPresenceEvaluation.StrategyUsesNews(newsAwareRuntime.Definition));
            await runner.RunValidatedAsync(run, [newsAwareRuntime], DateTimeOffset.UtcNow, Path.Combine(tempDir, "results", "news.json"), default);
            Assert.True(Volatile.Read(ref newsHttpRequests) > 0, "Expected news provider to be queried for a news-aware strategy.");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // Derives an in-memory test runtime strategy with Regime = null and a matching recomputed immutable ContentSha256.
    private static AuthorizedRuntimeStrategy WithoutRegime(StrategyArtifact artifact)
    {
        var definition = artifact.ResolvedStrategy with { Regime = null };
        var hash = EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
            artifact.Identity.StrategyId,
            artifact.Identity.SemanticVersion,
            definition,
            artifact.AdmissionProfile));
        return new AuthorizedRuntimeStrategy(
            new StrategyArtifactIdentity(artifact.Identity.StrategyId, artifact.Identity.SemanticVersion, hash),
            StrategySelectionMode.Backtest,
            definition,
            artifact.AdmissionProfile);
    }

    private static DateOnly[] BuildWeekdays(DateOnly start, int count)
    {
        var result = new List<DateOnly>(count);
        for (var day = start; result.Count < count; day = day.AddDays(1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                result.Add(day);
            }
        }

        return result.ToArray();
    }

    private static DateOnly[] BuildWeekdaysEndingOn(DateOnly endInclusive, int count)
    {
        var result = new List<DateOnly>(count);
        for (var day = endInclusive; result.Count < count; day = day.AddDays(-1))
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                result.Add(day);
            }
        }

        result.Reverse();
        return result.ToArray();
    }

    private static DateTimeOffset SessionMidnightUtc(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, NewYorkZone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static DateTimeOffset SessionLocalUtc(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, NewYorkZone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static (OhlcvBar[] Daily, OhlcvBar[] FiveMinute, Dictionary<DateOnly, MarketSessionSchedule> Schedules)
        BuildNewsOrbFixtureBars(string ticker, DateOnly[] tradingDays)
    {
        var daily = new List<OhlcvBar>(tradingDays.Length);
        var fiveMin = new List<OhlcvBar>(tradingDays.Length * 78);
        var schedules = BuildCalendarRange(tradingDays[0].AddDays(-5), tradingDays[^1].AddDays(5));

        for (var i = 0; i < tradingDays.Length; i++)
        {
            var day = tradingDays[i];
            var isSignalDay = i == tradingDays.Length - 1;
            var open = isSignalDay ? 105m : 100m;
            var high = isSignalDay ? 112m : 103m;
            var low = isSignalDay ? 104m : 98m;
            var close = isSignalDay ? 111m : 100m;
            var dayVol = isSignalDay ? 5_000_000m : 1_000_000m;
            daily.Add(new OhlcvBar(ticker, SessionMidnightUtc(day), "1d", open, high, low, close, dayVol, "sip", "raw"));

            for (var slot = 0; slot < 78; slot++)
            {
                var start = SessionLocalUtc(day, new TimeOnly(9, 30).AddMinutes(slot * 5));
                if (!isSignalDay)
                {
                    fiveMin.Add(new OhlcvBar(ticker, start, "5m", 100m, 100.5m, 99.5m, 100m, 10_000m, "sip", "raw"));
                }
                else if (slot < 6)
                {
                    // First 30m opening range [104.5 .. 108.0] with 3x opening volume (30,000/bar vs 10,000 baseline).
                    fiveMin.Add(new OhlcvBar(ticker, start, "5m", 105m, 108m, 104.5m, 107.5m, 30_000m, "sip", "raw"));
                }
                else
                {
                    // Afternoon ramp closes above ORH (108.0) and above session VWAP (~108.5).
                    fiveMin.Add(new OhlcvBar(ticker, start, "5m", 109m, 112m, 108.5m, 111m, 15_000m, "sip", "raw"));
                }
            }
        }

        return (daily.ToArray(), fiveMin.ToArray(), schedules);
    }

    private static (OhlcvBar[] Daily, OhlcvBar[] FiveMinute, Dictionary<DateOnly, MarketSessionSchedule> Schedules)
        BuildSimpleDailyAndFiveMinuteFixture(string ticker, DateOnly[] tradingDays)
    {
        var daily = new List<OhlcvBar>(tradingDays.Length);
        var fiveMin = new List<OhlcvBar>(tradingDays.Length * 4);
        var schedules = BuildCalendarRange(tradingDays[0].AddDays(-5), tradingDays[^1]);

        for (var i = 0; i < tradingDays.Length; i++)
        {
            var day = tradingDays[i];
            var px = 100m + i * 0.5m;
            daily.Add(new OhlcvBar(ticker, SessionMidnightUtc(day), "1d", px, px + 2m, px - 1m, px + 1m, 1_000_000m, "sip", "raw"));
            for (var slot = 0; slot < 4; slot++)
            {
                var start = SessionLocalUtc(day, new TimeOnly(9, 30).AddMinutes(slot * 5));
                fiveMin.Add(new OhlcvBar(ticker, start, "5m", px, px + 1m, px - 0.5m, px + 0.5m, 50_000m, "sip", "raw"));
            }
        }

        return (daily.ToArray(), fiveMin.ToArray(), schedules);
    }

    private static Dictionary<DateOnly, MarketSessionSchedule> BuildCalendarRange(DateOnly from, DateOnly to)
    {
        var map = new Dictionary<DateOnly, MarketSessionSchedule>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var isOpen = day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
            map[day] = new MarketSessionSchedule(day, isOpen, new TimeOnly(9, 30), new TimeOnly(16, 0));
        }

        return map;
    }

    private sealed class SyntheticMultiTimeframeProvider(
        OhlcvBar[] dailyBars,
        OhlcvBar[] fiveMinuteBars,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules)
        : IMarketDataProvider, IMarketSessionScheduleProvider
    {
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var tfSet = new HashSet<string>(timeframes, StringComparer.OrdinalIgnoreCase);
            if (tfSet.Contains("1d"))
            {
                foreach (var bar in dailyBars.Where(b => tickers.Contains(b.Ticker) && b.Timestamp >= start && b.Timestamp < end))
                {
                    yield return bar;
                }
            }

            if (tfSet.Contains("5m"))
            {
                foreach (var bar in fiveMinuteBars.Where(b => tickers.Contains(b.Ticker) && b.Timestamp >= start && b.Timestamp < end))
                {
                    yield return bar;
                }
            }

            await Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDateInclusive,
            DateOnly endDateInclusive,
            CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<DateOnly, MarketSessionSchedule>();
            for (var day = startDateInclusive; day <= endDateInclusive; day = day.AddDays(1))
            {
                result[day] = schedules.TryGetValue(day, out var existing)
                    ? existing
                    : new MarketSessionSchedule(day, day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday), new TimeOnly(9, 30), new TimeOnly(16, 0));
            }

            return Task.FromResult<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>(result);
        }
    }

    private sealed class StrictCutoffCalendarProvider(
        OhlcvBar[] dailyBars,
        OhlcvBar[] fiveMinuteBars,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules,
        DateOnly maxAllowedDateInclusive)
        : IMarketDataProvider, IMarketSessionScheduleProvider
    {
        public DateOnly MaxRequestedCalendarEndDate { get; private set; } = DateOnly.MinValue;

        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(
            IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes,
            DateTimeOffset start,
            DateTimeOffset end,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var tfSet = new HashSet<string>(timeframes, StringComparer.OrdinalIgnoreCase);
            if (tfSet.Contains("1d"))
            {
                foreach (var bar in dailyBars.Where(b => tickers.Contains(b.Ticker) && b.Timestamp >= start && b.Timestamp < end))
                {
                    yield return bar;
                }
            }

            if (tfSet.Contains("5m"))
            {
                foreach (var bar in fiveMinuteBars.Where(b => tickers.Contains(b.Ticker) && b.Timestamp >= start && b.Timestamp < end))
                {
                    yield return bar;
                }
            }

            await Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(
            DateOnly startDateInclusive,
            DateOnly endDateInclusive,
            CancellationToken cancellationToken = default)
        {
            if (endDateInclusive > MaxRequestedCalendarEndDate)
            {
                MaxRequestedCalendarEndDate = endDateInclusive;
            }

            if (endDateInclusive > maxAllowedDateInclusive)
            {
                throw new InvalidDataException($"Frozen exchange-sessions dataset ends on {maxAllowedDateInclusive:yyyy-MM-dd}; requested {endDateInclusive:yyyy-MM-dd}.");
            }

            var result = new Dictionary<DateOnly, MarketSessionSchedule>();
            for (var day = startDateInclusive; day <= endDateInclusive; day = day.AddDays(1))
            {
                result[day] = schedules.TryGetValue(day, out var existing)
                    ? existing
                    : new MarketSessionSchedule(day, day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday), new TimeOnly(9, 30), new TimeOnly(16, 0));
            }

            return Task.FromResult<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>>(result);
        }
    }

    private sealed class CountingNewsHttpHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"news":[],"next_page_token":null}""", Encoding.UTF8, "application/json")
            });
        }
    }
}

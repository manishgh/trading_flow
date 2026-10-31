using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Moq;
using TradingFlow.Alpaca;
using TradingFlow.Application.Candidates;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Data.Jobs;
using TradingFlow.Data.Csv;
using TradingFlow.Domain.Discovery;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

/// <summary>Owns disposable research strategies, deterministic market inputs and the real plan-to-runner dependencies.</summary>
internal sealed class BacktestMatrixFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tfm-" + Guid.NewGuid().ToString("N")[..8]);
    public CandidateWorkflowTestDatabase Database { get; } = new();
    public SqliteJobRepository Jobs { get; private set; } = null!;
    public BacktestPlanService Plans { get; private set; } = null!;
    public StartBacktestRequest Request { get; private set; } = null!;
    private readonly SimpleYamlReader reader = new();
    private StrategyArtifactCatalog artifacts = null!;
    private SyntheticDailyProvider provider = null!;
    public IReadOnlyDictionary<string, int> HistoricalBarCaptures => provider.BarCaptures;
    public int HistoricalBarsCaptureCount => provider.BarCaptures.Values.Sum();
    public int HistoricalCalendarCaptureCount => provider.CalendarCaptureCount;
    private SipPortfolioExecutionSourceTests.Sources? archives;
    public string? SipSourceSha256 { get; private set; }

    public static DurableJobWorkerOptions WorkerOptions { get; } = DurableJobWorkerOptions.Default with
    {
        LeaseDuration = TimeSpan.FromSeconds(10),
        HeartbeatInterval = TimeSpan.FromMilliseconds(50),
        IdlePollInterval = TimeSpan.FromMilliseconds(10)
    };

    /// <summary>Builds an isolated catalog and captured source while preserving the application's actual admission, configuration and execution paths.</summary>
    public static async Task<BacktestMatrixFixture> CreateAsync(int stockCount, int strategyCount, bool sip = false,
        EngineConfig? evidence = null)
    {
        var fixture = new BacktestMatrixFixture();
        try
        {
            Directory.CreateDirectory(fixture.Root);
            await fixture.Database.InitializeAsync();
            await fixture.WriteCatalogAsync(strategyCount, sip);
            fixture.provider = new(sip, fixture.Root);
            var profilePath = Path.Combine(fixture.Root, "configs", "backtest", "matrix.yaml");
            Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
            var profile = ProfileYaml(fixture.Root);
            if (evidence is not null)
                profile = profile.Replace("engine:", $"engine:\n  evidence_research_root: {evidence.EvidenceResearchRoot!.Replace('\\', '/')}\n" +
                    $"  evidence_manifest_path: {evidence.EvidenceManifestPath!.Replace('\\', '/')}\n" +
                    $"  evidence_manifest_sha256: {evidence.EvidenceManifestSha256}\n" +
                    $"  evidence_dependency_graph_sha256: {evidence.EvidenceDependencyGraphSha256}", StringComparison.Ordinal);
            if (sip) profile = await fixture.AddSipSourceAsync(profile, stockCount);
            await File.WriteAllTextAsync(profilePath, profile);
            fixture.artifacts = new(fixture.Root, Path.Combine(fixture.Root, "configs", "strategy-catalog.json"), fixture.reader);
            var paths = new ProjectPaths(fixture.Root);
            var authorization = new StrategyAuthorizationTestRegistry();
            var experiments = new StrategyExperimentArtifactStore(Path.Combine(fixture.Root, "experiments"), fixture.artifacts,
                fixture.reader, AtomicFileArtifactWriter.Instance);
            var catalog = new ConfigCatalogService(paths, fixture.reader, fixture.artifacts, experiments, authorization);
            var writer = new RunConfigWriter(paths, fixture.reader, AtomicFileArtifactWriter.Instance, fixture.artifacts, experiments, authorization);
            var configuration = new WorkflowRunConfiguration(catalog, writer, fixture.reader, Mock.Of<IOperationalTradingLimitStore>());
            var clock = new CandidateWorkflowTestClock(DateTimeOffset.UtcNow);
            var source = new Mock<IUniverseSourceResolver>();
            source.Setup(item => item.ResolveAsync(It.IsAny<UniverseSourceSelectionRequest>(), "swing", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new UniverseSourceCapture("wishlist", "fixture", clock.Now, clock.Now.AddMinutes(3),
                    Enumerable.Range(0, stockCount).Select(index => new DiscoverySymbolObservation("S" + index.ToString("D3"))).ToArray()));
            var discovery = new UniverseDiscoveryService(source.Object, fixture.Database.Discovery, fixture.Database.Workflows, fixture.Database.Workflows, clock);
            var captured = await discovery.PreviewAsync(new("1.0", "swing", null, [new("wishlist", "fixture")]));
            var strategies = await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest);
            fixture.Request = new("1.0", "matrix", captured.UniverseSnapshotId,
                strategies.Select(strategy => new StrategyReference(strategy.Identity.StrategyId, strategy.Identity.SemanticVersion, strategy.Identity.ContentSha256)).ToArray(),
                SyntheticDailyProvider.Sessions[270].AddMinutes(sip ? 1 : 0), SyntheticDailyProvider.Sessions[sip ? 272 : 299].AddDays(1),
                10000m, 1m, 25m, 5, "use_cache", "full", "matrix", "matrix.yaml");
            fixture.Jobs = new(fixture.Database);
            var limits = new RunQueueLimits(32, 16);
            var workflow = new RunWorkflowService(fixture.Jobs, fixture.Database.Workflows, new StrategyIdentityResolver(catalog), configuration, clock, limits);
            fixture.Plans = new(fixture.Database.Workflows, fixture.Database.Workflows, fixture.Jobs, workflow, configuration, limits, clock);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    /// <summary>Seals physical SIP pages and a captured synthetic calendar, then binds their hashes into this private profile.</summary>
    private async Task<string> AddSipSourceAsync(string profile, int stockCount)
    {
        profile = profile.Replace("\r\n", "\n", StringComparison.Ordinal);
        archives = new();
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var sessions = new List<SipPortfolioSessionArchive>();
        var dates = SyntheticDailyProvider.Sessions[270..273].Select(time =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time, zone).DateTime)).ToArray();
        foreach (var date in dates.Skip(1))
        {
            var opening = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(9, 30)), zone));
            var at = checked((opening.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
            for (var index = 0; index < stockCount; index++)
            {
                var symbol = "S" + index.ToString("D3");
                var quote = JsonSerializer.SerializeToElement(new { t = SipNanosecondTime.Format(at), bp = 127m, ap = 127.1m,
                    bs = 1000, @as = 1000, bx = "Q", ax = "P", c = new[] { "R" }, z = "C" });
                var prefix = JsonSerializer.SerializeToElement(new { t = SipNanosecondTime.Format(at - 1), bp = 127m, ap = 127.1m,
                    bs = 1000, @as = 1000, bx = "Q", ax = "P", c = new[] { "R" }, z = "C" });
                var stopQuote = JsonSerializer.SerializeToElement(new { t = SipNanosecondTime.Format(at + 1500), bp = 123.9m, ap = 124m,
                    bs = 1000, @as = 1000, bx = "Q", ax = "P", c = new[] { "R" }, z = "C" });
                var trades = Enumerable.Range(1, 3).Select(row => JsonSerializer.SerializeToElement(new {
                    t = SipNanosecondTime.Format(at + row * 1000), i = row, p = row == 1 ? 127.1m : 124m,
                    s = 10000, x = "U", c = new[] { "@" }, z = "C"
                })).ToArray();
                sessions.Add(await archives.Session(symbol, at, [prefix], [quote, stopQuote], trades, 23_400_000_000_000L));
            }
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(dates.Select(date => new {
            date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), open = "09:30", close = "16:00"
        }));
        await File.WriteAllBytesAsync(Path.Combine(Root, "calendar.json"), payload);
        var receipt = JsonSerializer.SerializeToUtf8Bytes(new {
            HttpStatus = 200, Sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)), RowCount = dates.Length,
            Start = dates[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), End = dates[^1].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            CapturedUtc = DateTimeOffset.UtcNow, OriginalHistoricalReceiptClaim = false
        });
        var receiptPath = Path.Combine(Root, "calendar.receipt.json");
        await File.WriteAllBytesAsync(receiptPath, receipt);
        var manifest = new EventPortfolioSourceManifest(1, SipPortfolioExecutionSourceTests.Policy(), sessions,
            new(ExecutionProtectionActivation.NativeParentFullyFilled, 30_000_000_000L, 0, true, 1, 1, "raw-fixture"),
            receiptPath, Convert.ToHexStringLower(SHA256.HashData(receipt)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var manifestPath = Path.Combine(Root, "execution-source.json");
        await File.WriteAllBytesAsync(manifestPath, bytes);
        SipSourceSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return profile.Replace("engine:", $"engine:\n  execution_replay_policy_id: {TradingFlow.Domain.Backtesting.ExecutionReplayPolicy.DailyRthSipPortfolioV1}\n" +
            $"  precommitted_liquidation_source_utc: {SyntheticDailyProvider.Sessions[272]:O}\n" +
            $"  execution_source_manifest_path: {manifestPath.Replace('\\', '/')}\n  execution_source_manifest_sha256: {SipSourceSha256}", StringComparison.Ordinal)
            .Replace("download_timeframes:\n    - 1d", "download_timeframes:\n    - 1d\n    - 5m", StringComparison.Ordinal);
    }

    /// <summary>Uses the production durable façade and actual runner; only the provider edge supplies fixture candles.</summary>
    public BacktestJobService CreateHandler(IMarketDataProvider? marketProvider = null, Action<BacktestProgress>? observe = null)
    {
        if (marketProvider is BlockingProvider blocking)
            blocking.UseArchiveWriter(new FileSystemRawArchiveWriter(new RawArchiveOptions(Path.Combine(Root, "raw"))));
        var runner = new BacktestRunner(reader, artifacts, marketDataProviderFactory: _ => marketProvider ?? provider,
            preparedDataBindingStore: new PreparedMarketDataEventBindingStore(Database.Workflows));
        IBacktestRunExecutor executor = observe is null ? new BacktestRunExecutor(runner) : new ObservedExecutor(runner, observe);
        return new(executor, Jobs, reader,
            BacktestJobServiceOptions.Default with { MaximumPendingJobs = 32, RetainedTerminalJobs = 32 }, artifacts);
    }

    /// <summary>Replays one accepted book through the existing injected-source path for prepared-versus-direct decision and monthly parity.</summary>
    public async Task<(BacktestResult Result, MonthlyRunResultReport Monthly)> RunDirectAsync(Guid jobId)
    {
        var job = await Jobs.GetJobAsync(jobId, default) ?? throw new InvalidOperationException("Synthetic book was not admitted.");
        var captured = DurableFileJobRequest.Deserialize(job.RequestJson);
        using var lease = captured.AcquireVerifiedReadLease();
        var cohort = captured.Workflow?.Cohort ?? throw new InvalidOperationException("Parity requires an exact saved cohort.");
        var run = reader.ReadBacktestRun(job.ConfigPath);
        var runner = new BacktestRunner(reader, artifacts, marketDataProviderFactory: _ => provider);
        var result = await runner.RunAsync(job.ConfigPath, Path.Combine(Root, "direct-results", jobId.ToString("N"), "result.json"),
            Request.TestWindowEndUtc, default);
        var monthly = new MonthlyRunResultWriter(new StrategyRunArtifactValidator(reader, artifacts)).Create(run, result,
            Request.TestWindowEndUtc, cohort.CohortName, [cohort.Strategy]);
        return (result, monthly);
    }

    /// <summary>Replays the same admitted synthetic book through ordinary or scoped verification without changing its registered strategies.</summary>
    internal async Task<BacktestResult> RunEvidenceParityAsync(Guid jobId, WorkflowEvidenceVerificationScope? scope, string outputName)
    {
        var job = await Jobs.GetJobAsync(jobId, default) ?? throw new InvalidOperationException("Synthetic parity book was not admitted.");
        using var captured = DurableFileJobRequest.Deserialize(job.RequestJson).AcquireVerifiedReadLease();
        var run = reader.ReadBacktestRun(job.ConfigPath);
        var runner = new BacktestRunner(reader, artifacts, marketDataProviderFactory: _ => provider);
        var resultPath = Path.Combine(Root, "evidence-parity", outputName, "result.json");
        return scope is null ? await runner.RunAsync(job.ConfigPath, resultPath, Request.TestWindowEndUtc, default)
            : await runner.RunAsync(job.ConfigPath, resultPath, Request.TestWindowEndUtc, default,
                scope.Resolve(run.Engine), scope.Guard);
    }

    /// <summary>Observes synchronous real-runner progress to simulate host shutdown without replacing execution or result publication.</summary>
    private sealed class ObservedExecutor(BacktestRunner runner, Action<BacktestProgress> observe) : IBacktestRunExecutor
    {
        /// <summary>Forwards every real result and progress record; the fixture may cancel its host at an exact production stage.</summary>
        public Task<BacktestResult> RunAsync(string configPath, string resultPath, DateTimeOffset cutoff,
            CancellationToken token, IProgress<BacktestProgress>? progress = null,
            PreparedMarketDataRequestReference? preparedDataReference = null) =>
            runner.RunAsync(configPath, resultPath, cutoff, token, new ObservedProgress(observe, progress), preparedDataReference);
    }

    private sealed class ObservedProgress(Action<BacktestProgress> observe, IProgress<BacktestProgress>? downstream) : IProgress<BacktestProgress>
    {
        /// <summary>Retains the worker's progress projection before triggering the fixture's host-shutdown signal.</summary>
        public void Report(BacktestProgress value) { downstream?.Report(value); observe(value); }
    }

    /// <summary>Creates distinct immutable research configurations using the existing shared momentum evaluator.</summary>
    private async Task WriteCatalogAsync(int strategyCount, bool sip)
    {
        var strategiesRoot = Path.Combine(Root, "configs", "strategies");
        Directory.CreateDirectory(strategiesRoot);
        var template = StrategyDecisionKernelTests.CreateRequest().Strategy;
        var profileId = sip ? "fixture-terminal-open-v4" : "causal-market-exits-v3";
        for (var index = 0; index < strategyCount; index++)
        {
            var strategy = template with
            {
                StrategyId = "fixture.matrix." + index,
                StrategyName = "Matrix fixture " + index,
                EntryRules = template.EntryRules with { MinVolumeSpike = 0, MinEntryRsi = 55, VolumeConfirmationMode = "none" },
                ExitRules = template.ExitRules with { MaxHoldHours = 10000, MaxHoldBars = 1, TargetRMultiple = 1m + index / 2m, AllowSameBarStopTarget = false },
                Execution = template.Execution with { SlippageBps = 0 }
            };
            await File.WriteAllTextAsync(Path.Combine(strategiesRoot, "matrix-" + index + ".yaml"), RunConfigWriter.SerializeStrategyYaml(strategy));
        }
        var manifest = new
        {
            schemaVersion = 1,
            admissionProfiles = new[] { new {
                profileId, profileVersion = sip ? "4.0.0" : "3.0.0", parserProfile = "simple-yaml-strict-v2",
                standardIndicatorLibrary = "Skender.Stock.Indicators", standardIndicatorLibraryVersion = "2.7.1",
                derivedIndicatorProfile = "TradingFlow.DerivedIndicators", derivedIndicatorProfileVersion = "2.1.0",
                riskPolicy = "account-risk-budget-v1", executionTimingPolicy = sip
                    ? "completed-bar-precommitted-terminal-open-v4" : "completed-bar-next-slice-market-exits-v3"
            } },
            strategies = Enumerable.Range(0, strategyCount).Select(index => new {
                sourcePath = "configs/strategies/matrix-" + index + ".yaml", strategyId = "fixture.matrix." + index,
                semanticVersion = "1.0.0", lifecycle = "research", admissionProfileId = profileId,
                reason = "Isolated deterministic acceptance fixture."
            }).ToArray()
        };
        await File.WriteAllTextAsync(Path.Combine(Root, "configs", "strategy-catalog.json"), JsonSerializer.Serialize(manifest));
    }

    /// <summary>Declares deterministic zero-fee daily execution with optional research checks disabled explicitly.</summary>
    private static string ProfileYaml(string root) => $$"""
        run_name: matrix
        mode: backtest
        engine:
          pipeline: tpl
          worker_count: 1
          bounded_capacity: 100
          indicator_warmup_bars: 260
          ticker_timeout_seconds: 60
          max_retained_replay_bars: 10000
          max_strategy_work_items: 100
          max_retained_candidates: 10000
          fail_fast: true
        time_window:
          type: rolling
          lookback_days: 180
          warmup_lookback_days: 400
        market_data:
          provider: alpaca
          download_timeframes:
            - 1d
          derive_from: 1d
          raw_root: {{Path.Combine(root, "raw").Replace('\\', '/')}}
          normalized_root: {{Path.Combine(root, "normalized").Replace('\\', '/')}}
          results_root: {{Path.Combine(root, "results").Replace('\\', '/')}}
          cache_policy: use_cache
        providers:
          alpaca:
            data_feed: sip
        portfolio:
          starting_capital: 10000
          account_risk_budget_pct: 1
          max_position_notional_pct: 25
          max_concurrent_positions: 5
          fixed_buy_fee: 0
          fixed_sell_fee: 0
          max_execution_order_history: 10000
          prevent_overlapping_ticker_positions: true
          max_bar_participation_pct: 100
          sec_fee_rate: 0
          finra_taf_per_share: 0
          finra_taf_cap: 0
        signal_source:
          type: internal_candles
          require_signature: false
          dedupe_window_seconds: 300
          reject_stale_after_seconds: 0
        validation:
          out_of_sample:
            enabled: false
            percent: 30
          walk_forward:
            enabled: false
            window_days: 30
            step_days: 15
          benchmark:
            enabled: false
            ticker: SPY
          data_quality:
            enabled: true
            max_duplicate_bars: 0
            max_invalid_ohlc_bars: 0
            max_zero_volume_pct: 0
          bias_risk:
            universe_source: synthetic_fixture
            price_adjustment_policy: synthetic
        execution:
          router: simulated
          broker: none
          dry_run: true
          allow_live_orders: false
          order_type: bracket
          allow_extended_hours_trading: false
        news:
          enabled: false
        artifacts:
          retention_mode: full
        universe:
          mode: unresolved_wishlist
        strategies:
          - ../strategies/matrix-0.yaml
        """;

    /// <summary>Removes only this fixture's temporary database and checked temporary directory on success or failure.</summary>
    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        archives?.Dispose();
        var absolute = Path.GetFullPath(Root);
        if (!String.Equals(Path.GetDirectoryName(absolute), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Matrix fixture cleanup escaped its owned temporary directory.");
        if (Directory.Exists(absolute))
        {
            if ((File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Matrix fixture cleanup cannot follow a directory link.");
            Directory.Delete(absolute, true);
        }
    }

    /// <summary>Provides synthetic daily bars and a complete synthetic weekday calendar without external requests.</summary>
    private sealed class SyntheticDailyProvider(bool sip, string root) : IMarketDataProvider, IMarketSessionScheduleProvider,
        IMarketDataProvenanceProvider, IMarketDataCacheSourceDescriptorProvider, IHistoricalMarketDataEvidenceProvider
    {
        private readonly IRawArchiveWriter archive = new FileSystemRawArchiveWriter(new RawArchiveOptions(Path.Combine(root, "raw")));
        private readonly ConcurrentDictionary<string, int> barCaptures = new(StringComparer.Ordinal);
        private int calendarCaptureCount;
        public IReadOnlyDictionary<string, int> BarCaptures => new Dictionary<string, int>(barCaptures, StringComparer.Ordinal);
        public int CalendarCaptureCount => Volatile.Read(ref calendarCaptureCount);
        public MarketDataProvenance MarketDataProvenance => new("synthetic-matrix-v1", "sip", sip ? "raw" : "all");
        public MarketDataCacheSourceDescriptor CacheSourceDescriptor => new("synthetic-matrix-v1", "sip", sip ? "raw" : "all", "synthetic-fixed-v1");
        public string HistoricalEndpointIdentity => "synthetic://matrix-bars-v1";
        public string HistoricalParserVersion => "synthetic-matrix-json-v1";
        public string HistoricalCalendarIdentity => "synthetic://matrix-weekday-new-york-v1";
        public static DateTimeOffset[] Sessions { get; } = CreateSessions();

        /// <summary>Archives one explicit synthetic source payload and refuses a row that exceeds the caller's collection allowance.</summary>
        public async Task<HistoricalBarsCapture> CaptureHistoricalBarsAsync(string ticker, string timeframe,
            DateTimeOffset startUtc, DateTimeOffset endUtc, long maximumBars, CancellationToken cancellationToken)
        {
            if (maximumBars <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBars));
            var started = DateTimeOffset.UtcNow;
            var key = $"{ticker}|{timeframe}|{startUtc:O}|{endUtc:O}";
            barCaptures.AddOrUpdate(key, 1, static (_, count) => count + 1);
            var bars = new List<OhlcvBar>();
            await foreach (var bar in GetBarsAsync([ticker], [timeframe], startUtc, endUtc, cancellationToken))
            {
                if (bars.Count >= maximumBars) throw new InvalidOperationException("Synthetic capture exceeded the retained replay-bar allowance.");
                bars.Add(bar);
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { SyntheticFixture = true, OriginalHistoricalReceiptClaim = false,
                Ticker = ticker, Timeframe = timeframe, StartUtc = startUtc, EndUtc = endUtc, Bars = bars });
            var evidence = await ArchiveSyntheticAsync(archive, "bars", bytes, started, cancellationToken);
            return new(bars, evidence);
        }

        /// <summary>Archives the same complete synthetic calendar used by the original runner edge, with actual fixture receipt times.</summary>
        public async Task<HistoricalCalendarCapture> CaptureHistoricalCalendarAsync(DateOnly startDate, DateOnly endDate,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calendarCaptureCount);
            return await CaptureCalendarAsync(archive, startDate, endDate, cancellationToken);
        }

        /// <summary>Warms actual indicators, produces a rising evaluation period, then stops new signals before the window ends.</summary>
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var ticker in tickers)
            for (var index = 0; index < Sessions.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Sessions[index] < start || Sessions[index] >= end) continue;
                var close = sip && index >= 271 ? 124m - (index - 271) / 10m
                    : index < 290 ? 100m + index / 10m : 100m - (index - 290);
                if (timeframes.Contains("1d"))
                    yield return new(ticker, Sessions[index], "1d", close - .05m,
                        sip && index >= 271 ? 128m : close + 1m, close - 1m, close, 1000000m, "sip", sip ? "raw" : "all");
                if (sip && index >= 270 && timeframes.Contains("5m"))
                    for (var slot = 0; slot < 78; slot++)
                    {
                        var timestamp = Sessions[index].AddHours(9.5).AddMinutes(slot * 5);
                        yield return new(ticker, timestamp, "5m", close, close + 1m, close - 1m, close, 1000000m, "sip", "raw");
                    }
            }
        }

        /// <summary>Returns every requested date, including explicitly closed weekends, for the synthetic market calendar.</summary>
        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(DateOnly startDateInclusive,
            DateOnly endDateInclusive, CancellationToken cancellationToken) => Task.FromResult(Calendar(startDateInclusive, endDateInclusive, cancellationToken));

        /// <summary>Builds New York midnight bars across DST using the fixture's deterministic weekday schedule.</summary>
        private static DateTimeOffset[] CreateSessions()
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var dates = new List<DateTimeOffset>();
            for (var date = new DateOnly(2025, 1, 2); dates.Count < 300; date = date.AddDays(1))
                if (date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                    dates.Add(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), zone)));
            return dates.ToArray();
        }
    }

    /// <summary>Holds the real runner inside provider collection until durable cancellation reaches its token.</summary>
    internal sealed class BlockingProvider : IMarketDataProvider, IMarketSessionScheduleProvider,
        IMarketDataProvenanceProvider, IMarketDataCacheSourceDescriptorProvider, IHistoricalMarketDataEvidenceProvider
    {
        private IRawArchiveWriter? archive;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MarketDataProvenance MarketDataProvenance => new("synthetic-matrix-blocking-v1", "sip", "all");
        public MarketDataCacheSourceDescriptor CacheSourceDescriptor => new("synthetic-matrix-blocking-v1", "sip", "all", "synthetic-fixed-v1");
        public string HistoricalEndpointIdentity => "synthetic://matrix-blocking-bars-v1";
        public string HistoricalParserVersion => "synthetic-matrix-json-v1";
        public string HistoricalCalendarIdentity => "synthetic://matrix-weekday-new-york-v1";

        /// <summary>Receives only the enclosing fixture's explicit raw writer so no default or operator archive can be used.</summary>
        public void UseArchiveWriter(IRawArchiveWriter writer) => archive = writer;

        /// <summary>Signals real comparison collection and holds it until the durable worker propagates cancellation.</summary>
        public async Task<HistoricalBarsCapture> CaptureHistoricalBarsAsync(string ticker, string timeframe,
            DateTimeOffset startUtc, DateTimeOffset endUtc, long maximumBars, CancellationToken cancellationToken)
        {
            if (maximumBars <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBars));
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("A blocking synthetic capture cannot complete without cancellation.");
        }

        /// <summary>Archives complete synthetic dates in the enclosing fixture's raw directory before cancellation collection.</summary>
        public Task<HistoricalCalendarCapture> CaptureHistoricalCalendarAsync(DateOnly startDate, DateOnly endDate,
            CancellationToken cancellationToken) => CaptureCalendarAsync(archive ??
                throw new InvalidOperationException("Blocking provider requires its enclosing fixture's raw writer."),
                startDate, endDate, cancellationToken);

        /// <summary>Signals that private workspace execution started and exits only when the actual worker cancels collection.</summary>
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers, IReadOnlyCollection<string> timeframes,
            DateTimeOffset start, DateTimeOffset end, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        /// <summary>Supplies the same synthetic calendar so cancellation tests reach collection without provider access.</summary>
        public Task<IReadOnlyDictionary<DateOnly, MarketSessionSchedule>> LoadMarketSessionSchedulesAsync(DateOnly startDateInclusive,
            DateOnly endDateInclusive, CancellationToken cancellationToken) => Task.FromResult(Calendar(startDateInclusive, endDateInclusive, cancellationToken));
    }

    /// <summary>Archives synthetic calendar source bytes without claiming that an HTTP request or historical observation occurred.</summary>
    private static async Task<HistoricalCalendarCapture> CaptureCalendarAsync(IRawArchiveWriter archive,
        DateOnly start, DateOnly end, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        var sessions = Calendar(start, end, token);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { SyntheticFixture = true, OriginalHistoricalReceiptClaim = false,
            StartDate = start, EndDate = end, Sessions = sessions.Values.OrderBy(session => session.TradeDate).ToArray() });
        var evidence = await ArchiveSyntheticAsync(archive, "calendar", bytes, started, token);
        return new(sessions, evidence);
    }

    /// <summary>Records fixture-generation time and completed synthetic pagination while preserving the original payload and receipt.</summary>
    private static async Task<HistoricalQueryEvidence> ArchiveSyntheticAsync(IRawArchiveWriter archive, string artifactType,
        byte[] bytes, DateTimeOffset started, CancellationToken token)
    {
        var received = DateTimeOffset.UtcNow;
        var receipt = await archive.ArchiveAsync(new("synthetic-matrix", artifactType, received, "json", "application/json",
            Attributes: new Dictionary<string, string> { ["syntheticFixture"] = "true", ["originalHistoricalReceiptClaim"] = "false",
                ["paginationCompleted"] = "true" }), bytes, token);
        return new(started, DateTimeOffset.UtcNow, true, [receipt]);
    }

    /// <summary>Creates complete inclusive synthetic session coverage shared by ordinary and cancellation providers.</summary>
    private static IReadOnlyDictionary<DateOnly, MarketSessionSchedule> Calendar(DateOnly start, DateOnly end, CancellationToken token)
    {
        var result = new Dictionary<DateOnly, MarketSessionSchedule>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            token.ThrowIfCancellationRequested();
            result[date] = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                ? MarketSessionSchedule.Closed(date) : MarketSessionSchedule.RegularDay(date);
        }
        return result;
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using Moq;
using TradingFlow.Alpaca;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Backtesting.Optimization;
using TradingFlow.Data.Jobs;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Domain.Market;
using System.Globalization;
using System.Text.RegularExpressions;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class EventPortfolioSourceBindingTests
{
    private static readonly long Open = SipNanosecondTime.Parse("2026-05-21T13:30:00Z");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The normal workspace uses its configured source once and preserves the exact prepared IDs, fills and physical source audit.</summary>
    [Fact]
    public async Task ConfiguredWorkspace_MatchesDirectReplayWithoutRegeneratingCandidates()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = Request() with { Portfolio = fixture.Run.Portfolio };
        var direct = Assert.Single(await EventPortfolioExecution.ReplayManyAsync([request],
            SipPortfolioExecutionSource.Create(fixture.Manifest.Sessions, fixture.Manifest.SourcePolicy).ReadAsync(), _ => SimulationPolicy(request)));
        Assert.NotEmpty(direct.Trades);
        Assert.Empty(direct.UnavailableReasons);
        var reader = new SimpleYamlReader();
        var repository = TestRepository.FindRoot();
        var runner = new BacktestRunner(reader, new StrategyArtifactCatalog(repository,
            Path.Combine(repository, "configs", "strategy-catalog.json"), reader),
            marketDataProviderFactory: _ => throw new InvalidOperationException("This bridge fixture must not request provider data."));
        var workspace = runner.CreateWorkspace(fixture.Run, fixture.ResultPath);
        var journal = new Journal();
        var actual = await workspace.ExecuteAsync(() => workspace.Runner.ExecuteEventPortfolioAsync(workspace.Run,
            request.Strategies.ToArray(), request.Candidates, [], null, new Dictionary<string, RegimeCalendar>(), journal, default));
        Assert.Equal(direct.Trades, actual.Trades);
        Assert.Empty(actual.ExecutionFailures);
        Assert.All(actual.Trades, trade => Assert.Equal("prepared-AAA-once", trade.CandidateId));
        using var frozen = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(fixture.ResultPath, ".event-portfolio-request.json")));
        Assert.Equal("prepared-AAA-once", frozen.RootElement.GetProperty("request").GetProperty("candidates")[0].GetProperty("candidateId").GetString());
        Assert.Equal(fixture.Run.Engine.ExecutionSourceManifestSha256, frozen.RootElement.GetProperty("sourceManifestSha256").GetString());
        var audit = JsonSerializer.Deserialize<SipPortfolioSourceAudit>(await File.ReadAllTextAsync(Path.ChangeExtension(fixture.ResultPath, ".event-portfolio-source-audit.json")), Json)!;
        Assert.True(audit.FullyConsumedAndVerified);
        Assert.Equal(2, audit.QuoteRows);
        Assert.Equal(3, audit.TradeRows);
        Assert.Equal(fixture.Run.Engine.ExecutionSourceManifestSha256,
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.ChangeExtension(fixture.ResultPath, ".event-source-manifest.json")))));
        Assert.All(journal.Events.Where(item => item.ReferenceId is not null), item => Assert.Equal("prepared-AAA-once", item.ReferenceId));
        Assert.NotEmpty(Directory.EnumerateDirectories(fixture.Root, ".run-inputs-*"));
    }

    /// <summary>An execution-specific binding refuses a second replay instead of reading raw archives again.</summary>
    [Fact]
    public async Task Binding_IsSingleUseAndPublishesOriginalRequest()
    {
        using var fixture = await Fixture.CreateAsync();
        var binding = EventPortfolioSourceBinding.FromConfig(fixture.Run, fixture.ResultPath)!;
        var first = await binding.ExecuteAsync(Request(), default);
        Assert.Single(first.Trades);
        await Assert.ThrowsAsync<InvalidOperationException>(() => binding.ExecuteAsync(Request(), default));
        Assert.Single(Directory.GetFiles(fixture.Root, "*.event-portfolio-request.json"));
        Assert.Single(Directory.GetFiles(fixture.Root, "*.event-portfolio-outcome.json"));
    }

    /// <summary>Changed manifest bytes fail before a workspace, provider or source stream can be created.</summary>
    [Fact]
    public async Task ConfiguredManifest_HashRejectsChangedBytes()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.AppendAllTextAsync(fixture.ManifestPath, " ");
        var error = Assert.Throws<InvalidDataException>(() => EventPortfolioSourceBinding.FromConfig(fixture.Run, fixture.ResultPath));
        Assert.Contains("frozen profile hash", error.Message);
        Assert.False(File.Exists(Path.ChangeExtension(fixture.ResultPath, ".event-portfolio-request.json")));
    }

    /// <summary>Unsupported timing assumptions are rejected before opening missing or corrupt physical pages.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnsupportedSourceOrReportDelay_RejectsBeforeRawArchiveReads(bool sourceDelay)
    {
        using var fixture = await Fixture.CreateAsync();
        var manifest = fixture.Manifest with {
            SourcePolicy = sourceDelay ? fixture.Manifest.SourcePolicy with { SourceAvailabilityDelayNanoseconds = 1 } : fixture.Manifest.SourcePolicy,
            Assumptions = sourceDelay ? fixture.Manifest.Assumptions : fixture.Manifest.Assumptions with { FillReportLatencyNanoseconds = 1 }
        };
        await fixture.WriteManifestAsync(manifest);
        File.Delete(Path.Combine(fixture.Manifest.Sessions[0].TradesDirectory, "page-0000001.json.gz"));
        var error = Assert.Throws<InvalidDataException>(() => EventPortfolioSourceBinding.FromConfig(fixture.Run, fixture.ResultPath));
        Assert.Contains("zero source/report delay", error.Message);
        Assert.False(File.Exists(Path.ChangeExtension(fixture.ResultPath, ".event-portfolio-request.json")));
    }

    /// <summary>Declared archive bounds must cover the candidate's entry and terminal horizon before source streaming.</summary>
    [Fact]
    public async Task IncompleteDeclaredCoverage_RejectsBeforeConsumingOrPublishingOutcome()
    {
        using var fixture = await Fixture.CreateAsync();
        var candidate = Request().Candidates[0];
        var request = Request() with { Candidates = [candidate with {
            ExecutionReplay = candidate.ExecutionReplay! with { EventExecutionEndUtc = ExecutionEventTime.ToUtc(Open + 86_400_000_000_000L) }
        }] };
        var binding = EventPortfolioSourceBinding.FromConfig(fixture.Run, fixture.ResultPath)!;
        File.Delete(Path.Combine(fixture.Manifest.Sessions[0].TradesDirectory, "page-0000001.json.gz"));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => binding.ExecuteAsync(request, default));
        Assert.Contains("cover", error.Message);
        Assert.False(File.Exists(Path.ChangeExtension(fixture.ResultPath, ".event-portfolio-source-audit.json")));
        Assert.False(File.Exists(Path.ChangeExtension(fixture.ResultPath, ".event-portfolio-outcome.json")));
    }

    /// <summary>Covered endpoints cannot hide an absent exchange session in the middle of the holding period.</summary>
    [Fact]
    public async Task MissingIntermediateExchangeSession_RejectsBeforeReadingRawArchives()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.WriteCalendarAsync([0, 1, 5]);
        var last = await fixture.EmptySessionAsync(5);
        await fixture.WriteManifestAsync(fixture.Manifest with {
            Sessions = [fixture.Manifest.Sessions[0], last], CalendarReceiptSha256 = fixture.CalendarReceiptSha256
        });
        var candidate = Request().Candidates[0];
        var request = Request() with { Candidates = [candidate with {
            ExitTimestamp = ExecutionEventTime.ToUtc(Open + 5 * 86_400_000_000_000L + 10_000_000_000L),
            ExecutionReplay = candidate.ExecutionReplay! with {
                EventExecutionEndUtc = ExecutionEventTime.ToUtc(Open + 5 * 86_400_000_000_000L + 300_000_000_000L)
            }
        }] };
        var binding = EventPortfolioSourceBinding.FromConfig(fixture.Run, fixture.ResultPath)!;
        File.Delete(Path.Combine(fixture.Manifest.Sessions[0].TradesDirectory, "page-0000001.json.gz"));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => binding.ExecuteAsync(request, default));
        Assert.Contains("2026-05-22", error.Message);
        Assert.Contains("missing required exchange session", error.Message);
        Assert.False(File.Exists(Path.ChangeExtension(fixture.ResultPath, ".event-portfolio-outcome.json")));
    }

    /// <summary>Repeating the same CLI result request receives a fresh source binding and suffixed artifacts without changing earlier bytes.</summary>
    [Fact]
    public async Task RepeatedRequestedResultPath_UsesSeparateWorkspaceAndArtifacts()
    {
        using var fixture = await Fixture.CreateAsync();
        var reader = new SimpleYamlReader();
        var repository = TestRepository.FindRoot();
        var runner = new BacktestRunner(reader, new StrategyArtifactCatalog(repository,
            Path.Combine(repository, "configs", "strategy-catalog.json"), reader));
        var request = Request();
        var first = runner.CreateWorkspace(fixture.Run, fixture.ResultPath);
        var firstResult = await first.ExecuteAsync(() => first.Runner.ExecuteEventPortfolioAsync(first.Run,
            request.Strategies.ToArray(), request.Candidates, [], null, new Dictionary<string, RegimeCalendar>(), new Journal(), default));
        var firstAuditPath = Path.ChangeExtension(first.ResultPath, ".event-portfolio-source-audit.json");
        var firstAuditBytes = await File.ReadAllBytesAsync(firstAuditPath);
        var second = runner.CreateWorkspace(fixture.Run, fixture.ResultPath);
        var secondResult = await second.ExecuteAsync(() => second.Runner.ExecuteEventPortfolioAsync(second.Run,
            request.Strategies.ToArray(), request.Candidates, [], null, new Dictionary<string, RegimeCalendar>(), new Journal(), default));
        Assert.NotEqual(first.ResultPath, second.ResultPath);
        Assert.Equal(firstResult.Trades, secondResult.Trades);
        Assert.Equal(firstAuditBytes, await File.ReadAllBytesAsync(firstAuditPath));
        Assert.True(File.Exists(Path.ChangeExtension(second.ResultPath, ".event-portfolio-source-audit.json")));
        Assert.NotEmpty(Directory.EnumerateDirectories(fixture.Root, ".run-inputs-*"));
    }

    /// <summary>Two real optimizer permutations reuse preparation while each obtains its own single-use SIP source and result artifacts.</summary>
    [Fact]
    public async Task TwoSipOptimizerPermutations_UseFreshBindingsAndDistinctResultPaths()
    {
        using var fixture = await Fixture.CreateAsync();
        var configuration = await fixture.WriteOptimizerConfigAsync();
        var reader = new SimpleYamlReader();
        var repository = TestRepository.FindRoot();
        var provider = new EmptyFixtureMarketProvider();
        var runner = new BacktestRunner(reader, new StrategyArtifactCatalog(repository,
            Path.Combine(repository, "configs", "strategy-catalog.json"), reader), marketDataProviderFactory: _ => provider);
        var result = await new StrategyOptimizer(reader, runner).OptimizeAsync(configuration, default);
        Assert.Equal(2, result.TotalPermutations);
        Assert.Equal(2, result.TopRuns.Count);
        Assert.Equal(1, provider.StockReads);
        Assert.Equal(2, result.TopRuns.Select(run => run.BacktestResult.ResultPath).Distinct().Count());
        foreach (var run in result.TopRuns)
        {
            var audit = JsonSerializer.Deserialize<SipPortfolioSourceAudit>(await File.ReadAllTextAsync(
                Path.ChangeExtension(run.BacktestResult.ResultPath, ".event-portfolio-source-audit.json")), Json)!;
            Assert.True(audit.FullyConsumedAndVerified);
            Assert.Equal(3, audit.TradeRows);
        }
    }

    /// <summary>Physically complete empty archives cannot turn an accepted stock into a known zero-return trade.</summary>
    [Fact]
    public async Task EmptyPhysicalArchive_WithholdsEconomicOutcome()
    {
        using var fixture = await Fixture.CreateAsync(empty: true);
        var outcome = await EventPortfolioSourceBinding.FromConfig(fixture.Run, fixture.ResultPath)!.ExecuteAsync(Request(), default);
        Assert.Empty(outcome.Trades);
        Assert.NotEmpty(outcome.Failures);
        Assert.NotEmpty(outcome.UnavailableReasons);
        Assert.Equal(Open, outcome.UnavailableFromNanoseconds);
        var audit = JsonSerializer.Deserialize<SipPortfolioSourceAudit>(await File.ReadAllTextAsync(Path.ChangeExtension(fixture.ResultPath, ".event-portfolio-source-audit.json")), Json)!;
        Assert.True(audit.FullyConsumedAndVerified);
        Assert.Equal(0, audit.TradeRows);
    }

    /// <summary>One source binding cannot silently combine strategies or admit a stock outside its frozen cohort.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultipleStrategiesOrOutsideStock_AreRejectedBeforeReading(bool multipleStrategies)
    {
        using var fixture = await Fixture.CreateAsync();
        var request = Request();
        request = multipleStrategies ? request with { Strategies = [request.Strategies[0], request.Strategies[0]] }
            : request with { Candidates = [request.Candidates[0] with { Ticker = "OTHER" }] };
        var binding = EventPortfolioSourceBinding.FromConfig(fixture.Run, fixture.ResultPath)!;
        await Assert.ThrowsAsync<InvalidDataException>(() => binding.ExecuteAsync(request, default));
        Assert.False(File.Exists(Path.ChangeExtension(fixture.ResultPath, ".event-portfolio-request.json")));
    }

    /// <summary>The durable job path fingerprints the configured source manifest and rejects post-enqueue changes before executing.</summary>
    [Fact]
    public async Task NormalJob_CapturesManifestAndRejectsMutationBeforeExecutor()
    {
        using var fixture = await Fixture.CreateAsync();
        await using var database = new CandidateWorkflowTestDatabase();
        await database.InitializeAsync();
        var configPath = await fixture.WriteRunConfigAsync();
        var reader = new SimpleYamlReader();
        var admittedRun = reader.ReadBacktestRun(configPath);
        Assert.Equal(new[] { "AAA" }, admittedRun.Tickers);
        Assert.True(admittedRun.Universe!.IsResolvedSnapshot);
        Assert.StartsWith(fixture.Root + Path.DirectorySeparatorChar, admittedRun.RawRoot, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(fixture.Root + Path.DirectorySeparatorChar, admittedRun.NormalizedRoot, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.Root, admittedRun.ResultsRoot);
        var repository = new SqliteJobRepository(database);
        var catalog = new StrategyArtifactCatalog(TestRepository.FindRoot(),
            Path.Combine(TestRepository.FindRoot(), "configs", "strategy-catalog.json"), reader);
        var executor = new Mock<IBacktestRunExecutor>(MockBehavior.Strict);
        var service = new BacktestJobService(executor.Object, repository, reader, BacktestJobServiceOptions.Default, catalog);
        var started = await service.StartAsync("frozen-source", configPath);
        var job = await repository.GetJobAsync(started.JobId, default);
        Assert.Contains(DurableFileJobRequest.Deserialize(job!.RequestJson).Files,
            item => item.Path == fixture.ManifestPath && item.Sha256 == fixture.Run.Engine.ExecutionSourceManifestSha256);
        await File.AppendAllTextAsync(fixture.ManifestPath, " ");
        Assert.True(await new DurableJobProcessor(repository).ProcessNextAsync("source-fixture", service, DurableJobWorkerOptions.Default, default));
        var completed = await repository.GetJobAsync(started.JobId, default);
        Assert.Equal("failed", completed!.Status);
        Assert.Contains("changed after enqueue", completed.ErrorMessage);
        executor.VerifyNoOtherCalls();
    }

    /// <summary>Uses unchanged strategy terms and a single immutable pre-capital candidate.</summary>
    private static EventPortfolioReplayRequest Request()
    {
        var definition = StrategyDecisionKernelTests.CreateRequest().Strategy;
        var strategy = definition with { Execution = definition.Execution with { SlippageBps = 0 } };
        var candidate = new BacktestCandidateTrade("AAA", strategy.StrategyName, "long", ExecutionEventTime.ToUtc(Open),
            101, 100, 103.5m, ExecutionEventTime.ToUtc(Open + 10_000_000_000L), 0, "technical_exit", 1,
            StrategyId: strategy.StrategyId, CandidateId: "prepared-AAA-once") {
            ExecutionReplay = new(ExecutionReplayPolicy.DailyRthSipPortfolioV1, ExecutionEventTime.ToUtc(Open - 86_400_000_000_000L),
                ExecutionEventTime.ToUtc(Open), ExecutionEventTime.ToUtc(Open + 60_000_000_000L), []) {
                EventExecutionEndUtc = ExecutionEventTime.ToUtc(Open + 300_000_000_000L)
            }
        };
        return new("source-binding-fixture", new(10000, 1, 25, 4, 0, 0, 1, true, MaxBarParticipationPct: 100),
            [strategy], [candidate], [], new Dictionary<string, string>());
    }

    /// <summary>Matches the adapter's retained configured cost and raw-price assumptions for direct replay comparison.</summary>
    private static ExecutionEventSimulationPolicy SimulationPolicy(EventPortfolioReplayRequest request) =>
        new(new(request.Portfolio.MaxBarParticipationPct, request.Strategies[0].Execution.SlippageBps, 0,
                request.Portfolio.ImpactBpsAtMaxParticipation, request.Portfolio.FixedBuyFee, request.Portfolio.FixedSellFee,
                request.Portfolio.SecFeeRate, request.Portfolio.FinraTafPerShare, request.Portfolio.FinraTafCap,
                MaximumTrackedOrderContracts: request.Portfolio.MaxExecutionOrderHistory),
            ExecutionProtectionActivation.NativeParentFullyFilled, 30_000_000_000L, 0, true, 1, 1, "raw-fixture");

    private sealed class Journal : IExecutionEventSink
    {
        public List<ExecutionEvent> Events { get; } = [];
        /// <summary>Retains only this test's ordinary execution journal for candidate identity assertions.</summary>
        public void Append(ExecutionEvent item) => Events.Add(item);
    }

    private sealed class EmptyFixtureMarketProvider : IMarketDataProvider
    {
        public int StockReads { get; private set; }
        /// <summary>No trading outcomes are measured here; empty historical inputs isolate source-binding lifecycle and reusable preparation.</summary>
        public async IAsyncEnumerable<OhlcvBar> GetBarsAsync(IReadOnlyCollection<string> tickers,
            IReadOnlyCollection<string> timeframes, DateTimeOffset start, DateTimeOffset end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (tickers.Contains("AAA")) StockReads++;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SipPortfolioExecutionSourceTests.Sources archives = new();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tf-source-binding-" + Guid.NewGuid().ToString("N"));
        public string ManifestPath => Path.Combine(Root, "execution-source.json");
        public string ResultPath => Path.Combine(Root, "result.json");
        public string CalendarReceiptPath => Path.Combine(Root, "calendar.receipt.json");
        public string CalendarReceiptSha256 { get; private set; } = String.Empty;
        public EventPortfolioSourceManifest Manifest { get; private set; } = null!;
        public BacktestRunConfig Run { get; private set; } = null!;

        /// <summary>Reuses physical archive fixtures, with genuine prior quotes and strictly later entry/stop prints.</summary>
        public static async Task<Fixture> CreateAsync(bool empty = false)
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.Root);
            var quote = JsonSerializer.SerializeToElement(new { t = SipNanosecondTime.Format(Open), bp = 100, ap = 101, bs = 1000, @as = 1000, bx = "Q", ax = "P", c = new[] { "R" }, z = "C" });
            var prefix = JsonSerializer.SerializeToElement(new { t = SipNanosecondTime.Format(Open - 1), bp = 100, ap = 101, bs = 1000, @as = 1000, bx = "Q", ax = "P", c = new[] { "R" }, z = "C" });
            var trades = Enumerable.Range(1, 3).Select(index => JsonSerializer.SerializeToElement(new {
                t = SipNanosecondTime.Format(Open + index * 1000), i = index, p = index == 1 ? 100.5m : 100m,
                s = 10000, x = "U", c = new[] { "@" }, z = "C"
            })).ToArray();
            var session = await fixture.archives.Session("AAA", Open, empty ? [] : [prefix], empty ? [] : [quote], empty ? [] : trades,
                sessionDurationNanoseconds: 23_400_000_000_000L);
            var reader = new SimpleYamlReader();
            var source = reader.ReadBacktestRun(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "swing-backtest-profile.yaml"));
            fixture.Run = source with { Tickers = ["AAA"], Universe = UniverseConfig.StaticUniverse,
                Engine = source.Engine with { ExecutionReplayPolicyId = ExecutionReplayPolicy.DailyRthSipPortfolioV1 },
                ResultsRoot = fixture.Root };
            await fixture.WriteCalendarAsync([0]);
            await fixture.WriteManifestAsync(new(1, SipPortfolioExecutionSourceTests.Policy(), [session],
                new(ExecutionProtectionActivation.NativeParentFullyFilled, 30_000_000_000L, 0, true, 1, 1, "raw-fixture"),
                fixture.CalendarReceiptPath, fixture.CalendarReceiptSha256));
            return fixture;
        }

        /// <summary>Creates an explicit captured calendar and receipt; dates come from fixture inputs rather than weekday inference.</summary>
        public async Task WriteCalendarAsync(int[] dayOffsets)
        {
            var dates = dayOffsets.Select(day => new DateOnly(2026, 5, 21).AddDays(day)).Order().ToArray();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(dates.Select(date => new {
                date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), open = "09:30", close = "16:00"
            }));
            await File.WriteAllBytesAsync(Path.Combine(Root, "calendar.json"), bytes);
            var receipt = JsonSerializer.SerializeToUtf8Bytes(new {
                HttpStatus = 200, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), RowCount = dates.Length,
                Start = dates[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), End = dates[^1].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                CapturedUtc = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero), OriginalHistoricalReceiptClaim = false
            });
            await File.WriteAllBytesAsync(CalendarReceiptPath, receipt);
            CalendarReceiptSha256 = Convert.ToHexStringLower(SHA256.HashData(receipt));
        }

        /// <summary>Provides a fully sealed later session for testing gaps between otherwise covered endpoints.</summary>
        public Task<SipPortfolioSessionArchive> EmptySessionAsync(int dayOffset) =>
            archives.Session("AAA", Open + dayOffset * 86_400_000_000_000L, [], [], [], sessionDurationNanoseconds: 23_400_000_000_000L);

        /// <summary>Freezes exact manifest bytes and updates only the test-owned run's expected hash.</summary>
        public async Task WriteManifestAsync(EventPortfolioSourceManifest manifest)
        {
            Manifest = manifest;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
            await File.WriteAllBytesAsync(ManifestPath, bytes);
            Run = Run with { Engine = Run.Engine with { ExecutionSourceManifestPath = ManifestPath,
                ExecutionSourceManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) } };
        }

        /// <summary>Writes a resolved single-stock job with fixture-owned data roots while retaining the exact frozen source manifest.</summary>
        public async Task<string> WriteRunConfigAsync()
        {
            var source = await File.ReadAllTextAsync(Path.Combine(TestRepository.FindRoot(), "configs", "backtest", "swing-backtest-profile.yaml"));
            source = source.Replace("\r\n", "\n", StringComparison.Ordinal);
            var strategySection = source.IndexOf("\nstrategies:", StringComparison.Ordinal);
            if (strategySection < 0) throw new InvalidOperationException("Fixture profile has no strategies section.");
            source = source[..strategySection] + "\nstrategies:\n" + String.Join("\n", Run.Strategies.Select(path => "  - " + path.Replace('\\', '/'))) + "\n";
            source = source.Replace("\nengine:\n", $"\nengine:\n  execution_replay_policy_id: {ExecutionReplayPolicy.DailyRthSipPortfolioV1}\n  execution_source_manifest_path: {ManifestPath.Replace('\\', '/')}\n  execution_source_manifest_sha256: {Run.Engine.ExecutionSourceManifestSha256}\n", StringComparison.Ordinal);
            source = source.Replace("  mode: unresolved_wishlist", "  mode: resolved_snapshot", StringComparison.Ordinal)
                .Replace("\nmarket_data:\n", "\ntickers:\n  - AAA\n\nmarket_data:\n", StringComparison.Ordinal);
            source = Regex.Replace(source, "(?m)^  raw_root:.*$", "  raw_root: " + Path.Combine(Root, "raw").Replace('\\', '/'));
            source = Regex.Replace(source, "(?m)^  normalized_root:.*$", "  normalized_root: " + Path.Combine(Root, "normalized").Replace('\\', '/'));
            source = Regex.Replace(source, "(?m)^  results_root:.*$", "  results_root: " + Root.Replace('\\', '/'));
            var path = Path.Combine(Root, "job.yaml");
            await File.WriteAllTextAsync(path, source);
            return path;
        }

        /// <summary>Creates a two-choice diagnostic search entirely in the fixture; no canonical strategy or market data is changed.</summary>
        public async Task<string> WriteOptimizerConfigAsync()
        {
            var configPath = await WriteRunConfigAsync();
            var text = await File.ReadAllTextAsync(configPath);
            var start = text.IndexOf("\ntime_window:\n", StringComparison.Ordinal);
            var end = text.IndexOf("\nmarket_data:\n", start, StringComparison.Ordinal);
            text = text[..start] + "\ntime_window:\n  type: fixed\n  lookback_days: 1\n  warmup_lookback_days: 0\n  start: 2026-05-21T13:30:00Z\n  end: 2026-05-21T20:00:00Z\n\ntickers:\n  - AAA\n" + text[end..];
            text = text.Replace("\nengine:\n", "\nengine:\n  precommitted_liquidation_source_utc: 2026-05-21T04:00:00Z\n", StringComparison.Ordinal)
                .Replace("  mode: unresolved_wishlist", "  mode: static", StringComparison.Ordinal);
            text = Regex.Replace(text, "(?m)^  provider:.*$", "  provider: csv");
            text = Regex.Replace(text, "(?m)^  results_root:.*$", "  results_root: " + Root.Replace('\\', '/'));
            await File.WriteAllTextAsync(configPath, text);
            var strategyPath = Path.Combine(Root, "optimizer-strategy.yaml");
            var strategy = await File.ReadAllTextAsync(Run.Strategies[0]);
            strategy = strategy.Replace("enable_atr_trailing_stop: true", "enable_atr_trailing_stop: false", StringComparison.Ordinal)
                .Replace("rule: price_above_sma", "rule: none", StringComparison.Ordinal);
            await File.WriteAllTextAsync(strategyPath, strategy);
            var path = Path.Combine(Root, "optimization.yaml");
            await File.WriteAllTextAsync(path, $"run_name: source-binding-search\nbase_strategy: {strategyPath.Replace('\\', '/')}\nbacktest_config: {configPath.Replace('\\', '/')}\nmetric: NetProfit\ntop_n_results: 2\nparameters:\n  exit_rules.target_r_multiple:\n    - 2.0\n    - 3.0\n");
            return path;
        }

        /// <summary>Removes only this fixture's verified absolute temporary directory and physical archives.</summary>
        public void Dispose()
        {
            archives.Dispose();
            var root = Path.GetFullPath(Root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("tf-source-binding-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped the owned temporary directory.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

using System.Text.Json;
using TradingFlow.Backtesting.Artifacts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Data.Candles;
using TradingFlow.Data.Csv;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Sessions;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Strategies;
using TradingFlow.Engine.Universe;
using TradingFlow.Data.Catalysts;

namespace TradingFlow.Backtesting;

public sealed record PreparedBacktestMarket(
    CandlePipelineResult MarketState,
    IReadOnlyList<OhlcvBar> BenchmarkBars,
    DateTimeOffset DataStart,
    DateTimeOffset EvaluationStart,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    PreparedMarketDataSummary? PreparedMarketData = null);

public sealed partial class BacktestRunner(
    SimpleYamlReader yamlReader,
    StrategyArtifactCatalog strategyArtifacts,
    IArtifactWriter? artifactWriter = null,
    ILogger<BacktestRunner>? logger = null,
    Func<BacktestRunConfig, IMarketDataProvider>? marketDataProviderFactory = null,
    IStrategyDecisionContextProvider? decisionContextProvider = null,
    IBacktestOpportunityObserver? opportunityObserver = null,
    EventPortfolioReplayBinding? eventPortfolioReplay = null,
    ConfiguredDataProviders? dataProviders = null,
    IPreparedMarketDataBindingStore? preparedDataBindingStore = null)
{
    private readonly ConfiguredDataProviders _dataProviders = dataProviders ?? ConfiguredDataProviders.FromEnvironment();
    private readonly IStrategyDecisionContextProvider? _decisionContextProvider = decisionContextProvider;
    private IHistoricalNewsPresenceProvider? _historicalNewsProvider;
    private FrozenBacktestEvidence? frozenEvidence;
    private Action<EngineConfig>? requireLiveEvidenceScope;
    private readonly IBacktestOpportunityObserver? _opportunityObserver = opportunityObserver;
    private bool eventBindingFromConfig;
    private string? reservedExecutionResultPath;
    private readonly IArtifactWriter _artifactWriter = artifactWriter ?? AtomicFileArtifactWriter.Instance;
    private readonly ILogger<BacktestRunner> _logger = logger ?? NullLogger<BacktestRunner>.Instance;
    private IRawArchiveWriter? _rawArchiveWriter;
    private string? _csvSourceRoot;
    private string? _transientArtifactRoot;
    private (string RawRoot, string NormalizedRoot)? logicalInputRoots;
    private PreparedMarketDataRequestReference? preparedDataReference;
    private PreparedMarketDataHandle? preparedData;
    private bool ownsPreparedData;
    private string? preparedComparisonId;
    private IReadOnlyDictionary<string, AuthorizedRuntimeStrategy> _validatedRuntimeStrategies =
        new Dictionary<string, AuthorizedRuntimeStrategy>(StringComparer.Ordinal);
    private readonly StrategySessionClock _sessionClock = new();
    private CandlePipelineEngine _candlePipeline = new(NullCandleStore.Instance);
    private readonly BacktestValidator _validator = new();

    /// <summary>Runs the selected configuration with a fresh execution identity or an explicitly admitted comparison data request.</summary>
    public async Task<BacktestResult> RunAsync(
        string configPath,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null,
        PreparedMarketDataRequestReference? preparedDataReference = null)
    {
        var run = yamlReader.ReadBacktestRun(configPath);
        return await RunAsync(
            configPath,
            GetResultPath(run),
            DateTimeOffset.UtcNow,
            cancellationToken,
            progress,
            preparedDataReference);
    }

    /// <summary>Validates immutable strategies and forwards the admitted input selection into an isolated execution.</summary>
    public Task<BacktestResult> RunAsync(
        string configPath,
        string resultPath,
        DateTimeOffset evaluationCutoffUtc,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null,
        PreparedMarketDataRequestReference? preparedDataReference = null) =>
        RunSelectedAsync(configPath, resultPath, evaluationCutoffUtc, cancellationToken, progress, preparedDataReference, null, null);

    /// <summary>Executes one worker's already verified graph while its mandatory owner guard remains live through every workspace.</summary>
    public Task<BacktestResult> RunAsync(string configPath, string resultPath, DateTimeOffset evaluationCutoffUtc,
        CancellationToken cancellationToken, FrozenBacktestEvidence verified, Action<EngineConfig> requireLiveScope,
        IProgress<BacktestProgress>? progress = null, PreparedMarketDataRequestReference? preparedDataReference = null)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(requireLiveScope);
        return RunSelectedAsync(configPath, resultPath, evaluationCutoffUtc, cancellationToken, progress, preparedDataReference,
            verified, requireLiveScope);
    }

    /// <summary>Reads and authorizes the selected strategies before creating an isolated execution workspace.</summary>
    private async Task<BacktestResult> RunSelectedAsync(string configPath, string resultPath, DateTimeOffset evaluationCutoffUtc,
        CancellationToken cancellationToken, IProgress<BacktestProgress>? progress,
        PreparedMarketDataRequestReference? preparedDataReference, FrozenBacktestEvidence? verified, Action<EngineConfig>? requireLiveScope)
    {
        var startedAt = DateTimeOffset.UtcNow;
        progress?.Report(BacktestProgress.StageOnly("loading_config", $"Loading run config {Path.GetFileName(configPath)}."));
        var run = yamlReader.ReadBacktestRun(configPath);
        if (preparedDataReference is null && run.TimeWindow.Type.Equals("rolling", StringComparison.OrdinalIgnoreCase))
        {
            run = run with { TimeWindow = run.TimeWindow with { End = run.TimeWindow.End ?? evaluationCutoffUtc } };
        }
        progress?.Report(BacktestProgress.StageOnly("loading_strategies", $"Loading {run.Strategies.Count} strategy config(s)."));
        var artifactValidator = new StrategyRunArtifactValidator(yamlReader, strategyArtifacts);
        var runtimeStrategies = run.Strategies
            .Select(path => artifactValidator.ReadAndValidateRuntimeArtifact(path, StrategySelectionMode.Backtest))
            .ToArray();
        return await RunValidatedAsync(run, runtimeStrategies, startedAt, resultPath, cancellationToken, progress, preparedDataReference,
            verified, requireLiveScope);
    }

    /// <summary>Executes explicit strategy definitions while retaining the same preparation and workspace lifetime rules.</summary>
    public async Task<BacktestResult> RunAsync(
        BacktestRunConfig run,
        StrategyDefinition[] strategies,
        DateTimeOffset startedAt,
        string resultPath,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null,
        PreparedMarketDataRequestReference? preparedDataReference = null)
    {
        run = await ResolveAdmittedRollingEndAsync(run, preparedDataReference, cancellationToken);
        var workspace = CreateWorkspace(run, resultPath);
        workspace.Runner.preparedDataReference = preparedDataReference;
        return await workspace.ExecuteAsync(() => workspace.Runner.RunCoreAsync(workspace.Run, strategies, startedAt,
            workspace.ResultPath, cancellationToken, progress));
    }

    /// <summary>Keeps authorized strategy identities and comparison inputs together for the complete durable attempt.</summary>
    internal async Task<BacktestResult> RunValidatedAsync(
        BacktestRunConfig run, AuthorizedRuntimeStrategy[] runtimeStrategies,
        DateTimeOffset startedAt, string resultPath, CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null,
        PreparedMarketDataRequestReference? preparedDataReference = null, FrozenBacktestEvidence? verified = null,
        Action<EngineConfig>? requireLiveScope = null)
    {
        run = await ResolveAdmittedRollingEndAsync(run, preparedDataReference, cancellationToken);
        var validated = runtimeStrategies.ToDictionary(
            artifact => artifact.Definition.StrategyId, StringComparer.Ordinal);
        var workspace = CreateWorkspace(run, resultPath, verified, requireLiveScope);
        workspace.Runner.preparedDataReference = preparedDataReference;
        workspace.Runner._validatedRuntimeStrategies = validated;
        var definitions = runtimeStrategies.Select(artifact => artifact.Definition).ToArray();
        return await workspace.ExecuteAsync(() => workspace.Runner.RunCoreAsync(
            workspace.Run, definitions, startedAt, workspace.ResultPath, cancellationToken, progress));
    }

    internal AuthorizedRuntimeStrategy ResolveRuntimeStrategy(StrategyDefinition strategy)
    {
        if (!_validatedRuntimeStrategies.TryGetValue(strategy.StrategyId, out var artifact))
        {
            if (_validatedRuntimeStrategies.Count > 0)
                throw new InvalidDataException("Backtest strategy has no validated artifact identity.");
            return StrategyDecisionRequestAssembler.CreateBacktestRuntimeStrategy(strategy, strategyArtifacts);
        }
        var actualHash = TradingFlow.Domain.Research.EvidenceCanonicalJson.ComputeSha256(
            new CanonicalStrategyDocument(
                artifact.Identity.StrategyId, artifact.Identity.SemanticVersion, strategy, artifact.AdmissionProfile));
        if (artifact.SelectionMode != StrategySelectionMode.Backtest ||
            !actualHash.Equals(artifact.Identity.ContentSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Backtest execution definition differs from its validated artifact identity.");
        return artifact with { Definition = strategy };
    }

    // Refuse unbound treatments before market preparation; all diagnostic books retain the captured evidence graph.
    private void RequireHistoricalDiagnosticEvidence(EngineConfig engine, IReadOnlyList<StrategyDefinition> strategies)
    {
        if (_historicalNewsProvider is null && !strategies.Any(strategy => strategy.EntryRules.HistoricalNewsDiagnostic is not null)) return;
        if (_historicalNewsProvider is null || frozenEvidence is null ||
            !String.Equals(engine.EvidenceManifestSha256, frozenEvidence.ManifestSha256, StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(engine.EvidenceDependencyGraphSha256, frozenEvidence.DependencyGraphSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Historical news diagnostic requires its selected manifest and complete frozen dependency graph before execution.");
        if (strategies.Any(strategy => HistoricalNewsPresenceEvaluation.HasStrictNewsRequirements(strategy.EntryRules)))
            throw new InvalidDataException("A frozen historical news diagnostic package cannot replace a selected strategy's strict news requirements.");
        foreach (var strategy in strategies.Where(strategy => strategy.EntryRules.HistoricalNewsDiagnostic is not null))
        {
            var runtime = ResolveRuntimeStrategy(strategy);
            if (runtime.SelectionMode != StrategySelectionMode.Backtest ||
                !HistoricalCompanyNewsDiagnosticPolicy.IsBoundTreatment(strategy.EntryRules.HistoricalNewsDiagnostic,
                    runtime.AdmissionProfile.HistoricalNewsDiagnosticPolicyId))
                throw new InvalidDataException("Historical diagnostic treatments require an exact registered Research strategy admission profile.");
        }
    }

    /// <summary>Keeps original provider responses at the configured raw root while isolating disposable projections and execution journals.</summary>
    internal BacktestRunWorkspace CreateWorkspace(BacktestRunConfig run, string resultPath,
        FrozenBacktestEvidence? verified = null, Action<EngineConfig>? requireLiveScope = null)
    {
        // Every nested operation uses this same clock; independently started runs get a new cutoff.
        run = FreezeRollingEnd(run);
        var guard = requireLiveScope ?? requireLiveEvidenceScope;
        var reusable = verified ?? (guard is not null ? frozenEvidence : null);
        var evidence = reusable is null ? FrozenBacktestEvidence.FromConfig(run.Engine)
            : FrozenBacktestEvidence.FromConfig(run.Engine, reusable, guard
                ?? throw new InvalidOperationException("Evidence reuse requires its live scope owner."));
        var contextProvider = _decisionContextProvider ?? evidence?.StrictContextProvider;
        if (contextProvider is not null && evidence?.HistoricalNewsProvider is not null)
            throw new InvalidDataException("Historical diagnostic evidence cannot be combined with a strict decision-context provider.");
        if (ExecutionReplayPolicy.IsEventPortfolio(run.Engine.ExecutionReplayPolicyId) &&
            run.Engine.ExecutionSourceManifestPath is not null)
            resultPath = ReserveResultPath(resultPath);
        var owner = Path.GetDirectoryName(Path.GetFullPath(resultPath))!;
        if (eventPortfolioReplay is not null && !eventBindingFromConfig && run.Engine.ExecutionSourceManifestPath is not null)
            throw new InvalidOperationException("Choose one explicit event binding or a configured source manifest; their provenance cannot be substituted silently.");
        // Prepared-market and optimizer children each need their own source pass and result paths.
        var executionBinding = eventBindingFromConfig || eventPortfolioReplay is null
            ? EventPortfolioSourceBinding.FromConfig(run, resultPath) : eventPortfolioReplay;
        var inputRoots = logicalInputRoots ?? (run.RawRoot, run.NormalizedRoot);
        // Nested optimizer/preparation work must reuse the original durable owner, never a parent's disposable spool.
        var archiveWriter = _rawArchiveWriter ?? new FileSystemRawArchiveWriter(new RawArchiveOptions(inputRoots.RawRoot));
        var inputs = Path.Combine(owner, ".run-inputs-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(inputs)) throw new IOException("Run input workspace already exists.");
        Directory.CreateDirectory(inputs);
        var scoped = new BacktestRunner(yamlReader, strategyArtifacts, _artifactWriter, _logger, marketDataProviderFactory,
            contextProvider, _opportunityObserver, executionBinding, _dataProviders, preparedDataBindingStore) {
            _csvSourceRoot = _csvSourceRoot ?? inputRoots.NormalizedRoot,
            _transientArtifactRoot = inputs,
            logicalInputRoots = inputRoots,
            _rawArchiveWriter = archiveWriter,
            _historicalNewsProvider = evidence?.HistoricalNewsProvider,
            frozenEvidence = evidence,
            requireLiveEvidenceScope = guard,
            preparedDataReference = preparedDataReference,
            preparedData = preparedData,
            preparedComparisonId = preparedComparisonId ?? "run-" + Guid.NewGuid().ToString("N"),
            _candlePipeline = new CandlePipelineEngine(new LocalFileCandleStore(Path.Combine(inputs, "candles"))),
            eventBindingFromConfig = executionBinding is not null && (eventPortfolioReplay is null || eventBindingFromConfig),
            reservedExecutionResultPath = executionBinding is not null && run.Engine.ExecutionSourceManifestPath is not null ? resultPath : null
        };
        return new(scoped, run with { RawRoot = inputRoots.RawRoot, NormalizedRoot = Path.Combine(inputs, "normalized") },
            inputs, owner, _logger, resultPath);
    }

    /// <summary>Validates a pinned book and publishes shared-engine results, including actual open entry fills in diagnostics.</summary>
    private async Task<BacktestResult> RunCoreAsync(BacktestRunConfig run, StrategyDefinition[] strategies,
        DateTimeOffset startedAt, string resultPath, CancellationToken cancellationToken, IProgress<BacktestProgress>? progress)
    {
        RequireHistoricalDiagnosticEvidence(run.Engine, strategies);
        RequireEventBinding(run);
        strategies = ApplyRunSessionPolicy(run, strategies);
        ValidateRun(run, strategies);
        RunUniverseValidator.RequireResolved(run);
        await EnsurePreparedDataAsync(run, strategies, cancellationToken, progress);
        var (resolvedRun, membership, promotionEvidence) =
            await ResolveUniverseAsync(run, cancellationToken, progress);
        run = resolvedRun;
        var preparedMarket = await PrepareMarketAsync(run, strategies, cancellationToken, progress);
        return await RunPreparedAsync(
            run,
            strategies,
            startedAt,
            resultPath,
            preparedMarket,
            cancellationToken,
            progress,
            membership,
            promotionEvidence);
    }

    /// <summary>
    /// Replaces the static ticker list with a point-in-time screened universe when the run
    /// opts into historical_screener mode. No-op for static runs, so existing configs are
    /// unaffected. The screened list is derived only from data before the evaluation start.
    /// </summary>
    private async Task<(
        BacktestRunConfig Run,
        UniverseMembership? Membership,
        UniversePromotionEvidence? PromotionEvidence)> ResolveUniverseAsync(
        BacktestRunConfig run,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress)
    {
        RunUniverseValidator.RequireResolved(run);
        if (run.Universe is { IsHistoricalScreener: false, DailyEligibility: not null } fixedList)
        {
            return (run, await ResolveDailyEligibilityAsync(run, fixedList, cancellationToken, progress), null);
        }

        if (run.Universe is not { } universe || !universe.IsHistoricalScreener)
        {
            return (run, null, null);
        }

        var provider = CreateProvider(run);
        var (_, evaluationStart, windowEnd) = ResolveWindow(run.TimeWindow);
        var dailyTimeframe = ResolveDailyTimeframe(run);

        // The candidate pool is either the static list or, for a broad honest pool, a Finviz
        // screener export. The Finviz screen is applied here; the no-lookahead price/liquidity
        // screen below still runs on top so within-window selection stays point-in-time.
        var configuredTickers = run.Tickers;
        var effectiveUniverse = universe;
        var universeSource = UniverseConfig.HistoricalScreenerMode;
        if (universe.UsesFinvizScreen)
        {
            var pool = await ResolveFinvizCandidatePoolAsync(universe, cancellationToken);
            var merged = universe.MergesCuratedAndFinviz
                ? universe.Candidates.Concat(pool)
                : pool;
            configuredTickers = merged
                .Select(ticker => ticker.Trim().ToUpperInvariant())
                .Where(ticker => ticker.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            progress?.Report(BacktestProgress.StageOnly(
                "finviz_candidates",
                $"Candidate pool: {configuredTickers.Count} ({pool.Count} from Finviz{(universe.MergesCuratedAndFinviz ? $" + {universe.Candidates.Count} curated" : String.Empty)})."));
            effectiveUniverse = universe with { Candidates = Array.Empty<string>() };
            universeSource = universe.MergesCuratedAndFinviz ? "historical_screener/curated+finviz" : "historical_screener/finviz";
        }

        progress?.Report(BacktestProgress.StageOnly(
            "resolving_universe",
            $"Screening point-in-time universe as of {evaluationStart:yyyy-MM-dd} (no-lookahead)."));

        var screener = new HistoricalScreenerUniverseProvider(provider);
        var request = new UniverseRequest(configuredTickers, effectiveUniverse, evaluationStart, dailyTimeframe, windowEnd);

        var resolution = await screener.ResolveAsync(request, cancellationToken);
        if (resolution.Tickers.Count == 0)
        {
            throw new InvalidOperationException(
                $"universe resolved zero tickers as of {resolution.AsOfDate:yyyy-MM-dd}. " +
                "Check the candidate pool (static list or Finviz screener), daily data availability, and screen thresholds.");
        }

        await PersistUniverseSnapshotAsync(run, resolution, cancellationToken);

        // Per-run (default): trade the as-of selection. Per-day: trade the union of every
        // ticker ever eligible, gated day-by-day at trade acceptance.
        UniverseMembership? membership = null;
        IReadOnlyList<string> effectiveTickers = resolution.Tickers;
        if (universe.IsPerDayRescreen)
        {
            membership = await screener.ResolveMembershipAsync(request, cancellationToken);
            var union = membership.Tickers.ToArray();
            if (union.Length == 0)
            {
                throw new InvalidOperationException(
                    "per_day universe has zero eligible tickers across the window. Check the candidate pool and screen thresholds.");
            }

            effectiveTickers = union;
            universeSource += "/per_day";
        }

        progress?.Report(BacktestProgress.StageOnly(
            "resolved_universe",
            universe.IsPerDayRescreen
                ? $"Per-day universe: {effectiveTickers.Count} ticker(s) ever eligible from {configuredTickers.Count} candidate(s)."
                : $"Universe: {resolution.Tickers.Count} ticker(s) selected from {configuredTickers.Count} candidate(s), {resolution.Rejections.Count} rejected."));

        var biasRisk = run.Validation.BiasRisk with
        {
            UniverseSource = universeSource,
            UniverseAsOfDate = resolution.AsOfDate
        };
        var resolvedRun = run with
        {
            Tickers = effectiveTickers,
            Validation = run.Validation with { BiasRisk = biasRisk }
        };
        // Current Finviz exports and static candidate lists are not historical provider
        // snapshots. They are useful for research, but cannot prove point-in-time membership.
        // Promotion therefore remains fail-closed until an archived snapshot ledger is supplied.
        return (resolvedRun, membership, null);
    }

    /// <summary>Prepares shared completed market inputs using the consistent price basis required by exact admitted strategies.</summary>
    public async Task<PreparedBacktestMarket> PrepareMarketAsync(
        BacktestRunConfig run,
        IReadOnlyCollection<StrategyDefinition> strategies,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null)
    {
        run = FreezeRollingEnd(run);
        strategies = ApplyRunSessionPolicy(run, strategies);
        ValidateRun(run, strategies);
        await EnsurePreparedDataAsync(run, strategies, cancellationToken, progress);
        if (ExecutionReplayPolicy.IsEventPortfolio(run.Engine.ExecutionReplayPolicyId) && eventPortfolioReplay is null)
            throw new InvalidOperationException("The SIP portfolio policy requires a frozen event source binding; candle fallback is forbidden.");

        var evidenceRules = strategies.Select(strategy => MarketEvidenceOperationalRules.ForExecutionTimingPolicy(
            ResolveRuntimeStrategy(strategy).AdmissionProfile.ExecutionTimingPolicy)).Distinct().ToArray();
        if (evidenceRules.Length != 1)
            throw new InvalidOperationException("Shared market preparation requires strategies with the same admitted price-adjustment basis.");
        var provider = CompletedBacktestDataProvider.Wrap(CreateProvider(run));
        var (dataStart, evaluationStart, windowEnd) = ResolveWindow(run.TimeWindow);

        var completedTickerCount = 0;
        var totalTickerCount = run.Tickers.Count;
        var requiredTimeframes = ResolveRequiredTimeframes(strategies);
        // SIP fills use physical events; observed five-minute bars are also needed for separately verified close valuation.
        if (run.Engine.ExecutionReplayPolicyId is ExecutionReplayPolicy.DailyRthFiveMinuteV1 or ExecutionReplayPolicy.DailyRthSipPortfolioV1)
            requiredTimeframes = requiredTimeframes.Append(ExecutionReplayPolicy.Timeframe).Distinct().ToArray();
        progress?.Report(new BacktestProgress(
            "market_pipeline",
            $"Preparing reusable market state for {totalTickerCount} ticker(s), {requiredTimeframes.Length} required timeframe(s).",
            null,
            completedTickerCount,
            totalTickerCount));

        var marketState = await _candlePipeline.RunAsync(
            new CandlePipelineRequest(
                run.Tickers,
                run.Intervals,
                requiredTimeframes,
                run.DerivedTimeframes.Source,
                dataStart,
                windowEnd,
                run.Engine.BoundedCapacity,
                run.Engine.WorkerCount,
                IncludeExtendedHours: true,
                ResolveExchangeTimezone(strategies),
                CreateCandleStoreContext(run),
                run.Engine.MaxRetainedReplayBars, evidenceRules[0],
                BacktestCalendarBounds.IntradayLastRequiredDate(run, windowEnd, ResolveExchangeTimezone(strategies))),
            provider,
            cancellationToken,
            new Progress<string>(message => progress?.Report(new BacktestProgress(
                "market_pipeline",
                message,
                null,
                Volatile.Read(ref completedTickerCount),
                totalTickerCount))));

        var retainedMarketBarCount = marketState.TickerStates.Values.Sum(state =>
            state.BarsByTimeframe.Values.Sum(bars => bars.Count));
        EnsureReplayRetentionLimit(
            retainedBars: 0,
            additionalBars: retainedMarketBarCount,
            run.Engine.MaxRetainedReplayBars);

        var benchmarkBars = ResolvePreparedBenchmarkBars(run, marketState) ??
            await LoadBenchmarkBarsAsync(
                run,
                provider,
                dataStart,
                windowEnd,
                retainedMarketBarCount,
                run.Engine.MaxRetainedReplayBars,
                cancellationToken);
        progress?.Report(new BacktestProgress(
            "market_pipeline",
            $"Prepared reusable market state for {marketState.TickerStates.Count}/{totalTickerCount} ticker(s).",
            null,
            totalTickerCount,
            totalTickerCount));

        return new PreparedBacktestMarket(
            marketState,
            benchmarkBars,
            dataStart,
            evaluationStart,
            evaluationStart,
            windowEnd,
            preparedData?.Summary);
    }

    public async Task<BacktestResult> RunPreparedAsync(
        BacktestRunConfig run,
        StrategyDefinition[] strategies,
        DateTimeOffset startedAt,
        string resultPath,
        PreparedBacktestMarket preparedMarket,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null,
        UniverseMembership? universeMembership = null,
        UniversePromotionEvidence? universePromotionEvidence = null)
    {
        if (_transientArtifactRoot is null || eventBindingFromConfig)
        {
            var workspace = CreateWorkspace(run, resultPath);
            workspace.Runner._validatedRuntimeStrategies = _validatedRuntimeStrategies;
            return await workspace.ExecuteAsync(() => workspace.Runner.RunPreparedCoreAsync(
                workspace.Run, strategies, startedAt, workspace.ResultPath, preparedMarket, cancellationToken,
                progress, universeMembership, universePromotionEvidence));
        }
        return await RunPreparedCoreAsync(run, strategies, startedAt, resultPath, preparedMarket,
            cancellationToken, progress, universeMembership, universePromotionEvidence);
    }

    /// <summary>Evaluates already prepared market state and publishes execution evidence with the original selected-data identity.</summary>
    private async Task<BacktestResult> RunPreparedCoreAsync(
        BacktestRunConfig run,
        StrategyDefinition[] strategies,
        DateTimeOffset startedAt,
        string resultPath,
        PreparedBacktestMarket preparedMarket,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null,
        UniverseMembership? universeMembership = null,
        UniversePromotionEvidence? universePromotionEvidence = null)
    {
        RequireEventBinding(run);
        strategies = ApplyRunSessionPolicy(run, strategies);
        ValidateRun(run, strategies);
        var preparedMarketBarCount = preparedMarket.MarketState.TickerStates.Values.Sum(state =>
            state.BarsByTimeframe.Values.Sum(bars => bars.Count));
        var benchmarkAliasesPreparedMarket = preparedMarket.MarketState.TickerStates.Values
            .SelectMany(state => state.BarsByTimeframe.Values)
            .Any(bars => ReferenceEquals(bars, preparedMarket.BenchmarkBars));
        EnsureReplayRetentionLimit(
            preparedMarketBarCount,
            benchmarkAliasesPreparedMarket ? 0 : preparedMarket.BenchmarkBars.Count,
            run.Engine.MaxRetainedReplayBars);
        if (ExecutionReplayPolicy.IsIntradayCandle(run.Engine.ExecutionReplayPolicyId))
        {
            EnsureStrategyWorkItemLimit(run.Tickers.Count, strategies.Length, run.Engine.MaxStrategyWorkItems);
            return await RunIntradayPreparedAsync(run, strategies.Single(), startedAt, resultPath,
                preparedMarket, cancellationToken, progress, universePromotionEvidence, universeMembership);
        }
        using var candidateHypothesisJournal = new ExecutionEventJournal(CreateExecutionJournalPath("candidate"));
        using var portfolioExecutionJournal = new ExecutionEventJournal(CreateExecutionJournalPath("portfolio"));
        var executionAuditor = new ExecutionAuditor(
            capacity: 1,
            sink: candidateHypothesisJournal,
            evidenceScope: "candidate_hypothesis");
        var catalystStreamer = new CatalystStreamer(CreateNewsProvider(run));

        var completedTickerCount = 0;
        var totalTickerCount = run.Tickers.Count;
        progress?.Report(new BacktestProgress(
            "preparing_ticker_contexts",
            $"Preparing {totalTickerCount} ticker context(s) with catalysts and indicators.",
            null,
            completedTickerCount,
            totalTickerCount));

        var tickerContexts = await BuildPreparedTickerContextsAsync(
            run,
            preparedMarket.MarketState,
            catalystStreamer,
            preparedMarket.DataStart,
            preparedMarket.WindowStart,
            preparedMarket.WindowEnd,
            cancellationToken,
            progress);
        var regimeCalendars = await BuildRegimeCalendarsAsync(
            run,
            strategies,
            preparedMarket.DataStart,
            preparedMarket.WindowEnd,
            cancellationToken);

        foreach (var strategy in strategies)
        {
            progress?.Report(BacktestProgress.StrategyGroup(strategy.StrategyName, totalTickerCount));
        }

        EnsureStrategyWorkItemLimit(
            run.Tickers.Count,
            strategies.Length,
            run.Engine.MaxStrategyWorkItems);
        var retainedCandidateBudget = new RetainedCandidateBudget(run.Engine.MaxRetainedCandidates);
        var workItems = run.Tickers
            .SelectMany(ticker => strategies.Select(strategy => new StrategyTickerWorkItem(ticker, strategy)))
            .ToArray();
        var completedWorkItemCount = 0;
        var totalWorkItemCount = workItems.Length;
        progress?.Report(new BacktestProgress(
            "running_strategy_matrix",
            $"Running {totalWorkItemCount} strategy/ticker evaluation work item(s).",
            null,
            completedWorkItemCount,
            totalWorkItemCount));

        var strategyTickerBatches = new System.Collections.Concurrent.ConcurrentBag<StrategyTickerBacktestBatch>();
        var strategyWorkItemTimeout = ResolveStrategyWorkItemTimeout(run.Engine.TickerTimeoutSeconds);
        await Parallel.ForEachAsync(
            workItems,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = ResolveEvaluationWorkerCount(run.Engine.WorkerCount, totalWorkItemCount),
                CancellationToken = cancellationToken
            },
            async (workItem, token) =>
            {
                progress?.Report(new BacktestProgress(
                    "running_strategy_ticker",
                    $"Evaluating {workItem.Strategy.StrategyName} on {workItem.Ticker}.",
                    workItem.Ticker,
                    Volatile.Read(ref completedWorkItemCount),
                    totalWorkItemCount,
                    workItem.Strategy.StrategyName));

                using var workItemCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                workItemCancellation.CancelAfter(strategyWorkItemTimeout);
                var candidateJournalPath = CreateCandidateAuditJournalPath(workItem.Strategy, workItem.Ticker);
                try
                {
                    strategyTickerBatches.Add(await ProcessPreparedStrategyTickerAsync(
                        run,
                        tickerContexts,
                        workItem.Strategy,
                        workItem.Ticker,
                        preparedMarket.WindowStart,
                        preparedMarket.WindowEnd,
                        universeMembership,
                        regimeCalendars.TryGetValue(workItem.Strategy.StrategyId, out var regimeCalendar)
                            ? regimeCalendar
                            : null,
                        executionAuditor,
                        candidateJournalPath,
                        retainedCandidateBudget,
                        workItemCancellation.Token));
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    strategyTickerBatches.Add(StrategyTickerBacktestBatch.Failed(
                        workItem.Ticker,
                        workItem.Strategy,
                        $"strategy_ticker_timeout_after_{(int)strategyWorkItemTimeout.TotalSeconds}s",
                        candidateAuditJournalPath: File.Exists(candidateJournalPath) ? candidateJournalPath : null));
                }

                var completed = Interlocked.Increment(ref completedWorkItemCount);
                progress?.Report(new BacktestProgress(
                    "finished_strategy_ticker",
                    $"Finished {workItem.Strategy.StrategyName} on {workItem.Ticker}.",
                    workItem.Ticker,
                    completed,
                    totalWorkItemCount,
                    workItem.Strategy.StrategyName));

                await Task.CompletedTask;
            });

        progress?.Report(BacktestProgress.StageOnly("building_results", "Building portfolio, strategy, and analyzer results."));
        var batches = strategyTickerBatches.ToArray();
        var tickerResults = run.Tickers
            .Select(ticker =>
            {
                if (!tickerContexts.TryGetValue(ticker, out var context) || context.ErrorMessage is not null)
                {
                    return TickerBacktestResult.Failed(
                        ticker,
                        new InvalidOperationException(context?.ErrorMessage ?? $"No prepared market state was produced for ticker {ticker}."));
                }

                var candidateCount = batches
                    .Where(batch => batch.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase))
                    .Sum(batch => batch.CandidateTrades.Count);
                return TickerBacktestResult.Completed(ticker, context.TotalPreparedBarCount, candidateCount);
            })
            .ToArray();
        var allBars = tickerContexts.Values
            .Where(context => context.ErrorMessage is null)
            .SelectMany(context => context.ProcessedBars)
            .ToArray();
        var candidates = batches
            .SelectMany(x => x.CandidateTrades)
            .OrderBy(x => x.EntryTimestamp)
            .ThenBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_opportunityObserver is not null)
            await _opportunityObserver.ObserveAsync(CaptureOpportunities(run.RunName, run.Portfolio,
                strategies, candidates, allBars, universeMembership, regimeCalendars), cancellationToken);
        var candidateAuditJournals = batches
            .Select(batch => batch.CandidateAuditJournalPath)
            .Where(path => !String.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
        var candidateDiagnostics = batches.Select(batch => batch.Diagnostics).ToArray();
        // Native daily books retain actual cutoff exposure; explicitly configured terminal exits remain required.
        var accounting = run.Engine.ExecutionReplayPolicyId is null && strategies.All(s => s.Execution.Timeframe == "1d" &&
            s.Session.ExchangeTimezone == "America/New_York" && !s.Session.IsContinuousMarket)
            ? new NativeDailyAccountingContext(preparedMarket.WindowStart.ToUniversalTime(), preparedMarket.WindowEnd.ToUniversalTime(),
                candidateSessionSchedules ?? throw new InvalidDataException("Native daily accounting requires authoritative sessions."),
                run.Engine.PrecommittedLiquidationSourceUtc is null) : null;
        var eventOutcome = run.Engine.ExecutionReplayPolicyId == ExecutionReplayPolicy.DailyRthSipPortfolioV1
            ? await ExecuteEventPortfolioAsync(run, strategies, candidates, allBars,
                universeMembership, regimeCalendars, portfolioExecutionJournal, cancellationToken)
            : accounting is not null ? BuildUnifiedPortfolioTradesWithAudit(run.Portfolio, strategies, candidates,
                universeMembership, regimeCalendars, allBars, portfolioExecutionJournal, accounting) : null;
        if (eventOutcome?.State is { } nativeState && batches.Any(batch => !batch.Succeeded))
            eventOutcome = eventOutcome with { State = NativeDailySessionLedger.MarkUnavailableFrom(nativeState,
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(preparedMarket.WindowStart,
                    TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime),
                "Selected work items failed: " + String.Join("; ", batches.Where(b => !b.Succeeded)
                    .Select(b => $"{b.Ticker} ({b.StrategyId}): {b.ErrorMessage}"))) };
        var unifiedPortfolio = BuildUnifiedPortfolioResult(
            run.Portfolio,
            strategies,
            candidates,
            allBars,
            universeMembership,
            regimeCalendars,
            portfolioExecutionJournal, eventOutcome);
        var unifiedCompletedTradesForAudit = unifiedPortfolio.CompletedTrades;
        var economicResultsComplete = unifiedPortfolio.EconomicResultsComplete &&
            batches.All(batch => batch.Succeeded);
        if (!economicResultsComplete)
        {
            unifiedPortfolio = SuppressUnifiedPortfolioEconomicClaims(unifiedPortfolio);
        }
        var strategyResults = BuildStrategyResults(
            run.Portfolio,
            strategies,
            candidates,
            allBars,
            candidateDiagnostics,
            universeMembership,
            regimeCalendars, strategies.Length == 1 ? eventOutcome : null, accounting);
        if (!economicResultsComplete)
        {
            strategyResults = strategyResults
                .Select(SuppressStrategyEconomicClaims)
                .ToArray();
        }
        var diagnostics = BuildDiagnostics(
            strategies,
            strategyResults,
            candidateDiagnostics);
        var bestStrategy = SelectBestActiveStrategy(strategyResults);
        var winner = bestStrategy is null
            ? null
            : new WinnerStrategySummary(
                bestStrategy.StrategyId,
                bestStrategy.StrategyName,
                bestStrategy.EndingCapital,
                bestStrategy.NetProfit,
                bestStrategy.TotalReturnPct,
                bestStrategy.AverageDailyReturnPct,
                bestStrategy.MaxDrawdownPct,
                bestStrategy.AcceptedTradeCount,
                bestStrategy.RejectedTradeCount);
        var validation = _validator.Validate(run, allBars, preparedMarket.BenchmarkBars, strategyResults);
        var missedMoves = BuildMissedMoveAudits(strategies, unifiedCompletedTradesForAudit, allBars,
            unifiedPortfolio.PortfolioState);
        var publishedEconomics = BuildPublishedEconomicSummary(
            run.Portfolio.StartingCapital,
            bestStrategy,
            winner,
            economicResultsComplete);
        var universePromotion = new UniversePromotionEligibilityValidator()
            .Validate(universePromotionEvidence);

        progress?.Report(BacktestProgress.StageOnly("writing_result", $"Writing result {Path.GetFileName(resultPath)}."));
        var result = new BacktestResult(
            run.RunName,
            resultPath,
            startedAt,
            DateTimeOffset.UtcNow,
            run.Portfolio.StartingCapital,
            publishedEconomics.EndingCapital,
            publishedEconomics.NetProfit,
            publishedEconomics.TotalReturnPct,
            publishedEconomics.AverageDailyReturnPct,
            bestStrategy?.TradingDayCount ?? 0,
            publishedEconomics.MaxDrawdownPct,
            publishedEconomics.Winner,
            tickerResults.Sum(x => x.ProcessedBarCount),
            bestStrategy?.CandidateTradeCount ?? 0,
            bestStrategy?.AcceptedTradeCount ?? 0,
            bestStrategy?.RejectedTradeCount ?? 0,
            publishedEconomics.WinningTradeCount,
            publishedEconomics.LosingTradeCount,
            strategyResults,
            tickerResults,
            validation,
            publishedEconomics.CompletedTrades,
            diagnostics,
            missedMoves,
            Array.Empty<FinalizedOrder>(),
            unifiedPortfolio,
            universePromotion,
            CandidateDecisionAudit: Array.Empty<BacktestCandidateDecisionAudit>(),
            ExecutionAudit: Array.Empty<BacktestExecutionAuditEvent>(),
            EconomicResultsComplete: economicResultsComplete,
            PreparedMarketData: preparedData?.Summary ?? preparedMarket.PreparedMarketData)
        {
            PortfolioState = unifiedPortfolio.PortfolioState,
            DecisionContextEvidence = frozenEvidence?.Provenance ?? (_decisionContextProvider as FrozenManifestDecisionContextProvider)?.Provenance,
            HistoryEligibility = batches.Where(b => b.HistoryEligibility is not null)
                .Select(b => b.HistoryEligibility!).OrderBy(h => h.StrategyId).ThenBy(h => h.Ticker).ToArray()
        };

        var artifacts = await WriteResultAsync(
            resultPath,
            result,
            run.Artifacts,
            candidateHypothesisJournal,
            portfolioExecutionJournal,
            unifiedCompletedTradesForAudit,
            candidateAuditJournals,
            totalWorkItemCount,
            batches,
            cancellationToken);
        result = result with
        {
            ResultPath = artifacts.ResultPath,
            CandidateDecisionAuditPath = artifacts.CandidateDecisionAuditPath,
            CandidateDecisionAudit = Array.Empty<BacktestCandidateDecisionAudit>(),
            ExecutionAuditPath = artifacts.PortfolioExecutionAuditPath,
            ExecutionAudit = Array.Empty<BacktestExecutionAuditEvent>(),
            CandidateHypothesisAuditPath = artifacts.CandidateHypothesisAuditPath,
            PortfolioExecutionAuditPath = artifacts.PortfolioExecutionAuditPath,
            ArtifactManifestPath = artifacts.ArtifactManifestPath,
            ArtifactReferences = artifacts.ArtifactReferences
        };
        candidateHypothesisJournal.Dispose();
        portfolioExecutionJournal.Dispose();
        foreach (var journalPath in candidateAuditJournals
                     .SelectMany(path => new[] { path, BacktestCandidateJournal.StatePath(path) })
                     .Append(candidateHypothesisJournal.Path)
                     .Append(portfolioExecutionJournal.Path))
        {
            try
            {
                File.Delete(journalPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                progress?.Report(BacktestProgress.StageOnly(
                    "cleanup_warning",
                    $"Result is complete, but candidate audit spool cleanup failed for {Path.GetFileName(journalPath)}: {exception.Message}"));
            }
        }
        progress?.Report(BacktestProgress.StageOnly("completed", "Backtest completed."));
        return result;
    }

    internal static StrategyBacktestResult SuppressStrategyEconomicClaims(
        StrategyBacktestResult result) =>
        result with
        {
            EndingCapital = result.StartingCapital,
            NetProfit = 0m,
            TotalReturnPct = 0m,
            AverageDailyReturnPct = 0m,
            MaxDrawdownPct = 0m,
            WinningTradeCount = 0,
            LosingTradeCount = 0,
            CompletedTrades = result.PortfolioState is null ? Array.Empty<BacktestTrade>() : result.CompletedTrades,
            EconomicResultsComplete = false
        };

    internal static UnifiedPortfolioBacktestResult SuppressUnifiedPortfolioEconomicClaims(
        UnifiedPortfolioBacktestResult result) =>
        result with
        {
            EndingCapital = result.StartingCapital,
            NetProfit = 0m,
            TotalReturnPct = 0m,
            MaxDrawdownPct = 0m,
            WinningTradeCount = 0,
            LosingTradeCount = 0,
            CompletedTrades = result.PortfolioState is null ? Array.Empty<BacktestTrade>() : result.CompletedTrades,
            EconomicResultsComplete = false
        };

    internal static PublishedBacktestEconomicSummary BuildPublishedEconomicSummary(
        decimal startingCapital,
        StrategyBacktestResult? bestStrategy,
        WinnerStrategySummary? winner,
        bool economicResultsComplete)
    {
        if (!economicResultsComplete)
        {
            return new PublishedBacktestEconomicSummary(
                startingCapital,
                0m,
                0m,
                0m,
                0m,
                null,
                0,
                0,
                Array.Empty<BacktestTrade>());
        }

        return new PublishedBacktestEconomicSummary(
            bestStrategy?.EndingCapital ?? startingCapital,
            bestStrategy?.NetProfit ?? 0m,
            bestStrategy?.TotalReturnPct ?? 0m,
            bestStrategy?.AverageDailyReturnPct ?? 0m,
            bestStrategy?.MaxDrawdownPct ?? 0m,
            winner,
            bestStrategy?.WinningTradeCount ?? 0,
            bestStrategy?.LosingTradeCount ?? 0,
            bestStrategy?.CompletedTrades ?? Array.Empty<BacktestTrade>());
    }

    private static StrategyDefinition[] ApplyRunSessionPolicy(BacktestRunConfig run, IReadOnlyCollection<StrategyDefinition> strategies)
    {
        _ = run;
        return strategies.ToArray();
    }

    /// <summary>Rejects incompatible run modes and exit policies before collecting or replaying market data.</summary>
    private static void ValidateRun(BacktestRunConfig run, IReadOnlyCollection<StrategyDefinition> strategies)
    {
        if (ExecutionReplayPolicy.IsIntradayCandle(run.Engine.ExecutionReplayPolicyId))
            ValidateIntradayRun(run, strategies);
        else
            foreach (var strategy in strategies) TechnicalExecutionEngine.RequireSupportedExitPolicy(strategy);
        if (run.Engine.ExecutionReplayPolicyId is not null && !ExecutionReplayPolicy.IsIntradayCandle(run.Engine.ExecutionReplayPolicyId))
            foreach (var strategy in strategies) ValidateFineExecutionPolicy(run, strategy);
        if (!run.Mode.Equals("backtest", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected mode backtest, got {run.Mode}.");
        }

        if (ExecutionReplayPolicy.IsEventPortfolio(run.Engine.ExecutionReplayPolicyId) && strategies.Count != 1)
            throw new InvalidOperationException("Run each SIP strategy/cohort in its own shared-capital portfolio.");
        if (strategies.Count == 0)
        {
            throw new InvalidOperationException("At least one strategy is required.");
        }

        if (!run.Engine.Pipeline.Equals("tpl", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported pipeline: {run.Engine.Pipeline}.");
        }

        if (run.Engine.MaxRetainedReplayBars <= 0)
        {
            throw new InvalidOperationException(
                "engine.max_retained_replay_bars must be greater than zero for a backtest.");
        }

        if (run.Engine.MaxStrategyWorkItems <= 0)
        {
            throw new InvalidOperationException(
                "engine.max_strategy_work_items must be greater than zero for a backtest.");
        }

        if (run.Engine.MaxRetainedCandidates <= 0)
        {
            throw new InvalidOperationException(
                "engine.max_retained_candidates must be greater than zero for a backtest.");
        }

        if (run.Portfolio.MaxConcurrentPositions <= 0)
        {
            throw new InvalidOperationException("portfolio.max_concurrent_positions must be greater than zero.");
        }

        if (run.Portfolio.MaxExecutionOrderHistory <= 0)
        {
            throw new InvalidOperationException(
                "portfolio.max_execution_order_history must be greater than zero.");
        }

        if (run.Mode.Equals("live", StringComparison.OrdinalIgnoreCase) &&
            run.Execution.AllowLiveOrders &&
            run.Execution.DryRun)
        {
            throw new InvalidOperationException("Live orders cannot be enabled while dry_run is true.");
        }
    }

    private static string[] ResolveRequiredTimeframes(IReadOnlyCollection<StrategyDefinition> strategies)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var strategy in strategies)
        {
            required.Add(strategy.Timeframe);
            required.Add(strategy.Execution.Timeframe);
            if (strategy.Confluence.Enabled)
            {
                required.Add(strategy.Confluence.Timeframe);
            }
        }

        return required.ToArray();
    }

    private static string ResolveExchangeTimezone(IReadOnlyCollection<StrategyDefinition> strategies)
    {
        return strategies
            .Select(strategy => strategy.Session.ExchangeTimezone)
            .FirstOrDefault(timezone => !String.IsNullOrWhiteSpace(timezone))
            ?? "America/New_York";
    }

    private static CandleStoreContext CreateCandleStoreContext(BacktestRunConfig run)
    {
        return new CandleStoreContext(
            String.IsNullOrWhiteSpace(run.Mode) ? "backtest" : run.Mode,
            run.RunName,
            run.Provider);
    }

    /// <summary>Resolves an absent rolling end once before preparation and replay without changing the logical window policy.</summary>
    private static BacktestRunConfig FreezeRollingEnd(BacktestRunConfig run) =>
        run.TimeWindow.Type.Equals("rolling", StringComparison.OrdinalIgnoreCase) && run.TimeWindow.End is null
            ? run with { TimeWindow = run.TimeWindow with { End = DateTimeOffset.UtcNow } }
            : run;

    /// <summary>Computes evaluation and warmup bounds from the execution's already resolved clock.</summary>
    private static (DateTimeOffset DataStart, DateTimeOffset EvaluationStart, DateTimeOffset End) ResolveWindow(TimeWindowConfig timeWindow)
    {
        if (timeWindow.Type.Equals("fixed", StringComparison.OrdinalIgnoreCase))
        {
            if (timeWindow.Start is null || timeWindow.End is null)
            {
                throw new InvalidOperationException("Fixed time windows require start and end.");
            }

            var warmupDays = Math.Max(timeWindow.WarmupLookbackDays, 0);
            var dataStart = warmupDays == 0
                ? timeWindow.Start.Value
                : timeWindow.Start.Value.AddDays(-warmupDays);
            return (dataStart, timeWindow.Start.Value, timeWindow.End.Value);
        }

        if (timeWindow.Type.Equals("rolling", StringComparison.OrdinalIgnoreCase))
        {
            var end = timeWindow.End ?? DateTimeOffset.UtcNow;
            var evaluationStart = timeWindow.Start ?? end.AddDays(-timeWindow.LookbackDays);
            var dataStart = timeWindow.WarmupLookbackDays <= 0
                ? evaluationStart
                : evaluationStart.AddDays(-timeWindow.WarmupLookbackDays);

            return (dataStart, evaluationStart, end);
        }

        throw new NotSupportedException($"Unsupported time_window.type: {timeWindow.Type}");
    }

    private async Task<IReadOnlyDictionary<string, PreparedTickerEvaluationContext>> BuildPreparedTickerContextsAsync(
        BacktestRunConfig run,
        CandlePipelineResult marketState,
        CatalystStreamer catalystStreamer,
        DateTimeOffset dataStart,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress)
    {
        var contexts = new System.Collections.Concurrent.ConcurrentDictionary<string, PreparedTickerEvaluationContext>(StringComparer.OrdinalIgnoreCase);
        var completedTickerCount = 0;
        await Parallel.ForEachAsync(
            run.Tickers,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = ResolveEvaluationWorkerCount(run.Engine.WorkerCount, run.Tickers.Count),
                CancellationToken = cancellationToken
            },
            async (ticker, token) =>
            {
                progress?.Report(new BacktestProgress(
                    "preparing_ticker_context",
                    $"Preparing reusable context for {ticker}.",
                    ticker,
                    Volatile.Read(ref completedTickerCount),
                    run.Tickers.Count));

                contexts[ticker] = await BuildPreparedTickerContextAsync(
                    run,
                    marketState,
                    catalystStreamer,
                    ticker,
                    dataStart,
                    windowStart,
                    windowEnd,
                    token);

                var completed = Interlocked.Increment(ref completedTickerCount);
                progress?.Report(new BacktestProgress(
                    "prepared_ticker_context",
                    $"Prepared reusable context for {ticker}.",
                    ticker,
                    completed,
                    run.Tickers.Count));
            });

        return contexts;
    }

    private async Task<PreparedTickerEvaluationContext> BuildPreparedTickerContextAsync(
        BacktestRunConfig run,
        CandlePipelineResult marketState,
        CatalystStreamer catalystStreamer,
        string ticker,
        DateTimeOffset dataStart,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!marketState.TickerStates.TryGetValue(ticker, out var tickerState))
            {
                var failure = marketState.Failures.TryGetValue(ticker, out var reason)
                    ? reason
                    : $"No prepared market state was produced for ticker {ticker}.";
                return PreparedTickerEvaluationContext.Failed(ticker, failure);
            }

            var snapshotsByTimeframe = tickerState.SnapshotsByTimeframe
                .ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            var catalysts = (await catalystStreamer.LoadTickerCatalystsAsync(ticker, dataStart, windowEnd, cancellationToken))
                .OrderBy(catalyst => catalyst.Timestamp)
                .ToArray();

            foreach (var timeframe in snapshotsByTimeframe.Keys)
            {
                CatalystSnapshotAttacher.AttachToSnapshots(snapshotsByTimeframe[timeframe], catalysts, cancellationToken);
            }

            return new PreparedTickerEvaluationContext(
                ticker,
                tickerState.BarsByTimeframe,
                snapshotsByTimeframe.ToDictionary(
                    x => x.Key,
                    x => (IReadOnlyList<IndicatorSnapshot>)x.Value,
                    StringComparer.OrdinalIgnoreCase),
                tickerState.AllBars
                    .Where(bar => bar.Timestamp >= windowStart && bar.Timestamp <= windowEnd)
                    .ToArray(),
                tickerState.BarsByTimeframe.Values.Sum(x => x.Count),
                catalysts,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!run.Engine.FailFast && exception is not AuditPersistenceException)
        {
            return PreparedTickerEvaluationContext.Failed(ticker, exception.Message);
        }
    }

    private async Task<StrategyTickerBacktestBatch> ProcessPreparedStrategyTickerAsync(
        BacktestRunConfig run,
        IReadOnlyDictionary<string, PreparedTickerEvaluationContext> tickerContexts,
        StrategyDefinition strategy,
        string ticker,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        UniverseMembership? universeMembership,
        RegimeCalendar? regimeCalendar,
        ExecutionAuditor executionAuditor,
        string candidateJournalPath,
        RetainedCandidateBudget retainedCandidateBudget,
        CancellationToken cancellationToken)
    {
        BacktestCandidateJournal? candidateJournal = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tickerContexts.TryGetValue(ticker, out var context) || context.ErrorMessage is not null)
            {
                var reason = context?.ErrorMessage ?? $"No prepared market state was produced for ticker {ticker}.";
                return StrategyTickerBacktestBatch.Failed(ticker, strategy, reason);
            }

            if (!context.BarsByTimeframe.TryGetValue(strategy.Timeframe, out var strategyBars) ||
                !context.SnapshotsByTimeframe.TryGetValue(strategy.Timeframe, out var strategySnapshots))
            {
                return StrategyTickerBacktestBatch.Failed(ticker, strategy,
                    $"missing_signal_timeframe: required {strategy.Timeframe} bars or indicator snapshots are absent.");
            }

            if (!context.BarsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionBars) ||
                !context.SnapshotsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionSnapshots))
            {
                return StrategyTickerBacktestBatch.Failed(ticker, strategy,
                    $"missing_execution_timeframe: required {strategy.Execution.Timeframe} bars or indicator snapshots are absent.");
            }

            var requiredWarmupBars = Math.Max(run.Engine.IndicatorWarmupBars, strategy.EntryRules.TechnicalFactor?.RequiredCompletedBars ?? 0);
            if (requiredWarmupBars < 2)
                throw new InvalidOperationException("Strategy decision warm-up must require at least two completed observations.");
            var historyEligibility = DescribeObservedHistoryEligibility(ticker, strategy.StrategyId, strategy.Timeframe,
                strategyBars, requiredWarmupBars, windowStart, windowEnd);
            var availableWarmupBars = historyEligibility.AvailableAtStart;
            if (availableWarmupBars < requiredWarmupBars &&
                !run.Engine.AllowLateWarmup)
            {
                return StrategyTickerBacktestBatch.Failed(
                    ticker,
                    strategy,
                    $"insufficient_indicator_warmup: {ticker} {strategy.Timeframe} has " +
                    $"{availableWarmupBars} completed pre-evaluation bars; " +
                    $"{requiredWarmupBars} required. Increase time_window.warmup_lookback_days.");
            }

            candidateJournal = new BacktestCandidateJournal(candidateJournalPath);

            var evaluation = await CreateStrategyCandidatesAsync(
                run,
                strategy,
                strategyBars,
                strategySnapshots,
                executionBars,
                executionSnapshots,
                context.BarsByTimeframe,
                context.SnapshotsByTimeframe,
                context.Catalysts,
                windowStart,
                windowEnd,
                universeMembership,
                regimeCalendar,
                candidateJournal,
                executionAuditor,
                retainedCandidateBudget,
                cancellationToken);

            return new StrategyTickerBacktestBatch(
                ticker,
                strategy.StrategyId,
                strategy.StrategyName,
                true,
                null,
                evaluation.Candidates,
                evaluation.CandidateDecisionAudit,
                candidateJournal.DurableJournalPath,
                evaluation.Diagnostics) { HistoryEligibility = historyEligibility };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            !run.Engine.FailFast &&
            exception is not (AuditPersistenceException or BacktestCandidateRetentionLimitExceededException))
        {
            return StrategyTickerBacktestBatch.Failed(
                ticker,
                strategy,
                exception.Message,
                candidateJournal?.DurableJournalPath is null ? candidateJournal?.Snapshot() ?? [] : [],
                candidateJournal?.DurableJournalPath);
        }
        finally
        {
            candidateJournal?.Dispose();
        }
    }

    private static TimeSpan ParseTimeframe(string timeframe) => TimeframeParser.Parse(timeframe);

    // Count real completed/available history only; this audit does not assert that all dependent indicator evidence is usable.
    internal static BacktestHistoryEligibility DescribeObservedHistoryEligibility(string ticker, string strategyId, string timeframe,
        IReadOnlyList<OhlcvBar> bars, int requiredBars, DateTimeOffset start, DateTimeOffset cutoff)
    {
        if (requiredBars < 2) throw new ArgumentOutOfRangeException(nameof(requiredBars));
        var duration = ParseTimeframe(timeframe);
        var availability = bars.Select(bar => bar.KnownAtUtc is { } known && known > bar.Timestamp.Add(duration)
            ? known : bar.Timestamp.Add(duration)).Order().ToArray();
        var before = availability.Count(time => time <= start);
        var completed = availability.Count(time => time <= cutoff);
        DateTimeOffset? first = completed >= requiredBars
            ? availability.FirstOrDefault(time => time >= start && time >= availability[requiredBars - 1] && time <= cutoff) : null;
        if (first == default(DateTimeOffset)) first = null;
        return new(ticker, strategyId, requiredBars, before, completed, first,
            first is null ? "still_warming_at_study_end" : "history_eligible");
    }

    private static bool IsDailyOrHigher(string timeframe)
    {
        return TimeframeParser.IsDailyOrHigher(timeframe);
    }

    private static int ResolveEvaluationWorkerCount(int configuredWorkerCount, int workItemCount)
    {
        if (workItemCount <= 0)
        {
            return 1;
        }

        if (configuredWorkerCount > 0)
        {
            return Math.Min(configuredWorkerCount, workItemCount);
        }

        return Math.Min(32, workItemCount);
    }

    private static TimeSpan ResolveStrategyWorkItemTimeout(int tickerTimeoutSeconds)
    {
        var configured = tickerTimeoutSeconds <= 0 ? 120 : tickerTimeoutSeconds;
        return TimeSpan.FromSeconds(Math.Max(configured, 30));
    }

    private static int FindFirstBarIndexAtOrAfter(IReadOnlyList<OhlcvBar> bars, DateTimeOffset timestamp)
    {
        for (var i = 0; i < bars.Count; i++)
        {
            if (bars[i].Timestamp >= timestamp)
            {
                return i;
            }
        }

        return bars.Count;
    }

    private static int FindFirstSignalIndexAtOrAfter(IReadOnlyList<OhlcvBar> bars, DateTimeOffset timestamp)
    {
        var index = FindFirstBarIndexAtOrAfter(bars, timestamp);
        return Math.Max(0, index - 1);
    }

    private int FindNextValidExecutionBarIndex(
        IReadOnlyList<OhlcvBar> bars,
        int startIndex,
        StrategyDefinition strategy)
    {
        var index = Math.Max(startIndex, 0);
        while (index < bars.Count &&
               !_sessionClock.ValidateExecutionWindow(bars[index].Timestamp, strategy.Execution.Timeframe, strategy.Session))
        {
            index++;
        }

        return index;
    }

    /// <summary>Evaluates completed evidence through the shared kernel and retains stable candidate identities for the chosen execution policy.</summary>
    private async Task<StrategyCandidateEvaluation> CreateStrategyCandidatesAsync(
        BacktestRunConfig run,
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        IReadOnlyList<OhlcvBar> executionBars,
        IReadOnlyList<IndicatorSnapshot> executionSnapshots,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> barsByTimeframe,
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe,
        IReadOnlyCollection<CatalystEvent> catalysts,
        DateTimeOffset evaluationStart,
        DateTimeOffset evaluationEnd,
        UniverseMembership? universeMembership,
        RegimeCalendar? regimeCalendar,
        BacktestCandidateJournal candidateJournal,
        ExecutionAuditor executionAuditor,
        RetainedCandidateBudget retainedCandidateBudget,
        CancellationToken cancellationToken)
    {
        var candidates = new List<BacktestCandidateTrade>();
        var diagnostics = new StrategyCandidateDiagnostics(strategy.StrategyId, strategy.StrategyName);
        // Prepared inputs can cover a larger comparison window; never inspect its later execution suffix.
        var executablePrefix = executionBars.Select((bar, index) => (bar, index))
            .Where(item => item.bar.Timestamp.Add(ParseTimeframe(strategy.Execution.Timeframe)) <= evaluationEnd &&
                (item.bar.KnownAtUtc is null || item.bar.KnownAtUtc <= evaluationEnd)).ToArray();
        executionSnapshots = executablePrefix.Select(item => executionSnapshots[item.index]).ToArray();
        executionBars = executablePrefix.Select(item => item.bar).ToArray();
        // The snapshot at index n-1 already contains n completed bars when its availability time arrives.
        var startIndex = Math.Max(Math.Max(run.Engine.IndicatorWarmupBars, strategy.EntryRules.TechnicalFactor?.RequiredCompletedBars ?? 0) - 1, 1);
        var runtimeStrategy = ResolveRuntimeStrategy(strategy);
        var terminal = run.Engine.PrecommittedLiquidationSourceUtc;
        var terminalProfile = runtimeStrategy.AdmissionProfile.ExecutionTimingPolicy is "completed-bar-precommitted-terminal-open-v4" or "completed-bar-precommitted-terminal-owned-v5";
        if (terminal.HasValue != terminalProfile)
            throw new InvalidOperationException("Terminal-open policy and immutable timing profile must agree.");
        if (terminal is { } requested)
        {
            if (run.TimeWindow.Type != "fixed") throw new InvalidOperationException("Terminal-open research needs a frozen fixed window.");
            BacktestDecisionTiming.ValidateTerminalOpen(requested, evaluationStart, evaluationEnd,
                strategy.Execution.Timeframe, strategy.Session, candidateSessionSchedules);
            if (!executionBars.Any(bar => bar.Timestamp == requested))
                throw new InvalidOperationException("Prerecorded terminal source slice is missing; complete economics are unavailable.");
        }
        var candidateDecisions = new StrategyCandidateDecisionOrchestrator(
            new StrategyDecisionKernel(),
            candidateJournal);
        var simulationRun = BacktestCandidateJournal.CreateRun(
            new { Run = CandidateIdentityConfiguration(run), Strategy = runtimeStrategy.Identity, Ticker = snapshots[0].Ticker },
            $"{run.RunName}:{runtimeStrategy.Identity.StrategyId}:{snapshots[0].Ticker}",
            evaluationStart);

        for (var i = startIndex; i < snapshots.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = snapshots[i];
            var signalAvailableTimestamp = snapshot.Timestamp.Add(ParseTimeframe(strategy.Timeframe));
            if (signalAvailableTimestamp < evaluationStart)
            {
                continue;
            }

            if (signalAvailableTimestamp > evaluationEnd)
            {
                break;
            }

            diagnostics.EvaluatedBarCount++;
            var discoveryExpiry = BacktestDecisionTiming.ResolveExpiry(
                signalAvailableTimestamp, strategy.Timeframe, strategy.Session, candidateSessionSchedules);
            if (discoveryExpiry > evaluationEnd)
            {
                discoveryExpiry = evaluationEnd;
            }

            if (discoveryExpiry <= signalAvailableTimestamp)
            {
                diagnostics.IncrementRejection("candidate_window_not_available");
                continue;
            }

            var setupKey = StrategyDecisionRequestAssembler.CreateSetupKey(
                strategy.StrategyId,
                strategy.Timeframe,
                snapshot.Timestamp);
            var discovery = StrategyDecisionRequestAssembler.CreateBacktestDiscovery(
                snapshot.Ticker,
                setupKey,
                signalAvailableTimestamp,
                discoveryExpiry);
            var decisionTimes = executionBars
                .Select(bar => bar.Timestamp.Add(ParseTimeframe(strategy.Execution.Timeframe)))
                .Where(timestamp => timestamp >= signalAvailableTimestamp &&
                    timestamp < discoveryExpiry &&
                    timestamp <= evaluationEnd)
                .Prepend(signalAvailableTimestamp)
                .Distinct()
                .OrderBy(timestamp => timestamp)
                .ToArray();
            var candidateState = StrategyCandidateState.DataWarming;
            string? lastReason = null;
            var candidateCreated = false;

            foreach (var decisionTime in decisionTimes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var eligibilityDay = DateOnly.FromDateTime(decisionTime.UtcDateTime);
                var universeEligible = universeMembership is null ||
                    universeMembership.IsMember(snapshot.Ticker, eligibilityDay);
                var regimeEligible = regimeCalendar is null || regimeCalendar.IsOn(decisionTime);
                var universeEvidence = new StrategyEligibilityEvidence(
                    universeEligible,
                    universeMembership is null ? "static_run_universe" : "historical_universe_membership",
                    $"{snapshot.Ticker}:{eligibilityDay:yyyy-MM-dd}",
                    decisionTime,
                    JsonSerializer.Serialize(new { snapshot.Ticker, eligibilityDay, universeEligible }));
                var regimeEvidence = new StrategyEligibilityEvidence(
                    regimeEligible,
                    regimeCalendar is null ? "strategy_has_no_regime_gate" : "historical_regime_calendar",
                    $"{strategy.StrategyId}:{eligibilityDay:yyyy-MM-dd}",
                    decisionTime,
                    JsonSerializer.Serialize(new { strategy.StrategyId, eligibilityDay, regimeEligible }));
                var request = StrategyDecisionRequestAssembler.Create(
                    runtimeStrategy,
                    snapshot.Ticker,
                    barsByTimeframe,
                    snapshotsByTimeframe,
                    catalysts,
                    decisionTime,
                    discovery,
                    universeEvidence,
                    regimeEvidence,
                    candidateState,
                    0,
                    setupKey,
                    signalAvailableTimestamp,
                    discoveryExpiry,
                    run.Engine.IndicatorWarmupBars,
                    simulationRun.RunId,
                    strategy.EntryRules.TechnicalFactor is { Metric: TechnicalFactorRules.RelativeRoc } factor &&
                        factorReferenceBars.TryGetValue(factor.BenchmarkSymbol, out var reference) ? reference : null,
                    strategy.EntryRules.TechnicalFactor is { Metric: TechnicalFactorRules.RelativeRoc } expectedFactor
                        ? TechnicalFactorCalculator.ExpectedCompletedSessions(candidateSessionSchedules
                            ?? throw new InvalidDataException("Relative strength requires an authoritative calendar."),
                            decisionTime, expectedFactor.LookbackBars + 1) : null,
                    await StrategyDecisionRequestAssembler.ResolveDrawdownContextAsync(_decisionContextProvider,
                        snapshot.Ticker, decisionTime, barsByTimeframe, "backtest", cancellationToken),
                    strategy.EntryRules.HistoricalNewsDiagnostic is not null && _historicalNewsProvider is not null
                        ? await _historicalNewsProvider.ResolveAsync(snapshot.Ticker, decisionTime, cancellationToken) : null);
                var persistedDecision = await candidateDecisions.EvaluateAsync(
                    simulationRun,
                    request,
                    cancellationToken);
                var decision = persistedDecision.Decision
                    ?? throw new InvalidOperationException(
                        "Backtest candidate became terminal before its current decision was evaluated.");
                candidateState = persistedDecision.Candidate.State;
                lastReason = decision.NoEntryReason;
                if (!decision.IsTriggered ||
                    decision.OrderPlan is not { } canonicalOrderPlan ||
                    decision.Signal is not { } signal)
                {
                    if (decision.State is StrategyCandidateState.Rejected or
                        StrategyCandidateState.Expired or
                        StrategyCandidateState.DataError)
                    {
                        break;
                    }

                    continue;
                }

                var plannedEntryIndex = FindNextValidExecutionBarIndex(
                    executionBars,
                    FindFirstBarIndexAtOrAfter(executionBars, canonicalOrderPlan.TriggeredAtUtc),
                    strategy);
                if (plannedEntryIndex >= executionBars.Count ||
                    executionBars[plannedEntryIndex].Timestamp >= discoveryExpiry)
                {
                    lastReason = "no_next_executable_bar_before_expiry";
                    break;
                }

                if (terminal is { } cutoff && executionBars[plannedEntryIndex].Timestamp >= cutoff)
                {
                    lastReason = "precommitted_terminal_entry_cutoff";
                    break;
                }

                var candidate = run.Engine.ExecutionReplayPolicyId == ExecutionReplayPolicy.DailyRthSipPortfolioV1
                    ? CreateEventExecutionCandidate(persistedDecision.Candidate.CandidateId.ToString("D"), run, strategy,
                        signal, canonicalOrderPlan, executionBars, executionSnapshots, plannedEntryIndex, executionAuditor)
                    : run.Engine.ExecutionReplayPolicyId is not null
                    ? CreateFineExecutionCandidate(persistedDecision.Candidate.CandidateId.ToString("D"), run, strategy,
                        signal, canonicalOrderPlan, executionBars, executionSnapshots,
                        barsByTimeframe[ExecutionReplayPolicy.Timeframe], plannedEntryIndex, executionAuditor)
                    : CreateCandidate(
                    persistedDecision.Candidate.CandidateId.ToString("D"),
                    run,
                    strategy,
                    signal,
                    canonicalOrderPlan,
                    executionBars,
                    executionSnapshots,
                    executionAuditor,
                    cancellationToken,
                    plannedEntryIndex);
                if (candidate is null)
                {
                    lastReason = "no_next_bar_or_invalid_stop";
                    break;
                }

                retainedCandidateBudget.Reserve();
                // Bind execution evidence to the persisted canonical decision identity rather than a display/definition alias.
                var decisionStrategyId = persistedDecision.Candidate.SelectedStrategy;
                if (String.IsNullOrWhiteSpace(decisionStrategyId))
                    throw new InvalidDataException("Triggered candidate is missing its persisted decision strategy identity.");
                var finalized = candidate.Value.Candidate with { DecisionStrategyId = decisionStrategyId };
                if (run.Engine.ExecutionReplayPolicyId is null && terminal is { } boundary && finalized.ExitTimestamp == boundary && !IsPriceTriggeredExitReason(finalized.ExitReason))
                    finalized = finalized with { ExitReason = "scheduled_study_exit", ExitPrice = executionBars[candidate.Value.ExitIndex].Open };
                candidates.Add(finalized);
                candidateCreated = true;
                break;
            }

            if (!candidateCreated)
            {
                diagnostics.IncrementRejection(lastReason ?? "execution_trigger_not_ready");
            }
        }

        diagnostics.CandidateTradeCount = candidates.Count;
        return new StrategyCandidateEvaluation(
            candidates,
            candidateJournal.DurableJournalPath is null ? candidateJournal.Snapshot() : [],
            diagnostics);
    }

    private static string GetResultPath(BacktestRunConfig run)
    {
        var owner = Environment.GetEnvironmentVariable("TRADINGFLOW_RESULT_OWNER");
        var safeOwner = String.IsNullOrWhiteSpace(owner) ? "shared" : SanitizePathSegment(owner);
        return Path.Combine(run.ResultsRoot, safeOwner, "portfolio", $"{run.RunName}.json");
    }

    /// <summary>Hashes the accepted logical inputs, including resolved membership, while excluding random per-attempt spool locations.</summary>
    private BacktestRunConfig CandidateIdentityConfiguration(BacktestRunConfig run) => logicalInputRoots is { } roots
        ? run with { RawRoot = roots.RawRoot, NormalizedRoot = roots.NormalizedRoot } : run;

    private string TransientArtifactRoot => _transientArtifactRoot
        ?? throw new InvalidOperationException("Audit spools require an owned run workspace.");

    internal string CreateExecutionJournalPath(string kind) =>
        Path.Combine(TransientArtifactRoot,
            ".execution-journal", $"{kind}-{Guid.NewGuid():N}.ndjson");

    internal string CreateCandidateAuditJournalPath(
        StrategyDefinition strategy,
        string ticker)
    {
        // The workspace GUID already isolates owners/runs. Extra hierarchy risks Windows SQLite path limits.
        var directory = Path.Combine(TransientArtifactRoot, ".candidate-journal");
        var strategyKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(strategy.StrategyId)))[..12].ToLowerInvariant();
        return Path.Combine(directory, $"s-{strategyKey}-{SanitizePathSegment(ticker)}-{Guid.NewGuid():N}.ndjson");
    }

    /// <summary>Publishes immutable results and journals, including SIP source evidence, into one hashed artifact set.</summary>
    private async Task<BacktestArtifactWriteResult> WriteResultAsync(
        string resultPath,
        BacktestResult result,
        ArtifactRetentionConfig artifacts,
        ExecutionEventJournal candidateHypothesisJournal,
        ExecutionEventJournal portfolioExecutionJournal,
        IReadOnlyList<BacktestTrade> unifiedCompletedTrades,
        IReadOnlyList<string> candidateAuditJournals,
        int expectedWorkItemCount,
        IReadOnlyList<StrategyTickerBacktestBatch> batches,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        var exclusivePath = reservedExecutionResultPath == resultPath ? resultPath : ReserveResultPath(resultPath);
        var artifactRoot = Path.GetDirectoryName(exclusivePath)!;
        var artifactSetId = Guid.NewGuid().ToString("D");
        var persistedResult = BacktestArtifactProjector.Project(
            result with { ResultPath = exclusivePath },
            artifacts);
        var candidateAuditPath = Path.ChangeExtension(exclusivePath, ".candidate-decisions.json");
        long candidateAuditCount = 0;
        await _artifactWriter.WriteStreamExclusiveAsync(
            candidateAuditPath,
            async (stream, token) =>
            {
                candidateAuditCount = await CandidateAuditExporter.WriteAsync(
                    stream, candidateAuditJournals, token);
            },
            cancellationToken);
        var candidateHypothesisAuditPath = Path.ChangeExtension(exclusivePath, ".candidate-hypotheses.json");
        await _artifactWriter.WriteStreamExclusiveAsync(
            candidateHypothesisAuditPath,
            (stream, token) => JsonSerializer.SerializeAsync(stream,
                candidateHypothesisJournal.ReadAsync(token),
                cancellationToken: token),
            cancellationToken);
        var portfolioExecutionAuditPath = Path.ChangeExtension(exclusivePath, ".portfolio-execution.json");
        await _artifactWriter.WriteStreamExclusiveAsync(
            portfolioExecutionAuditPath,
            (stream, token) => JsonSerializer.SerializeAsync(stream,
                portfolioExecutionJournal.ReadAsync(token),
                cancellationToken: token),
            cancellationToken);
        var unifiedCompletedTradesPath = Path.ChangeExtension(exclusivePath, ".unified-completed-trades.json");
        await _artifactWriter.WriteStreamExclusiveAsync(
            unifiedCompletedTradesPath,
            (stream, token) => JsonSerializer.SerializeAsync(
                stream,
                unifiedCompletedTrades,
                cancellationToken: token),
            cancellationToken);
        var manifestPath = Path.ChangeExtension(exclusivePath, ".manifest.json");
        var evidenceReferences = new List<BacktestArtifactReference>
        {
            await BacktestArtifactIntegrity.CreateReferenceAsync(artifactRoot, "candidate_decisions", candidateAuditPath, candidateAuditCount, cancellationToken),
            await BacktestArtifactIntegrity.CreateReferenceAsync(artifactRoot, "candidate_hypotheses", candidateHypothesisAuditPath, candidateHypothesisJournal.Count, cancellationToken),
            await BacktestArtifactIntegrity.CreateReferenceAsync(artifactRoot, "unified_portfolio_execution", portfolioExecutionAuditPath, portfolioExecutionJournal.Count, cancellationToken),
            await BacktestArtifactIntegrity.CreateReferenceAsync(artifactRoot, "unified_completed_trades", unifiedCompletedTradesPath, unifiedCompletedTrades.Count, cancellationToken)
        };
        // Preserve the independent valuation grid alongside native fills and source-session marks.
        if (result.PortfolioState is not null)
        {
            var calendarPath = Path.ChangeExtension(exclusivePath, ".native-daily-calendar.json");
            await _artifactWriter.WriteStreamExclusiveAsync(calendarPath,
                (stream, token) => JsonSerializer.SerializeAsync(stream,
                    candidateSessionSchedules ?? throw new InvalidDataException("Native accounting calendar is missing."),
                    cancellationToken: token), cancellationToken);
            evidenceReferences.Add(await BacktestArtifactIntegrity.CreateReferenceAsync(artifactRoot,
                "native_daily_calendar", calendarPath, 1, cancellationToken));
        }
        // Preserve SIP inputs and source/outcome proof in the same hashed artifact set as ordinary execution journals.
        var eventArtifacts = new[] {
            ("event_source_manifest", ".event-source-manifest.json"),
            ("event_exchange_calendar_receipt", ".event-calendar.receipt.json"),
            ("event_exchange_calendar", ".event-calendar.json"),
            ("event_portfolio_request", ".event-portfolio-request.json"),
            ("event_portfolio_source_audit", ".event-portfolio-source-audit.json"),
            ("event_portfolio_outcome", ".event-portfolio-outcome.json") };
        if (eventArtifacts.Any(item => File.Exists(Path.ChangeExtension(resultPath, item.Item2))))
        {
            foreach (var (kind, extension) in eventArtifacts)
            {
                var path = Path.ChangeExtension(resultPath, extension);
                if (!File.Exists(path)) throw new InvalidDataException("SIP source evidence is incomplete; result publication is withheld.");
                long count = 1;
                if (kind == "event_exchange_calendar")
                {
                    using var calendar = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
                    count = calendar.RootElement.GetArrayLength();
                }
                evidenceReferences.Add(await BacktestArtifactIntegrity.CreateReferenceAsync(artifactRoot, kind, path, count, cancellationToken));
            }
        }
        var rawJournalDirectory = Path.Combine(
            artifactRoot,
            $"{Path.GetFileNameWithoutExtension(exclusivePath)}.artifacts",
            "candidate-raw");
        Directory.CreateDirectory(rawJournalDirectory);
        for (var index = 0; index < candidateAuditJournals.Count; index++)
        {
            var journalPath = candidateAuditJournals[index];
            var archivedJournalPath = Path.Combine(
                rawJournalDirectory,
                $"{index:0000}-{Path.GetFileName(journalPath)}");
            await _artifactWriter.WriteStreamExclusiveAsync(
                archivedJournalPath,
                async (stream, token) =>
                {
                    await using var input = new FileStream(
                        journalPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        64 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(stream, token);
                },
                cancellationToken);
            evidenceReferences.Add(await BacktestArtifactIntegrity.CreateReferenceAsync(
                artifactRoot,
                "candidate_raw_journal",
                archivedJournalPath,
                await CountLinesAsync(archivedJournalPath, cancellationToken),
                cancellationToken));
        }
        persistedResult = persistedResult with
        {
            CandidateDecisionAuditPath = candidateAuditPath,
            CandidateDecisionAudit = Array.Empty<BacktestCandidateDecisionAudit>(),
            ExecutionAuditPath = portfolioExecutionAuditPath,
            ExecutionAudit = Array.Empty<BacktestExecutionAuditEvent>(),
            CandidateHypothesisAuditPath = candidateHypothesisAuditPath,
            PortfolioExecutionAuditPath = portfolioExecutionAuditPath,
            ArtifactManifestPath = manifestPath,
            ArtifactReferences = evidenceReferences
        };
        await _artifactWriter.WriteStreamExclusiveAsync(
            exclusivePath,
            (stream, token) => JsonSerializer.SerializeAsync(stream, persistedResult,
                cancellationToken: token),
            cancellationToken);
        var manifestArtifacts = evidenceReferences.Append(
            await BacktestArtifactIntegrity.CreateReferenceAsync(artifactRoot, "backtest_result", exclusivePath, 1, cancellationToken)).ToArray();
        var failedWorkItems = batches
            .Where(batch => !batch.Succeeded)
            .Select(batch => $"{batch.StrategyId}:{batch.Ticker}:{batch.ErrorMessage}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var coverage = new BacktestArtifactCoverage(
            expectedWorkItemCount,
            batches.Count(batch => batch.Succeeded),
            failedWorkItems.Length,
            failedWorkItems,
            result.UnifiedPortfolio?.ExecutionFailures?.Count ?? 0,
            result.UnifiedPortfolio?.ExecutionFailures?
                .OrderBy(failure => failure.CandidateId, StringComparer.Ordinal)
                .ToArray());
        await _artifactWriter.WriteStreamExclusiveAsync(
            manifestPath,
            (stream, token) => JsonSerializer.SerializeAsync(stream,
                new BacktestArtifactManifest(
                    1,
                    artifactSetId,
                    result.RunName,
                    DateTimeOffset.UtcNow,
                    true,
                    coverage,
                    manifestArtifacts),
                cancellationToken: token),
            cancellationToken);
        return new BacktestArtifactWriteResult(
            exclusivePath,
            candidateAuditPath,
            candidateHypothesisAuditPath,
            portfolioExecutionAuditPath,
            manifestPath,
            evidenceReferences);
    }

    private sealed record BacktestArtifactWriteResult(
        string ResultPath,
        string CandidateDecisionAuditPath,
        string CandidateHypothesisAuditPath,
        string PortfolioExecutionAuditPath,
        string ArtifactManifestPath,
        IReadOnlyList<BacktestArtifactReference> ArtifactReferences);

    private static async Task<long> CountLinesAsync(string path, CancellationToken cancellationToken)
    {
        long count = 0;
        using var reader = new StreamReader(path);
        while (await reader.ReadLineAsync(cancellationToken) is not null) count++;
        return count;
    }

    /// <summary>Chooses one unused prefix before SIP execution and otherwise preserves the ordinary immutable result suffixing.</summary>
    private static string ReserveResultPath(string resultPath)
    {
        if (IsResultPathAvailable(resultPath))
        {
            return resultPath;
        }

        var directory = Path.GetDirectoryName(resultPath)!;
        var fileName = Path.GetFileNameWithoutExtension(resultPath);
        var extension = Path.GetExtension(resultPath);
        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{fileName}-{index:0000}{extension}");
            if (IsResultPathAvailable(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not reserve a unique result path for {resultPath}.");
    }

    /// <summary>A prior result or any partial SIP publication owns its prefix and must never be overwritten.</summary>
    private static bool IsResultPathAvailable(string path) =>
        !File.Exists(path) &&
        !File.Exists(Path.ChangeExtension(path, ".candidate-decisions.json")) &&
        !File.Exists(Path.ChangeExtension(path, ".candidate-hypotheses.json")) &&
        !File.Exists(Path.ChangeExtension(path, ".portfolio-execution.json")) &&
        !File.Exists(Path.ChangeExtension(path, ".unified-completed-trades.json")) &&
        !File.Exists(Path.ChangeExtension(path, ".manifest.json")) &&
        !File.Exists(Path.ChangeExtension(path, ".event-source-manifest.json")) &&
        !File.Exists(Path.ChangeExtension(path, ".event-portfolio-request.json")) &&
        !File.Exists(Path.ChangeExtension(path, ".event-portfolio-source-audit.json")) &&
        !File.Exists(Path.ChangeExtension(path, ".event-portfolio-outcome.json"));

    private static string SanitizePathSegment(string value)
    {
        var cleaned = new string(value
            .Trim()
            .Select(ch => Char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')
            .ToArray());
        return String.IsNullOrWhiteSpace(cleaned) ? "shared" : cleaned;
    }

    private static async Task<IReadOnlyList<OhlcvBar>> LoadTickerBarsAsync(
        IMarketDataProvider provider,
        string ticker,
        IReadOnlyCollection<string> intervals,
        DateTimeOffset start,
        DateTimeOffset end,
        int retainedBars,
        int configuredReplayBarLimit,
        CancellationToken cancellationToken)
    {
        var bars = new List<OhlcvBar>();
        try
        {
            await foreach (var bar in provider.GetBarsAsync([ticker], intervals, start, end, cancellationToken))
            {
                EnsureReplayRetentionLimit(
                    retainedBars,
                    bars.Count + 1,
                    configuredReplayBarLimit);
                bars.Add(bar);
            }
        }
        catch (BacktestReplayRetentionLimitExceededException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var details = String.IsNullOrWhiteSpace(exception.Message)
                ? exception.GetType().Name
                : exception.Message;
            throw new InvalidOperationException(
                $"Failed loading market data for ticker {ticker} with intervals [{String.Join(", ", intervals)}]. Provider error: {details}",
                exception);
        }

        if (bars.Count == 0)
        {
            throw new InvalidOperationException(
                $"No market data bars were returned for ticker {ticker} with intervals [{String.Join(", ", intervals)}] between {start:O} and {end:O}.");
        }

        return bars;
    }

    private static async Task<IReadOnlyList<OhlcvBar>> LoadBenchmarkBarsAsync(
        BacktestRunConfig run,
        IMarketDataProvider provider,
        DateTimeOffset start,
        DateTimeOffset end,
        int retainedBars,
        int configuredReplayBarLimit,
        CancellationToken cancellationToken)
    {
        if (!run.Validation.Benchmark.Enabled || String.IsNullOrWhiteSpace(run.Validation.Benchmark.Ticker))
        {
            return Array.Empty<OhlcvBar>();
        }

        // Prefer a daily benchmark bar; otherwise use whatever timeframe the benchmark actually
        // has available. LoadTickerBarsAsync throws when a timeframe has no data, so each interval
        // is tried independently — loading only Intervals[0] previously produced a null benchmark
        // return whenever the benchmark lacked that exact timeframe.
        var intervals = run.Intervals
            .OrderBy(interval => TimeframeParser.IsDailyOrHigher(interval) ? 0 : 1)
            .ToArray();
        foreach (var interval in intervals)
        {
            try
            {
                var bars = await LoadTickerBarsAsync(
                    provider,
                    run.Validation.Benchmark.Ticker,
                    [interval],
                    start,
                    end,
                    retainedBars,
                    configuredReplayBarLimit,
                    cancellationToken);
                if (bars.Count > 0)
                {
                    return bars;
                }
            }
            catch (InvalidOperationException exception)
                when (exception is not BacktestReplayRetentionLimitExceededException)
            {
                // This timeframe has no data for the benchmark; try the next.
            }
        }

        return Array.Empty<OhlcvBar>();
    }

    internal static IReadOnlyList<OhlcvBar>? ResolvePreparedBenchmarkBars(
        BacktestRunConfig run,
        CandlePipelineResult marketState)
    {
        if (!run.Validation.Benchmark.Enabled || String.IsNullOrWhiteSpace(run.Validation.Benchmark.Ticker) ||
            !marketState.TickerStates.TryGetValue(run.Validation.Benchmark.Ticker, out var tickerState))
        {
            return null;
        }

        foreach (var interval in run.Intervals.OrderBy(
                     interval => TimeframeParser.IsDailyOrHigher(interval) ? 0 : 1))
        {
            if (tickerState.BarsByTimeframe.TryGetValue(interval, out var bars) && bars.Count > 0)
            {
                return bars;
            }
        }

        return null;
    }

    internal static void EnsureReplayRetentionLimit(
        int retainedBars,
        int additionalBars,
        int maximumBars)
    {
        if (retainedBars < 0 || additionalBars < 0 || maximumBars <= 0 ||
            (long)retainedBars + additionalBars > maximumBars)
        {
            throw new BacktestReplayRetentionLimitExceededException(maximumBars);
        }
    }

    internal static void EnsureStrategyWorkItemLimit(
        int tickerCount,
        int strategyCount,
        int maximumWorkItems)
    {
        if (tickerCount < 0 || strategyCount < 0 || maximumWorkItems <= 0 ||
            (long)tickerCount * strategyCount > maximumWorkItems)
        {
            throw new BacktestWorkItemLimitExceededException(maximumWorkItems);
        }
    }
}

internal sealed record DailyReturnMetrics(
    decimal AverageDailyReturnPct,
    int TradingDayCount);

internal sealed record PublishedBacktestEconomicSummary(
    decimal EndingCapital,
    decimal NetProfit,
    decimal TotalReturnPct,
    decimal AverageDailyReturnPct,
    decimal MaxDrawdownPct,
    WinnerStrategySummary? Winner,
    int WinningTradeCount,
    int LosingTradeCount,
    IReadOnlyList<BacktestTrade> CompletedTrades);

internal sealed class BacktestReplayRetentionLimitExceededException(int maximumBars)
    : InvalidOperationException(
        $"Backtest replay exceeds engine.max_retained_replay_bars ({maximumBars:N0}) across market and benchmark data.");

internal sealed class BacktestWorkItemLimitExceededException(int maximumWorkItems)
    : InvalidOperationException(
        $"Backtest strategy/ticker matrix exceeds engine.max_strategy_work_items ({maximumWorkItems:N0}).");

internal sealed class BacktestCandidateRetentionLimitExceededException(int maximumCandidates)
    : InvalidOperationException(
        $"Backtest candidate retention exceeds engine.max_retained_candidates ({maximumCandidates:N0}).");

internal sealed class RetainedCandidateBudget(int maximumCandidates)
{
    private int retainedCandidateCount;

    public void Reserve()
    {
        if (maximumCandidates <= 0 || Interlocked.Increment(ref retainedCandidateCount) > maximumCandidates)
        {
            throw new BacktestCandidateRetentionLimitExceededException(maximumCandidates);
        }
    }
}

internal sealed record PreparedTickerEvaluationContext(
    string Ticker,
    IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> BarsByTimeframe,
    IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> SnapshotsByTimeframe,
    IReadOnlyList<OhlcvBar> ProcessedBars,
    int TotalPreparedBarCount,
    IReadOnlyList<CatalystEvent> Catalysts,
    string? ErrorMessage)
{
    public static PreparedTickerEvaluationContext Failed(string ticker, string errorMessage)
    {
        return new PreparedTickerEvaluationContext(
            ticker,
            new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, IReadOnlyList<IndicatorSnapshot>>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<OhlcvBar>(),
            0,
            Array.Empty<CatalystEvent>(),
            errorMessage);
    }
}

internal sealed record StrategyTickerWorkItem(
    string Ticker,
    StrategyDefinition Strategy);

internal sealed record StrategyTickerBacktestBatch(
    string Ticker,
    string StrategyId,
    string StrategyName,
    bool Succeeded,
    string? ErrorMessage,
    IReadOnlyList<BacktestCandidateTrade> CandidateTrades,
    IReadOnlyList<BacktestCandidateDecisionAudit> CandidateDecisionAudit,
    string? CandidateAuditJournalPath,
    StrategyCandidateDiagnostics Diagnostics)
{
    public BacktestHistoryEligibility? HistoryEligibility { get; init; }
    public DateTimeOffset? IntradayUnavailableFromUtc { get; init; }
    public static StrategyTickerBacktestBatch Failed(
        string ticker,
        StrategyDefinition strategy,
        string errorMessage,
        IReadOnlyList<BacktestCandidateDecisionAudit>? candidateDecisionAudit = null,
        string? candidateAuditJournalPath = null)
    {
        var diagnostics = StrategyCandidateDiagnostics.MissingTimeframe(strategy, $"strategy_ticker_failed ({errorMessage})");
        return new StrategyTickerBacktestBatch(
            ticker,
            strategy.StrategyId,
            strategy.StrategyName,
            false,
            errorMessage,
            Array.Empty<BacktestCandidateTrade>(),
            candidateDecisionAudit ?? Array.Empty<BacktestCandidateDecisionAudit>(),
            candidateAuditJournalPath,
            diagnostics);
    }
}

internal sealed record StrategyCandidateEvaluation(
    IReadOnlyList<BacktestCandidateTrade> Candidates,
    IReadOnlyList<BacktestCandidateDecisionAudit> CandidateDecisionAudit,
    StrategyCandidateDiagnostics Diagnostics);

internal sealed class StrategyCandidateDiagnostics(
    string strategyId,
    string strategyName)
{
    public string StrategyId { get; } = strategyId;

    public string StrategyName { get; } = strategyName;

    public int EvaluatedBarCount { get; set; }

    public int CandidateTradeCount { get; set; }

    public Dictionary<string, int> RejectionCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> RejectionExamples { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static StrategyCandidateDiagnostics MissingTimeframe(StrategyDefinition strategy, string reason)
    {
        var diagnostics = new StrategyCandidateDiagnostics(strategy.StrategyId, strategy.StrategyName);
        diagnostics.IncrementRejection(reason);
        return diagnostics;
    }

    public void IncrementRejection(string reason)
    {
        var normalizedReason = NormalizeReason(reason);
        RejectionCounts[normalizedReason] = RejectionCounts.TryGetValue(normalizedReason, out var count) ? count + 1 : 1;
        if (!reason.Equals(normalizedReason, StringComparison.OrdinalIgnoreCase))
        {
            if (!RejectionExamples.TryGetValue(normalizedReason, out var examples))
            {
                examples = new List<string>();
                RejectionExamples[normalizedReason] = examples;
            }

            if (examples.Count < 5 && !examples.Contains(reason, StringComparer.OrdinalIgnoreCase))
            {
                examples.Add(reason);
            }
        }
    }

    public void MergeRejections(PortfolioAdmissionAudit admissionAudit)
    {
        foreach (var rejection in admissionAudit.RejectionCounts)
        {
            RejectionCounts[rejection.Key] = RejectionCounts.TryGetValue(rejection.Key, out var existing)
                ? existing + rejection.Value
                : rejection.Value;
        }

        foreach (var rejection in admissionAudit.RejectionExamples)
        {
            if (!RejectionExamples.TryGetValue(rejection.Key, out var examples))
            {
                examples = [];
                RejectionExamples[rejection.Key] = examples;
            }

            foreach (var example in rejection.Value)
            {
                if (examples.Count >= 5)
                {
                    break;
                }

                if (!examples.Contains(example, StringComparer.OrdinalIgnoreCase))
                {
                    examples.Add(example);
                }
            }
        }
    }

    private static string NormalizeReason(string reason)
    {
        var trimmed = reason.Trim();
        var detailStart = trimmed.IndexOf(" (", StringComparison.Ordinal);
        return detailStart > 0
            ? trimmed[..detailStart]
            : trimmed;
    }
}

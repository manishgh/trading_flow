using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Strategies;
using TradingFlow.Engine.Storage;
using TradingFlow.Data.Catalysts;
using Microsoft.Extensions.Logging;
using TradingFlow.Engine.Execution;
using System.Runtime.CompilerServices;
using TradingFlow.Domain.Discovery;

namespace TradingFlow.Backtesting;

public sealed partial class LiveRunner(
    IMarketDataProvider provider,
    ICatalystProvider? catalystProvider,
    IBrokerClient? brokerClient,
    TradingFlow.Domain.Locking.ITickerLockService? lockService,
    IOrderIntentRepository? orderIntents,
    IOrderEventRepository? orderEvents,
    IPositionLedgerRepository? positionLedger,
    TradingFlow.Domain.Audit.IDecisionAuditRepository? auditRepo,
    ILogger<LiveRunner> logger,
    IArtifactWriter? artifactWriter = null,
    ICandleStore? candleStore = null,
    IOrderSubmissionService? orderSubmissionService = null,
    ExecutionRunContext? executionRunContext = null,
    IOrderLifecycleService? orderLifecycleService = null,
    ILiveDiscoverySession? discoverySession = null,
    IMarketStateSnapshotProvider? marketStateSnapshots = null,
    TradingFlow.Domain.Persistence.ICandidateRepository? candidateRepository = null,
    TimeProvider? timeProvider = null,
    TimeSpan? iterationInterval = null,
    IStrategyDecisionContextProvider? decisionContextProvider = null,
    LivePipelineHealth? initialPipelineHealth = null,
    Func<LivePipelineHealth, CancellationToken, Task>? publishPipelineHealth = null,
    bool exitOnly = false)
{
    private readonly IStrategyDecisionContextProvider? _configuredDecisionContextProvider = decisionContextProvider;
    private IStrategyDecisionContextProvider? _decisionContextProvider;
    private readonly CandlePipelineEngine _candlePipeline = new(candleStore);
    private readonly TechnicalExecutionEngine _technicalExecutionEngine = new();
    private readonly ExecutionAuditor _auditor = new();
    private readonly IBrokerClient? _brokerClient = brokerClient;
    private readonly TradingFlow.Domain.Locking.ITickerLockService? _lockService = lockService;
    private readonly IOrderIntentRepository? _orderIntents = orderIntents;
    private readonly IOrderEventRepository? _orderEvents = orderEvents;
    private readonly IPositionLedgerRepository? _positionLedger = positionLedger;
    private readonly TradingFlow.Domain.Audit.IDecisionAuditRepository? _auditRepo = auditRepo;
    private readonly IArtifactWriter _artifactWriter = artifactWriter ?? AtomicFileArtifactWriter.Instance;
    private readonly IOrderSubmissionService? _orderSubmissionService = orderSubmissionService;
    private readonly ExecutionRunContext? _executionRunContext = executionRunContext;
    private readonly IOrderLifecycleService? _orderLifecycleService = orderLifecycleService;
    private readonly ILiveDiscoverySession? _discoverySession = discoverySession;
    private readonly IMarketStateSnapshotProvider? _marketStateSnapshots = marketStateSnapshots;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IStrategyCandidateDecisionOrchestrator? _candidateDecisions = candidateRepository is null
        ? null
        : new StrategyCandidateDecisionOrchestrator(new StrategyDecisionKernel(), candidateRepository);
    private readonly TimeSpan _iterationInterval = ResolveIterationInterval(iterationInterval);
    private readonly TradingFlow.Engine.Regime.RegimeGateService _regimeGate = new();
    private IReadOnlyDictionary<StrategyDefinition, AuthorizedRuntimeStrategy> runtimeStrategies =
        new Dictionary<StrategyDefinition, AuthorizedRuntimeStrategy>(StrategyReferenceComparer.Instance);

    public Task RunAsync(
        BacktestRunConfig run,
        AuthorizedRuntimeStrategy[] strategies,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        var effectiveStrategies = strategies
            .ToArray();
        var authorizations = effectiveStrategies.ToDictionary(
            strategy => strategy.Definition,
            strategy => strategy,
            StrategyReferenceComparer.Instance);
        return RunCoreAsync(
            run,
            effectiveStrategies.Select(strategy => strategy.Definition).ToArray(),
            authorizations,
            cancellationToken,
            progress);
    }

    public Task RunAsync(
        BacktestRunConfig run,
        StrategyDefinition[] strategies,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(strategies);
        return RunCoreAsync(
            run,
            ApplyRunSessionPolicy(run, strategies),
            new Dictionary<StrategyDefinition, AuthorizedRuntimeStrategy>(StrategyReferenceComparer.Instance),
            cancellationToken,
            progress);
    }

    /// <summary>Polls authorized swing strategies and records pipeline health between completed batches.</summary>
    private async Task RunCoreAsync(
        BacktestRunConfig run,
        StrategyDefinition[] strategies,
        IReadOnlyDictionary<StrategyDefinition, AuthorizedRuntimeStrategy> authorizations,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        foreach (var strategy in strategies)
        {
            TechnicalExecutionEngine.RequireSupportedExitPolicy(strategy);
            TechnicalExecutionEngine.RequireNoBacktestOnlyRules(strategy);
        }
        RunUniverseValidator.RequireResolved(run);
        _decisionContextProvider = _configuredDecisionContextProvider ?? FrozenManifestDecisionContextProvider.FromConfig(run.Engine);
        RequireSwingStrategies(strategies);
        runtimeStrategies = authorizations;
        logger.LogInformation("Starting LiveRunner for run: {RunName}", run.RunName);
        progress?.Report($"Starting LiveRunner for run: {run.RunName}");

        logger.LogInformation("Loaded {Count} strategies for live execution.", strategies.Length);
        foreach (var st in strategies)
        {
            logger.LogInformation("Strategy active: {StrategyName} (Execution TF: {ExecutionTF}, Indicator TF: {IndicatorTF})",
                st.StrategyName, st.Execution.Timeframe, st.Timeframe);
            progress?.Report($"Strategy active: {st.StrategyName}");
        }

        var catalystStreamer = new CatalystStreamer(catalystProvider);

        var pollingInterval = _iterationInterval;
        var pipelineHealth = initialPipelineHealth ?? new LivePipelineHealth();
        var decisionCalendar = new PaperDecisionCalendar(provider, ResolveExchangeTimezone(strategies));
        var entrySkipAudit = new EntrySkipAudit(
            _auditRepo, logger, run.RunName, _executionRunContext?.RunId, authorizations, _discoverySession is not null);

        var configuredTickers = new HashSet<string>(run.Tickers ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var lastDiscoveryTickers = new HashSet<string>(configuredTickers, StringComparer.OrdinalIgnoreCase);
        var lastDiscoveryMembers = new Dictionary<string, ActiveDiscoveryAggregate>(StringComparer.OrdinalIgnoreCase);
        var lastConfirmedExposureTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<ActiveBrokerOrder> lastConfirmedOpenOrders = Array.Empty<ActiveBrokerOrder>();
        IReadOnlyList<BrokerPosition> lastConfirmedOpenPositions = Array.Empty<BrokerPosition>();

        // Reconstruct broker exposure before the first discovery publication. A
        // restart must never briefly unsubscribe a held position merely because
        // its discovery source has already dropped it.
        if (_discoverySession is not null && _brokerClient is not null)
        {
            try
            {
                lastConfirmedOpenOrders = await _brokerClient.GetOpenOrdersAsync(cancellationToken);
                lastConfirmedOpenPositions = await _brokerClient.GetOpenPositionsAsync(cancellationToken);
                var retainedExposure = lastConfirmedOpenOrders
                    .Where(IsEntryExposureOrder)
                    .Select(order => order.Ticker)
                    .Concat(lastConfirmedOpenPositions
                        .Where(IsOpenExposurePosition)
                        .Select(position => position.Ticker))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                lastConfirmedExposureTickers.UnionWith(retainedExposure);
                await _discoverySession.RetainExposureSymbolsAsync(retainedExposure, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Initial broker exposure reconciliation failed for {RunName}; discovery publication is blocked to prevent unsafe unsubscription.",
                    run.RunName);
                throw;
            }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("LiveRunner iteration starting at {Time}", _timeProvider.GetUtcNow());
            progress?.Report($"LiveRunner iteration starting at {FormatLocalTime(_timeProvider.GetUtcNow())}");

            var runWideFailures = new List<string>();
            var discovery = await RefreshDiscoveryAsync(afterWarmup: false);
            if (discovery is not null)
            {
                SetDiscoveryUniverse(discovery);
                if (discovery.Added.Count > 0 || discovery.Dropped.Count > 0)
                {
                    logger.LogInformation(
                        "Discovery universe changed for {RunName}: ADD [{Added}], DROP [{Dropped}].",
                        run.RunName, String.Join(", ", discovery.Added), String.Join(", ", discovery.Dropped));
                    progress?.Report(
                        $"Discovery refresh: +{discovery.Added.Count} / -{discovery.Dropped.Count}; " +
                        $"{discovery.Symbols.Count} active ticker(s).");
                }
            }

            var end = _timeProvider.GetUtcNow();

            var maxLookbackDays = run.TimeWindow.WarmupLookbackDays > 0
                ? run.TimeWindow.WarmupLookbackDays
                : Math.Max(run.TimeWindow.LookbackDays, 5);
            foreach (var st in strategies)
            {
                var timeframes = new List<string> { st.Execution.Timeframe, st.Timeframe };
                if (st.Confluence.Enabled) timeframes.Add(st.Confluence.Timeframe);

                foreach (var tf in timeframes.Select(t => t.ToLowerInvariant()))
                {
                    if (run.TimeWindow.WarmupLookbackDays > 0)
                    {
                        continue;
                    }

                    if (tf.EndsWith("min") || tf.EndsWith("m")) maxLookbackDays = Math.Max(maxLookbackDays, 10);
                    else if (tf.EndsWith("hour") || tf.EndsWith("h")) maxLookbackDays = Math.Max(maxLookbackDays, 40);
                    else if (tf.EndsWith("day") || tf.EndsWith("d")) maxLookbackDays = Math.Max(maxLookbackDays, 260);
                }
            }
            var start = end.AddDays(-maxLookbackDays); // Warmup

            var activeExposureTickers = new HashSet<string>(
                lastConfirmedExposureTickers,
                StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<ActiveBrokerOrder> openOrdersSnapshot = lastConfirmedOpenOrders;
            IReadOnlyList<BrokerPosition> openPositionsSnapshot = lastConfirmedOpenPositions;
            var brokerStateRefreshed = _brokerClient is null;
            var brokerStateConfirmedForOrderDecisions = _brokerClient is null;
            if (_brokerClient != null)
            {
                try
                {
                    var openOrders = await _brokerClient.GetOpenOrdersAsync(cancellationToken);
                    var openPositions = await _brokerClient.GetOpenPositionsAsync(cancellationToken);

                    if (_orderLifecycleService is not null)
                    {
                        foreach (var order in openOrders.Where(order =>
                                     order.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) &&
                                     ClientOrderIdFactory.IsBindingFormat(order.ClientOrderId)))
                        {
                            await _orderLifecycleService.ApplyBrokerUpdateAsync(
                                BrokerOrderUpdateFactory.Create(order),
                                cancellationToken);
                        }
                    }

                    // Active Cancellation logic for unfilled entry orders if configured
                    if (!exitOnly && run.Execution != null &&
                        "active_cancel".Equals(run.Execution.OrderExpiration, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var order in openOrders)
                        {
                            if ("buy".Equals(order.Side, StringComparison.OrdinalIgnoreCase) &&
                                ("accepted".Equals(order.Status, StringComparison.OrdinalIgnoreCase) || "new".Equals(order.Status, StringComparison.OrdinalIgnoreCase)) &&
                                DateTimeOffset.UtcNow - order.CreatedAt > TimeSpan.FromMinutes(30))
                            {
                                logger.LogInformation("Active cancellation: Entry order {OrderId} for {Ticker} is unfilled after 30 minutes. Cancelling...", order.OrderId, order.Ticker);
                                progress?.Report($"Active cancellation: Entry order {order.OrderId} for {order.Ticker} is unfilled after 30 minutes. Cancelling...");
                                if (_orderSubmissionService is null)
                                {
                                    throw new InvalidOperationException(
                                        "Active order expiry requires the common order command service.");
                                }

                                var cancellation = await _orderSubmissionService.RequestCancelAsync(
                                    new OrderCancellationSubmission(
                                        order.ParentClientOrderId ?? order.ClientOrderId,
                                        order.OrderId,
                                        "entry_unfilled_after_active_cancel_window",
                                        _timeProvider.GetUtcNow().ToUniversalTime()),
                                    _brokerClient,
                                    cancellationToken);
                                if (!OrderStateMachine.IsTerminal(cancellation.State))
                                {
                                    throw new OrderCancellationPendingException(order.ClientOrderId, order.OrderId);
                                }
                            }
                        }
                        // Fetch the updated open orders list after cancellations
                        openOrders = await _brokerClient.GetOpenOrdersAsync(cancellationToken);
                        openPositions = await _brokerClient.GetOpenPositionsAsync(cancellationToken);
                    }

                    openOrdersSnapshot = openOrders;
                    openPositionsSnapshot = openPositions;
                    activeExposureTickers.Clear();
                    foreach (var order in openOrders.Where(IsEntryExposureOrder))
                    {
                        activeExposureTickers.Add(order.Ticker);
                    }

                    foreach (var pos in openPositions.Where(IsOpenExposurePosition))
                    {
                        activeExposureTickers.Add(pos.Ticker);
                    }
                    lastConfirmedOpenOrders = openOrdersSnapshot;
                    lastConfirmedOpenPositions = openPositionsSnapshot;
                    lastConfirmedExposureTickers = new HashSet<string>(
                        activeExposureTickers,
                        StringComparer.OrdinalIgnoreCase);
                    brokerStateRefreshed = true;
                    brokerStateConfirmedForOrderDecisions = true;

                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ex is not OrderCancellationPendingException)
                    {
                        runWideFailures.Add($"Broker order and position state: {LivePipelineHealth.DescribeFailure(ex)}");
                    }
                    brokerStateConfirmedForOrderDecisions = false;
                    logger.LogWarning(
                        ex,
                        "Failed to fetch a complete active broker snapshot; retaining the last confirmed exposure and blocking order decisions for this iteration.");
                }
            }

            if (_discoverySession is not null && brokerStateRefreshed)
            {
                try
                {
                    await _discoverySession.RetainExposureSymbolsAsync(activeExposureTickers, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Failed to update retained exposure subscriptions for {RunName}; continuing with the prior subscription set.",
                        run.RunName);
                }
            }

            var tickerFailures = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            var iterationTickers = new HashSet<string>(lastDiscoveryTickers, StringComparer.OrdinalIgnoreCase);
            iterationTickers.UnionWith(activeExposureTickers);
            if (iterationTickers.Count == 0)
            {
                logger.LogInformation("Discovery produced no active symbols for {RunName}.", run.RunName);
                progress?.Report("Discovery produced no active symbols. Waiting for the next refresh.");
            }
            else
            {
                await ProcessMarketPollAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var updatedHealth = pipelineHealth.RecordPoll(tickerFailures, runWideFailures, end);
            if (updatedHealth.EntriesStopped && !pipelineHealth.EntriesStopped)
            {
                logger.LogError("New entries stopped for {RunName} after {FailedPolls} consecutive run-wide pipeline failures.",
                    run.RunName, updatedHealth.ConsecutiveFailingPolls);
            }
            await PublishHealthAsync(updatedHealth);

            // Prepare market data, then process all usable states even when this poll blocks entries.
            async Task ProcessMarketPollAsync()
            {
                MarketStatePreparation preparation;
                try
                {
                    preparation = await PrepareMarketStatesAsync(
                        run, strategies, iterationTickers, start, end, progress, cancellationToken);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    runWideFailures.Add($"Market state preparation: {LivePipelineHealth.DescribeFailure(exception)}");
                    logger.LogError(exception, "Could not prepare market states for {RunName}.", run.RunName);
                    return;
                }

                if (preparation.SharedFailure is { } sharedMarketFailure)
                {
                    runWideFailures.Add(sharedMarketFailure);
                }

                // Check the refresh schedule again after warm-up. Keep the original
                // symbol batch for technical exits even if membership is withdrawn.
                var membershipBeforeWarmupRefresh = lastDiscoveryMembers;
                var refreshed = await RefreshDiscoveryAsync(afterWarmup: true);
                if (refreshed is not null)
                {
                    SetDiscoveryUniverse(refreshed);
                }

                var failedSources = pipelineHealth.Discovery?.FailedSources.ToDictionary(
                    source => (source.SourceKind, source.SourceKey)) ?? new();
                var calendar = decisionCalendar.ForPoll(end, cancellationToken);
                var activeTickerSnapshot = activeExposureTickers.ToArray();
                var processed = 0;
                try
                {
                    await Parallel.ForEachAsync(
                        iterationTickers,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = ResolveWorkerCount(run.Engine.WorkerCount),
                            CancellationToken = cancellationToken
                        },
                        async (ticker, token) =>
                        {
                            try
                            {
                                progress?.Report($"Processing prepared market state for {ticker}");
                                var marketFailure = preparation.TickerFailures.GetValueOrDefault(ticker);
                                if (!preparation.States.TryGetValue(ticker, out var tickerState))
                                {
                                    if (MarketDataUnavailable.IsNamedReason(marketFailure))
                                    {
                                        await SaveTickerPipelineAuditsAsync(run, strategies, ticker, marketFailure!, marketEvidenceUnavailable: true, token);
                                        return;
                                    }
                                    if (preparation.SharedFailure is not null)
                                    {
                                        return;
                                    }
                                    throw new InvalidOperationException(marketFailure ??
                                        $"No prepared market state was produced for ticker {ticker}.");
                                }

                                var membership = EntryMembershipObservation.Create(
                                    refreshed is not null ? lastDiscoveryMembers.GetValueOrDefault(ticker) : null,
                                    membershipBeforeWarmupRefresh.GetValueOrDefault(ticker),
                                    refreshed?.ResolvedAtUtc,
                                    _discoverySession is not null && refreshed is null,
                                    failedSources,
                                    _timeProvider.GetUtcNow());
                                await ProcessTickerLiveAsync(
                                    run, strategies, ticker, tickerState, catalystStreamer, membership, entrySkipAudit,
                                    activeTickerSnapshot, openOrdersSnapshot, openPositionsSnapshot,
                                    brokerStateConfirmedForOrderDecisions,
                                    exitOnly || pipelineHealth.EntriesStopped || preparation.SharedFailure is not null ||
                                    marketFailure is not null, calendar, token, progress);

                                if (marketFailure is not null)
                                {
                                    throw new InvalidOperationException(marketFailure);
                                }
                            }
                            catch (Exception exception) when (!token.IsCancellationRequested)
                            {
                                var reason = LivePipelineHealth.DescribeFailure(exception);
                                tickerFailures[ticker] = reason;
                                logger.LogWarning(exception, "Ticker pipeline failed for {Ticker}; continuing with other tickers.", ticker);
                                progress?.Report($"Ticker {ticker} failed: {reason}");
                                await SaveTickerPipelineAuditsAsync(run, strategies, ticker, reason, marketEvidenceUnavailable: false, token);
                            }
                            finally
                            {
                                var finished = Interlocked.Increment(ref processed);
                                progress?.Report($"Processed {finished}/{iterationTickers.Count} ticker pipeline(s).");
                            }
                        });
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    runWideFailures.Add($"Ticker batch coordination: {LivePipelineHealth.DescribeFailure(exception)}");
                    logger.LogError(exception, "Error coordinating the ticker batch for {RunName}.", run.RunName);
                }

                if (calendar.IsValueCreated && !calendar.Value.IsCompletedSuccessfully)
                {
                    var exception = calendar.Value.Exception?.InnerException;
                    var reason = exception is null ? "Calendar read was cancelled." : LivePipelineHealth.DescribeFailure(exception);
                    runWideFailures.Add($"Paper decision calendar: {reason}");
                    logger.LogError(exception, "Could not read the shared paper decision calendar for {RunName}: {Reason}.", run.RunName, reason);
                }
            }

            logger.LogInformation("LiveRunner iteration finished. Waiting {PollingInterval}...", pollingInterval);
            progress?.Report($"Iteration finished. Waiting {pollingInterval}...");
            await Task.Delay(pollingInterval, cancellationToken);
        }

        // Apply the new universe only after a complete refresh. Keep the earlier
        // symbols available for preparation and exits while discovery is unavailable.
        void SetDiscoveryUniverse(DiscoveryUniverseSnapshot snapshot)
        {
            lastDiscoveryMembers = snapshot.Members.ToDictionary(member => member.Symbol, StringComparer.OrdinalIgnoreCase);
            lastDiscoveryTickers = lastDiscoveryMembers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // Publish current source failures before any candidate can be evaluated
        // and before the durable first-POST fence checks the job snapshot.
        async Task<DiscoveryUniverseSnapshot?> RefreshDiscoveryAsync(bool afterWarmup)
        {
            if (_discoverySession is null)
            {
                return null;
            }

            var attemptedAt = _timeProvider.GetUtcNow().ToUniversalTime();
            DiscoveryUniverseSnapshot? snapshot = null;
            LivePipelineHealth updated;
            try
            {
                snapshot = await _discoverySession.RefreshAsync(cancellationToken);
                updated = pipelineHealth.RecordDiscoveryRefresh(snapshot);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                var phase = afterWarmup ? "after market warm-up" : "before market warm-up";
                var reason = LivePipelineHealth.DescribeFailure(exception);
                logger.LogWarning(exception, "Discovery refresh failed {RefreshPhase} for {RunName}; keeping prior symbols for exit work.",
                    phase, run.RunName);
                progress?.Report($"Discovery refresh failed {phase}: {reason}");
                updated = pipelineHealth.RecordDiscoveryRefreshFailure(attemptedAt, reason);
                snapshot = null;
            }

            await PublishHealthAsync(updated);
            return snapshot;
        }

        // Snapshot write failures propagate. Continue only once changed entry
        // restrictions are committed by the durable worker.
        async Task PublishHealthAsync(LivePipelineHealth updated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (updated == pipelineHealth)
            {
                return;
            }
            if (publishPipelineHealth is not null)
            {
                await publishPipelineHealth(updated, cancellationToken);
            }
            pipelineHealth = updated;
        }
    }

    private static void RequireSwingStrategies(IEnumerable<StrategyDefinition> strategies)
    {
        var unsupported = strategies
            .Where(strategy => !strategy.Timeframe.Equals("1d", StringComparison.OrdinalIgnoreCase))
            .Select(strategy => $"{strategy.StrategyId} ({strategy.Timeframe})")
            .ToArray();
        if (unsupported.Length > 0)
        {
            throw new InvalidOperationException(
                $"Live execution supports daily-primary swing strategies only. Unsupported: {String.Join(", ", unsupported)}.");
        }
    }

    private sealed class StrategyReferenceComparer : IEqualityComparer<StrategyDefinition>
    {
        public static StrategyReferenceComparer Instance { get; } = new();

        public bool Equals(StrategyDefinition? left, StrategyDefinition? right) =>
            ReferenceEquals(left, right);

        public int GetHashCode(StrategyDefinition value) => RuntimeHelpers.GetHashCode(value);
    }




}

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
    TimeSpan? iterationInterval = null)
{
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

    private async Task RunCoreAsync(
        BacktestRunConfig run,
        StrategyDefinition[] strategies,
        IReadOnlyDictionary<StrategyDefinition, AuthorizedRuntimeStrategy> authorizations,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        RunUniverseValidator.RequireResolved(run);
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

            var discoveryTickers = new HashSet<string>(lastDiscoveryTickers, StringComparer.OrdinalIgnoreCase);
            if (_discoverySession is not null)
            {
                try
                {
                    var discovery = await _discoverySession.RefreshAsync(cancellationToken);
                    discoveryTickers = discovery.Symbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    lastDiscoveryMembers = discovery.Members.ToDictionary(
                        member => member.Symbol,
                        member => member,
                        StringComparer.OrdinalIgnoreCase);
                    lastDiscoveryTickers = new HashSet<string>(discoveryTickers, StringComparer.OrdinalIgnoreCase);
                    if (discovery.Added.Count > 0 || discovery.Dropped.Count > 0)
                    {
                        logger.LogInformation(
                            "Discovery universe changed for {RunName}: ADD [{Added}], DROP [{Dropped}].",
                            run.RunName,
                            String.Join(", ", discovery.Added),
                            String.Join(", ", discovery.Dropped));
                        progress?.Report(
                            $"Discovery refresh: +{discovery.Added.Count} / -{discovery.Dropped.Count}; " +
                            $"{discovery.Symbols.Count} active ticker(s).");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Discovery refresh failed for {RunName}; retaining the last durable universe.",
                        run.RunName);
                    progress?.Report(
                        $"Discovery refresh failed; retaining {lastDiscoveryTickers.Count} prior ticker(s): {exception.Message}");
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
                    if (run.Execution != null && "active_cancel".Equals(run.Execution.OrderExpiration, StringComparison.OrdinalIgnoreCase))
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
                catch (Exception ex)
                {
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

            try
            {
                var iterationTickers = new HashSet<string>(discoveryTickers, StringComparer.OrdinalIgnoreCase);
                iterationTickers.UnionWith(activeExposureTickers);
                if (iterationTickers.Count == 0)
                {
                    logger.LogWarning(
                        "Discovery produced no active symbols for {RunName}; waiting for the next refresh.",
                        run.RunName);
                    progress?.Report("Discovery produced no active symbols. Waiting for the next refresh.");
                    await Task.Delay(pollingInterval, cancellationToken);
                    continue;
                }

                var processed = 0;
                var total = iterationTickers.Count;
                var activeTickerSnapshot = activeExposureTickers.ToArray();
                var requiredTimeframes = ResolveRequiredTimeframes(run, strategies);
                var requestedIntervals = run.Intervals
                    .Where(x => !String.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (requestedIntervals.Length == 0)
                {
                    requestedIntervals = requiredTimeframes;
                }

                var preparedStates = new System.Collections.Concurrent.ConcurrentDictionary<string, TickerMarketState>(
                    StringComparer.OrdinalIgnoreCase);
                var unresolvedTickers = new System.Collections.Concurrent.ConcurrentBag<string>();
                await Parallel.ForEachAsync(
                    iterationTickers,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = ResolveWorkerCount(run.Engine.WorkerCount),
                        CancellationToken = cancellationToken
                    },
                    async (ticker, token) =>
                    {
                        var streamed = _marketStateSnapshots is null
                            ? null
                            : await _marketStateSnapshots.GetTickerStateAsync(
                                ticker,
                                requiredTimeframes,
                                run.Engine.IndicatorWarmupBars,
                                end,
                                token);
                        if (streamed is null)
                        {
                            unresolvedTickers.Add(ticker);
                        }
                        else
                        {
                            preparedStates[ticker] = streamed;
                        }
                    });

                var fallbackFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!unresolvedTickers.IsEmpty)
                {
                    var fallback = await _candlePipeline.RunAsync(
                        new CandlePipelineRequest(
                            unresolvedTickers.ToArray(),
                            requestedIntervals,
                            requiredTimeframes,
                            run.DerivedTimeframes.Source,
                            start,
                            end,
                            run.Engine.BoundedCapacity,
                            run.Engine.WorkerCount,
                            IncludeExtendedHours: true,
                            ResolveExchangeTimezone(strategies),
                            CreateCandleStoreContext(run)),
                        provider,
                        cancellationToken,
                        new Progress<string>(message => progress?.Report(message)));
                    foreach (var (ticker, state) in fallback.TickerStates)
                    {
                        preparedStates[ticker] = state;
                    }

                    foreach (var (ticker, failure) in fallback.Failures)
                    {
                        fallbackFailures[ticker] = failure;
                    }
                }

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
                            if (!preparedStates.TryGetValue(ticker, out var tickerState))
                            {
                                var reason = fallbackFailures.TryGetValue(ticker, out var failure)
                                    ? failure
                                    : $"No prepared market state was produced for ticker {ticker}.";
                                throw new InvalidOperationException(reason);
                            }

                             await ProcessTickerLiveAsync(
                                 run,
                                 strategies,
                                 ticker,
                                 tickerState,
                                 catalystStreamer,
                                 lastDiscoveryMembers.TryGetValue(ticker, out var discoveryMember)
                                     ? discoveryMember
                                     : null,
                                 activeTickerSnapshot,
                                openOrdersSnapshot,
                                openPositionsSnapshot,
                                brokerStateConfirmedForOrderDecisions,
                                token,
                                progress);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Ticker pipeline failed for {Ticker}; continuing with other tickers.", ticker);
                            progress?.Report($"Ticker {ticker} failed: {ex.Message}");
                            await SaveTickerPipelineFailureAuditsAsync(run, strategies, ticker, ex.Message, token);
                        }
                        finally
                        {
                            var finished = Interlocked.Increment(ref processed);
                            progress?.Report($"Processed {finished}/{total} ticker pipeline(s).");
                        }
                    });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in LiveRunner iteration");
                progress?.Report($"Error processing {run.RunName}: {ex.Message}");
            }

            logger.LogInformation("LiveRunner iteration finished. Waiting {PollingInterval}...", pollingInterval);
            progress?.Report($"Iteration finished. Waiting {pollingInterval}...");
            await Task.Delay(pollingInterval, cancellationToken);
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

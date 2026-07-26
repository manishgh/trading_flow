using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Strategies;
using TradingFlow.Engine.Storage;
using TradingFlow.Data.Catalysts;
using Microsoft.Extensions.Logging;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Backtesting;

public sealed partial class LiveRunner(
    IMarketDataProvider provider,
    ICatalystProvider? catalystProvider,
    IBrokerClient? brokerClient,
    TradingFlow.Domain.Locking.ITickerLockService? lockService,
    TradingFlow.Domain.Orders.IOrderStateRepository? orderRepo,
    TradingFlow.Domain.Audit.IDecisionAuditRepository? auditRepo,
    ILogger<LiveRunner> logger,
    IArtifactWriter? artifactWriter = null,
    ICandleStore? candleStore = null,
    IRawArchiveWriter? rawArchiveWriter = null,
    IOrderSubmissionService? orderSubmissionService = null,
    ExecutionRunContext? executionRunContext = null,
    IOrderLifecycleService? orderLifecycleService = null)
{
    private readonly SignalGenerator _signalGenerator = new();
    private readonly StrategyDecisionBrain _decisionBrain = new();
    private readonly CandlePipelineEngine _candlePipeline = new(candleStore);
    private readonly TechnicalExecutionEngine _technicalExecutionEngine = new();
    private readonly CompletedBarExecutionPlanner _executionPlanner = new();
    private readonly TradingFlow.Engine.Sessions.StrategySessionClock _sessionClock = new();
    private readonly ExecutionAuditor _auditor = new();
    private readonly IBrokerClient? _brokerClient = brokerClient;
    private readonly TradingFlow.Domain.Locking.ITickerLockService? _lockService = lockService;
    private readonly TradingFlow.Domain.Orders.IOrderStateRepository? _orderRepo = orderRepo;
    private readonly TradingFlow.Domain.Audit.IDecisionAuditRepository? _auditRepo = auditRepo;
    private readonly IArtifactWriter _artifactWriter = artifactWriter ?? AtomicFileArtifactWriter.Instance;
    private readonly IRawArchiveWriter? _rawArchiveWriter = rawArchiveWriter;
    private readonly IOrderSubmissionService? _orderSubmissionService = orderSubmissionService;
    private readonly ExecutionRunContext? _executionRunContext = executionRunContext;
    private readonly IOrderLifecycleService? _orderLifecycleService = orderLifecycleService;
    private readonly TradingFlow.Engine.Regime.RegimeGateService _regimeGate = new();

    public async Task RunAsync(
        BacktestRunConfig run,
        StrategyDefinition[] strategies,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        strategies = ApplyRunSessionPolicy(run, strategies);
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

        var pollingInterval = TimeSpan.FromMinutes(1);

        var mergedTickers = new HashSet<string>(run.Tickers ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var screenerRelativeVolumeByTicker = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        if (run.Screener != null && run.Screener.Enabled && run.Screener.Filters != null)
        {
            logger.LogInformation("Fetching dynamic tickers from screeners...");
            progress?.Report("Fetching dynamic tickers from screeners...");

            try
            {
                using var httpClient = new System.Net.Http.HttpClient();
                using var finvizClient = new TradingFlow.Finviz.FinvizClient(httpClient, new TradingFlow.Finviz.FinvizOptions(
                    new Uri("https://finviz.com", UriKind.Absolute),
                    Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? ""),
                    _rawArchiveWriter ?? throw new InvalidOperationException(
                        "Finviz screening requires a raw archive writer so responses are durable before parsing."));

                foreach (var filter in run.Screener.Filters)
                {
                    var screenerRows = await finvizClient.GetScreenerRowsAsync(filter, cancellationToken);
                    var minimumRelativeVolume = ParseFinvizRelativeVolumeFilter(filter);
                    foreach (var row in screenerRows)
                    {
                        mergedTickers.Add(row.Ticker);
                        var effectiveRelativeVolume = row.RelativeVolume ?? minimumRelativeVolume;
                        if (effectiveRelativeVolume is { } value)
                        {
                            screenerRelativeVolumeByTicker[row.Ticker] = value;
                        }
                    }
                }
                progress?.Report($"Merged screener tickers. Total tickers to process: {mergedTickers.Count}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to fetch screener tickers.");
                if (mergedTickers.Count == 0)
                {
                    throw new Exception($"Screener failed and no static tickers provided: {ex.Message}", ex);
                }

                logger.LogWarning("Screener failed but static tickers exist. Proceeding with static tickers only.");
                progress?.Report($"Screener failed: {ex.Message}. Proceeding with {mergedTickers.Count} static tickers.");
            }
        }

        if (mergedTickers.Count == 0)
        {
            throw new Exception("No tickers found to process. Verify your screener query or static tickers list.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("LiveRunner iteration starting at {Time}", DateTimeOffset.UtcNow);
            progress?.Report($"LiveRunner iteration starting at {FormatLocalTime(DateTimeOffset.UtcNow)}");

            var end = DateTimeOffset.UtcNow;

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

            var activeExposureTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<ActiveBrokerOrder> openOrdersSnapshot = Array.Empty<ActiveBrokerOrder>();
            IReadOnlyList<BrokerPosition> openPositionsSnapshot = Array.Empty<BrokerPosition>();
            if (_brokerClient != null)
            {
                try
                {
                    var openOrders = await _brokerClient.GetOpenOrdersAsync(cancellationToken);
                    openOrdersSnapshot = openOrders;

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

                    // State Reconciliation: Adopt any open orders from the broker that are not in our database
                    if (_orderRepo != null)
                    {
                        foreach (var order in openOrders)
                        {
                            var existing = await _orderRepo.GetOrderAsync(order.OrderId, cancellationToken);
                            if (existing == null)
                            {
                                if (!order.Side.Equals("buy", StringComparison.OrdinalIgnoreCase))
                                {
                                    logger.LogDebug(
                                        "Skipping adoption of broker-side exit/order leg {OrderId} for {Ticker}. Side={Side} Type={OrderType}",
                                        order.OrderId,
                                        order.Ticker,
                                        order.Side,
                                        order.OrderType);
                                    continue;
                                }

                                logger.LogInformation("State reconciliation: Adopting orphan broker order {OrderId} for {Ticker}.", order.OrderId, order.Ticker);
                                progress?.Report($"State reconciliation: Adopting orphan broker order {order.OrderId} for {order.Ticker}.");

                                var newOrder = new TradingFlow.Domain.Orders.PersistedOrder
                                {
                                    OrderId = order.OrderId,
                                    Ticker = order.Ticker,
                                    RunName = run.RunName,
                                    ClientOrderId = order.ClientOrderId,
                                    StrategyName = "AdoptedFromBroker",
                                    Broker = run.Execution?.Broker ?? "unknown",
                                    Status = order.Status,
                                    EntryPrice = order.LimitPrice ?? 0m,
                                    ShareQuantity = (int)(order.Qty ?? 0m),
                                    CreatedAt = order.CreatedAt,
                                    UpdatedAt = DateTimeOffset.UtcNow
                                };
                                await _orderRepo.SaveOrderAsync(newOrder, cancellationToken);
                            }
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
                                if (_orderLifecycleService is not null &&
                                    ClientOrderIdFactory.IsBindingFormat(order.ClientOrderId))
                                {
                                    await _orderLifecycleService.RequestCancelAsync(
                                        order.ClientOrderId,
                                        order.OrderId,
                                        cancellationToken);
                                }

                                await _brokerClient.CancelOrderAsync(order.OrderId, cancellationToken);
                            }
                        }
                        // Fetch the updated open orders list after cancellations
                        openOrders = await _brokerClient.GetOpenOrdersAsync(cancellationToken);
                        openOrdersSnapshot = openOrders;
                    }

                    var openPositions = await _brokerClient.GetOpenPositionsAsync(cancellationToken);
                    openPositionsSnapshot = openPositions;
                    foreach (var order in openOrders.Where(IsEntryExposureOrder))
                    {
                        activeExposureTickers.Add(order.Ticker);
                    }

                    foreach (var pos in openPositions.Where(IsOpenExposurePosition))
                    {
                        activeExposureTickers.Add(pos.Ticker);
                    }

                    // Downward Reconciliation & Extended Hours exit handling
                    if (_orderRepo != null)
                    {
                        var allDbOrders = new List<TradingFlow.Domain.Orders.PersistedOrder>();
                        foreach (var t in mergedTickers)
                        {
                            var orders = await _orderRepo.GetActiveOrdersByTickerAsync(t, cancellationToken);
                            allDbOrders.AddRange(orders);
                        }

                        foreach (var dbOrder in allDbOrders)
                        {
                            var stillOpen = openOrders.Any(o => o.OrderId == dbOrder.OrderId);
                            var pos = openPositions.FirstOrDefault(p => p.Ticker.Equals(dbOrder.Ticker, StringComparison.OrdinalIgnoreCase));

                            if (!stillOpen)
                            {
                                if (dbOrder.Status == "pending_exit_setup" && pos != null)
                                {
                                    if (run.Execution != null && run.Execution.AllowExtendedHoursTrading)
                                    {
                                        logger.LogInformation("Entry order {OrderId} filled for {Ticker}. Submitting exit OCO order...", dbOrder.OrderId, dbOrder.Ticker);
                                        progress?.Report($"Entry filled for {dbOrder.Ticker}. Submitting OCO exit bracket...");

                                        try
                                        {
                                            var exitIds = await _brokerClient.SubmitExitOrdersAsync(dbOrder.Ticker, dbOrder.ShareQuantity, dbOrder.StopLossPrice, dbOrder.TakeProfitPrice, cancellationToken);
                                            dbOrder.Status = "exit_submitted";
                                            dbOrder.UpdatedAt = DateTimeOffset.UtcNow;
                                            await _orderRepo.SaveOrderAsync(dbOrder, cancellationToken);
                                            progress?.Report($"Successfully submitted exit orders for {dbOrder.Ticker}.");
                                        }
                                        catch (Exception ex)
                                        {
                                            logger.LogError(ex, "Failed to submit exit orders for {Ticker}", dbOrder.Ticker);
                                            progress?.Report($"Error submitting exit orders for {dbOrder.Ticker}: {ex.Message}");
                                        }
                                    }
                                }
                                else if (pos != null)
                                {
                                    if (dbOrder.Status != "filled" && dbOrder.Status != "exit_submitted")
                                    {
                                        dbOrder.Status = "filled";
                                        dbOrder.UpdatedAt = DateTimeOffset.UtcNow;
                                        await _orderRepo.SaveOrderAsync(dbOrder, cancellationToken);
                                    }
                                }
                                else
                                {
                                    logger.LogDebug(
                                        "Order {OrderId} for {Ticker} is absent from the open-order snapshot; terminal state requires an authoritative broker update.",
                                        dbOrder.OrderId,
                                        dbOrder.Ticker);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to fetch active broker state; bypassing sequential trade check.");
                }
            }

            try
            {
                var processed = 0;
                var total = mergedTickers.Count;
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

                var marketState = await _candlePipeline.RunAsync(
                    new CandlePipelineRequest(
                        mergedTickers.ToArray(),
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

                await Parallel.ForEachAsync(
                    mergedTickers,
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
                            if (!marketState.TickerStates.TryGetValue(ticker, out var tickerState))
                            {
                                var reason = marketState.Failures.TryGetValue(ticker, out var failure)
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
                                activeTickerSnapshot,
                                openOrdersSnapshot,
                                openPositionsSnapshot,
                                screenerRelativeVolumeByTicker,
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




}

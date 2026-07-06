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

public sealed class LiveRunner(
    IMarketDataProvider provider,
    ICatalystProvider? catalystProvider,
    IBrokerClient? brokerClient,
    TradingFlow.Domain.Locking.ITickerLockService? lockService,
    TradingFlow.Domain.Orders.IOrderStateRepository? orderRepo,
    TradingFlow.Domain.Audit.IDecisionAuditRepository? auditRepo,
    ILogger<LiveRunner> logger,
    IArtifactWriter? artifactWriter = null,
    ICandleStore? candleStore = null)
{
    private readonly SignalGenerator _signalGenerator = new();
    private readonly StrategyDecisionBrain _decisionBrain = new();
    private readonly CandlePipelineEngine _candlePipeline = new(candleStore);
    private readonly TechnicalExecutionEngine _technicalExecutionEngine = new();
    private readonly ExecutionAuditor _auditor = new();
    private readonly IBrokerClient? _brokerClient = brokerClient;
    private readonly TradingFlow.Domain.Locking.ITickerLockService? _lockService = lockService;
    private readonly TradingFlow.Domain.Orders.IOrderStateRepository? _orderRepo = orderRepo;
    private readonly TradingFlow.Domain.Audit.IDecisionAuditRepository? _auditRepo = auditRepo;
    private readonly IArtifactWriter _artifactWriter = artifactWriter ?? AtomicFileArtifactWriter.Instance;

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
                    Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? ""));

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
            foreach(var st in strategies)
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
                                    if (run.Execution != null && run.Execution.ExtendedHours)
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
                                    if (dbOrder.Status != "cancelled" && dbOrder.Status != "expired")
                                    {
                                        dbOrder.Status = "cancelled";
                                        dbOrder.UpdatedAt = DateTimeOffset.UtcNow;
                                        await _orderRepo.SaveOrderAsync(dbOrder, cancellationToken);
                                    }
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
                        run.Execution?.ExtendedHours ?? true,
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

    private async Task ProcessTickerLiveAsync(
        BacktestRunConfig run,
        StrategyDefinition[] strategies,
        string ticker,
        TickerMarketState marketState,
        CatalystStreamer catalystStreamer,
        IReadOnlyCollection<string> activeTickers,
        IReadOnlyCollection<ActiveBrokerOrder> openOrders,
        IReadOnlyCollection<BrokerPosition> openPositions,
        IReadOnlyDictionary<string, decimal> screenerRelativeVolumeByTicker,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        var podId = Environment.MachineName;
        var lockAcquired = false;

        // 0. Acquire Distributed Lock
        if (_lockService != null)
        {
            var acquired = await _lockService.TryAcquireLockAsync(ticker, podId, TimeSpan.FromMinutes(3), cancellationToken);
            if (!acquired)
            {
                logger.LogDebug("Ticker {Ticker} is locked by another pod. Skipping...", ticker);
                return;
            }

            lockAcquired = true;
        }

        try
        {
        // 0.5 Recover state from DB if necessary
        if (_orderRepo != null)
        {
            var dbOrders = await _orderRepo.GetActiveOrdersByTickerAsync(ticker, cancellationToken);
            if (dbOrders.Count > 0)
            {
                logger.LogInformation("Recovered {Count} active orders for {Ticker} from database state.", dbOrders.Count, ticker);
                progress?.Report($"Recovered {dbOrders.Count} active orders for {ticker} from database state.");
                // Here the pod could theoretically reconstruct technical exits based on the recovered StrategyName and EntryPrice
            }
        }

        var barsByTimeframe = marketState.BarsByTimeframe
            .ToDictionary(
                x => x.Key,
                x => x.Value.OrderBy(bar => bar.Timestamp).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var snapshotsByTimeframe = marketState.SnapshotsByTimeframe
            .ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase);

        if (run.News.Enabled)
        {
            var allBars = barsByTimeframe.Values.SelectMany(x => x).ToArray();
            if (allBars.Length > 0)
            {
                var catalysts = await catalystStreamer.LoadTickerCatalystsAsync(
                    ticker,
                    allBars.Min(x => x.Timestamp),
                    allBars.Max(x => x.Timestamp),
                    cancellationToken);
                foreach (var timeframe in snapshotsByTimeframe.Keys)
                {
                    CatalystSnapshotAttacher.AttachToSnapshots(snapshotsByTimeframe[timeframe], catalysts, cancellationToken);
                }
            }
        }

        var readonlySnapshotsByTimeframe = snapshotsByTimeframe.ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<IndicatorSnapshot>)x.Value,
            StringComparer.OrdinalIgnoreCase);

        var resultsDir = Path.Combine(run.ResultsRoot, "live", run.RunName, ticker);
        Directory.CreateDirectory(resultsDir);

        // 3. Process Strategies & Write Chart Data
        foreach (var strategy in strategies)
        {
            if (!readonlySnapshotsByTimeframe.TryGetValue(strategy.Timeframe, out var snapshots) || snapshots.Count == 0)
                continue;

            if (!barsByTimeframe.TryGetValue(strategy.Timeframe, out var barsList))
                continue;

            var lastSnapshot = snapshots[^1];
            var lastBar = barsList[^1];
            var isFinvizRelativeVolume = screenerRelativeVolumeByTicker.TryGetValue(ticker, out var screenerRelativeVolume);
            var configuredRelativeVolume = _decisionBrain.ResolveEntryRelativeVolume(strategy, lastSnapshot);
            var relativeVolumeSource = isFinvizRelativeVolume ? "finviz_screener" : strategy.EntryRules.MinVolumeSpikeSource;
            var effectiveRelativeVolume = isFinvizRelativeVolume
                ? screenerRelativeVolume
                : configuredRelativeVolume ?? 0m;

            var chartData = new
            {
                Timestamp = lastSnapshot.Timestamp,
                Ticker = ticker,
                StrategyName = strategy.StrategyName,
                Timeframe = lastSnapshot.Timeframe,
                Close = lastSnapshot.CurrentPrice,
                Atr = lastSnapshot.Atr ?? 0m,
                RelativeVolume = effectiveRelativeVolume,
                SlotRelativeVolume = lastSnapshot.SlotRelativeVolume ?? 0m,
                SessionRelativeVolume = lastSnapshot.SessionRelativeVolume ?? 0m,
                CalculatedRelativeVolume = lastSnapshot.RelativeVolume ?? 0m,
                RelativeVolumeSource = relativeVolumeSource,
                SlotAverageVolume = lastSnapshot.SlotAverageVolume ?? 0m,
                CumulativeAverageVolume = lastSnapshot.CumulativeAverageVolume ?? 0m,
                AverageSessionVolume = lastSnapshot.AverageSessionVolume ?? 0m,
                RelativeVolumeSampleCount = lastSnapshot.RelativeVolumeSampleCount,
                Volume = lastSnapshot.CurrentVolume,
                Rsi = lastSnapshot.Rsi,
                Vwap = lastSnapshot.Vwap
            };
            var json = System.Text.Json.JsonSerializer.Serialize(chartData);
            await _artifactWriter.WriteTextAsync(Path.Combine(resultsDir, $"{ticker}_chart.json"), json, cancellationToken);

            var technicalExitHandled = await TrySubmitTechnicalExitAsync(
                run,
                strategy,
                ticker,
                barsByTimeframe,
                readonlySnapshotsByTimeframe,
                openOrders,
                openPositions,
                cancellationToken,
                progress);
            if (technicalExitHandled)
            {
                continue;
            }

            // Check for existing position/order limits
            var activeCount = activeTickers.Count(t => t.Equals(ticker, StringComparison.OrdinalIgnoreCase));
            if (run.Portfolio.MaxOpenTradesPerTicker > 0 && activeCount >= run.Portfolio.MaxOpenTradesPerTicker)
            {
                var reason = $"max_open_trades_per_ticker_reached (Actual: {activeCount}, Allowed: {run.Portfolio.MaxOpenTradesPerTicker})";
                logger.LogDebug("Skipping {Ticker} because {Count} open position(s) or order(s) already exist, hitting the limit of {Max}.", ticker, activeCount, run.Portfolio.MaxOpenTradesPerTicker);
                if (_auditRepo != null)
                {
                    await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                    {
                        RunName = run.RunName,
                        Ticker = ticker,
                        StrategyName = strategy.StrategyName,
                        Timestamp = DateTimeOffset.UtcNow,
                        Decision = "Skipped",
                        RejectionReason = reason,
                        SignalJson = JsonSerializer.Serialize(new
                        {
                            ticker,
                            strategy = strategy.StrategyName,
                            activeCount,
                            maxOpenTradesPerTicker = run.Portfolio.MaxOpenTradesPerTicker
                        })
                    }, cancellationToken);
                }

                continue;
            }

            // Evaluate signal
            var signal = _signalGenerator.CreateTradeSignal(strategy, barsList, snapshots, snapshots.Count - 1);
            if (signal != null)
            {
                var signalJson = BuildSignalAuditJson(
                    signal,
                    effectiveRelativeVolume,
                    relativeVolumeSource,
                    lastSnapshot.RelativeVolume,
                    lastSnapshot.SlotRelativeVolume,
                    lastSnapshot.SessionRelativeVolume,
                    lastSnapshot.SlotAverageVolume,
                    lastSnapshot.CumulativeAverageVolume,
                    lastSnapshot.AverageSessionVolume,
                    lastSnapshot.RelativeVolumeSampleCount);
                var decisionTimestamp = DateTimeOffset.UtcNow;

                var signalAvailableTimestamp = lastSnapshot.Timestamp.Add(ParseTimeframe(strategy.Timeframe));
                var confluenceRejection = _signalGenerator.GetConfluenceRejection(strategy, signalAvailableTimestamp, readonlySnapshotsByTimeframe);
                if (confluenceRejection is not null)
                {
                    if (_auditRepo != null)
                    {
                        await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                        {
                            RunName = run.RunName,
                            Ticker = ticker,
                            StrategyName = strategy.StrategyName,
                            Timestamp = decisionTimestamp,
                            Decision = "Rejected",
                            RejectionReason = confluenceRejection,
                            SignalJson = signalJson
                        }, cancellationToken);
                    }
                    continue;
                }

                var relativeVolume = effectiveRelativeVolume;
                var rejectionReason = _decisionBrain.GetLongEntryRejection(strategy, signal, lastSnapshot, relativeVolume, relativeVolumeSource);

                if (rejectionReason != null)
                {
                    if (_auditRepo != null)
                    {
                        await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                        {
                            RunName = run.RunName,
                            Ticker = ticker,
                            StrategyName = strategy.StrategyName,
                            Timestamp = decisionTimestamp,
                            Decision = "Rejected",
                            RejectionReason = rejectionReason,
                            SignalJson = signalJson
                        }, cancellationToken);
                    }
                    continue;
                }

                const string signalDirection = "long";

                // Check News Veto
                if (run.News.Enabled)
                {
                    progress?.Report($"Checking recent news sentiment for {ticker}...");
                    var recentNews = await catalystStreamer.LoadTickerCatalystsAsync(ticker, DateTimeOffset.UtcNow.AddMinutes(-run.News.VetoTtlMinutes), DateTimeOffset.UtcNow, cancellationToken);
                    var badNews = recentNews.FirstOrDefault(c => c.SentimentScore <= run.News.VetoNegativeThreshold);
                    if (badNews != null)
                    {
                        var vetoMsg = $"VETOED: Negative news detected ({badNews.SentimentScore:F2}): {badNews.Headline}";
                        logger.LogWarning(
                            "News veto for {Ticker} {StrategyName}: {VetoMessage}",
                            ticker,
                            strategy.StrategyName,
                            vetoMsg);
                        progress?.Report(vetoMsg);
                        _auditor.LogEvent(ticker, strategy.StrategyName, lastSnapshot.Timestamp, ExecutionState.OrderRejected, vetoMsg);

                        if (_auditRepo != null)
                        {
                            await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                            {
                                RunName = run.RunName,
                                Ticker = ticker,
                                StrategyName = strategy.StrategyName,
                                Timestamp = decisionTimestamp,
                                Decision = "Rejected",
                                RejectionReason = "news_veto",
                                SignalJson = signalJson
                            }, cancellationToken);
                        }
                        continue;
                    }
                }

                var orderSignal = ResolveExecutionOrderSignal(strategy, signal, readonlySnapshotsByTimeframe);
                if (orderSignal is null)
                {
                    var reason = $"execution_timeframe_missing (ExecutionTimeframe: {strategy.Execution.Timeframe})";
                    logger.LogWarning(
                        "Skipping {Ticker} {StrategyName}; execution timeframe {ExecutionTimeframe} has no usable snapshot.",
                        ticker,
                        strategy.StrategyName,
                        strategy.Execution.Timeframe);
                    progress?.Report($"Skipped {ticker}: {reason}");

                    if (_auditRepo != null)
                    {
                        await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                        {
                            RunName = run.RunName,
                            Ticker = ticker,
                            StrategyName = strategy.StrategyName,
                            Timestamp = decisionTimestamp,
                            Decision = "Rejected",
                            RejectionReason = reason,
                            SignalJson = signalJson
                        }, cancellationToken);
                    }

                    continue;
                }

                var msg = $"LIVE SIGNAL: {strategy.StrategyName} {ticker} {signalDirection} at {signal.CurrentPrice} (execution {strategy.Execution.Timeframe}: {orderSignal.CurrentPrice})";
                logger.LogInformation(
                    "Live signal generated for {Ticker} {StrategyName} {Direction} at {SignalPrice}; execution price {ExecutionPrice} from {ExecutionTimeframe}.",
                    ticker,
                    strategy.StrategyName,
                    signalDirection,
                    signal.CurrentPrice,
                    orderSignal.CurrentPrice,
                    strategy.Execution.Timeframe);
                progress?.Report(msg);
                _auditor.LogEvent(ticker, strategy.StrategyName, lastSnapshot.Timestamp, ExecutionState.SignalGenerated, msg);

                if (_auditRepo != null)
                {
                    await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                    {
                        RunName = run.RunName,
                        Ticker = ticker,
                        StrategyName = strategy.StrategyName,
                        Timestamp = decisionTimestamp,
                        Decision = "Accepted",
                        RejectionReason = null,
                        SignalJson = signalJson
                    }, cancellationToken);
                }

                // Submit Order if broker is configured
                if (_brokerClient != null && !run.Execution.DryRun && run.Execution.AllowLiveOrders)
                {
                    progress?.Report($"Calculating position size for {ticker}...");
                    var riskEngine = new TradingFlow.Engine.Risk.RiskEngine();
                    var order = riskEngine.CreateLongBracketOrderWithPositionRisk(
                        strategy,
                        orderSignal,
                        run.Portfolio.StartingCapital,
                        run.Portfolio.RiskPerTradePct,
                        run.Portfolio.MaxPositionValuePct);

                    if (order != null)
                    {
                        order = order with
                        {
                            ClientOrderId = TradingFlow.Domain.Orders.ClientOrderIdFactory.Create(run.RunName, ticker)
                        };
                        var shares = order.ShareQuantity;

                        try
                        {
                            progress?.Report($"Submitting Bracket Order: {shares} shares of {ticker}...");
                            var orderId = await _brokerClient.SubmitOrderAsync(order, cancellationToken);
                            var successMsg = $"Order submitted successfully: ID {orderId}";
                            logger.LogInformation(
                                "Order submitted for {Ticker} {StrategyName}. OrderId={OrderId} Shares={ShareQuantity}",
                                ticker,
                                strategy.StrategyName,
                                orderId,
                                shares);
                            progress?.Report(successMsg);
                            _auditor.LogEvent(ticker, strategy.StrategyName, DateTimeOffset.UtcNow, ExecutionState.BracketOrderSubmitted, successMsg);

                            if (_orderRepo != null)
                            {
                                var persistedOrder = new TradingFlow.Domain.Orders.PersistedOrder
                                {
                                    OrderId = orderId,
                                    Ticker = ticker,
                                    RunName = run.RunName,
                                    ClientOrderId = order.ClientOrderId,
                                    StrategyName = strategy.StrategyName,
                                    Broker = run.Execution.Broker,
                                    Status = run.Execution.ExtendedHours ? "pending_exit_setup" : "new",
                                    EntryPrice = order.LimitPrice,
                                    StopLossPrice = order.StopLossPrice,
                                    TakeProfitPrice = order.TakeProfitPrice,
                                    ShareQuantity = order.ShareQuantity,
                                    CreatedAt = DateTimeOffset.UtcNow,
                                    UpdatedAt = DateTimeOffset.UtcNow
                                };
                                await _orderRepo.SaveOrderAsync(persistedOrder, cancellationToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            var errMsg = $"Order submission failed: {ex.Message}";
                            logger.LogError(
                                ex,
                                "Order submission failed for {Ticker} {StrategyName}.",
                                ticker,
                                strategy.StrategyName);
                            progress?.Report(errMsg);
                        }
                    }
                    else
                    {
                        progress?.Report($"Skipped order for {ticker}: Calculated shares is 0.");
                    }
                }
            }
            else
            {
                progress?.Report($"Evaluated {strategy.StrategyName} for {ticker}: No signal generated.");
                if (_auditRepo != null)
                {
                    await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                    {
                        RunName = run.RunName,
                        Ticker = ticker,
                        StrategyName = strategy.StrategyName,
                        Timestamp = DateTimeOffset.UtcNow,
                        Decision = "NoSignal",
                        RejectionReason = "no_signal_generated",
                        SignalJson = JsonSerializer.Serialize(new
                        {
                            ticker,
                            strategy = strategy.StrategyName,
                            timestamp = lastSnapshot.Timestamp,
                            timeframe = lastSnapshot.Timeframe,
                            close = lastSnapshot.CurrentPrice,
                            volume = lastSnapshot.CurrentVolume,
                            relativeVolume = effectiveRelativeVolume,
                            relativeVolumeSource,
                            calculatedRelativeVolume = lastSnapshot.RelativeVolume,
                            slotRelativeVolume = lastSnapshot.SlotRelativeVolume,
                            sessionRelativeVolume = lastSnapshot.SessionRelativeVolume
                        })
                    }, cancellationToken);
                }
            }
        }
        }
        finally
        {
            if (lockAcquired && _lockService != null)
            {
                try
                {
                    await _lockService.ReleaseLockAsync(ticker, podId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to release ticker lock for {Ticker}. It will expire by TTL.", ticker);
                }
            }
        }
    }

    private static TradeSignal? ResolveExecutionOrderSignal(
        StrategyDefinition strategy,
        TradeSignal signal,
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe)
    {
        if (strategy.Execution.Timeframe.Equals(strategy.Timeframe, StringComparison.OrdinalIgnoreCase))
        {
            return signal;
        }

        if (!snapshotsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionSnapshots) ||
            executionSnapshots.Count == 0)
        {
            return null;
        }

        var orderedSnapshots = executionSnapshots
            .OrderBy(snapshot => snapshot.Timestamp)
            .ToArray();
        var signalCloseTimestamp = signal.Timestamp.Add(ParseTimeframe(strategy.Timeframe));
        var executionSnapshot = orderedSnapshots.FirstOrDefault(snapshot => snapshot.Timestamp >= signalCloseTimestamp)
            ?? orderedSnapshots.LastOrDefault(snapshot => snapshot.Timestamp >= signal.Timestamp);

        if (executionSnapshot is null)
        {
            return null;
        }

        return signal with
        {
            Timestamp = executionSnapshot.Timestamp,
            Timeframe = executionSnapshot.Timeframe,
            CurrentPrice = executionSnapshot.CurrentPrice,
            CurrentVolume = executionSnapshot.CurrentVolume,
            CurrentRsi = executionSnapshot.Rsi ?? signal.CurrentRsi,
            CurrentAtr = signal.CurrentAtr > 0m
                ? signal.CurrentAtr
                : executionSnapshot.Atr ?? signal.CurrentAtr
        };
    }

    private async Task<bool> TrySubmitTechnicalExitAsync(
        BacktestRunConfig run,
        StrategyDefinition strategy,
        string ticker,
        IReadOnlyDictionary<string, List<OhlcvBar>> barsByTimeframe,
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe,
        IReadOnlyCollection<ActiveBrokerOrder> openOrders,
        IReadOnlyCollection<BrokerPosition> openPositions,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        if (_brokerClient is null ||
            _orderRepo is null ||
            run.Execution.DryRun ||
            !run.Execution.AllowLiveOrders)
        {
            return false;
        }

        var position = openPositions.FirstOrDefault(x =>
            x.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
            x.Qty > 0 &&
            !x.Side.Equals("short", StringComparison.OrdinalIgnoreCase));
        if (position is null)
        {
            return false;
        }

        var activeOrders = await _orderRepo.GetActiveOrdersByTickerAsync(ticker, cancellationToken);
        var strategyOrder = activeOrders
            .Where(x => x.StrategyName.Equals(strategy.StrategyName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefault();
        if (strategyOrder is null)
        {
            logger.LogDebug(
                "Open position for {Ticker} has no active persisted order for strategy {StrategyName}; technical exit evaluation skipped.",
                ticker,
                strategy.StrategyName);
            return false;
        }

        if (strategyOrder.Status.Equals("technical_exit_submitted", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!barsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionBars) ||
            !snapshotsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionSnapshots) ||
            executionBars.Count == 0 ||
            executionSnapshots.Count == 0)
        {
            logger.LogWarning(
                "Cannot evaluate technical exit for {Ticker} {StrategyName}; execution timeframe {Timeframe} is missing.",
                ticker,
                strategy.StrategyName,
                strategy.Execution.Timeframe);
            return false;
        }

        var entryTimestamp = strategyOrder.CreatedAt;
        var entryIndex = FindFirstBarIndexAtOrAfter(executionBars, entryTimestamp);
        if (entryIndex >= executionBars.Count)
        {
            return true;
        }

        var entryPrice = position.EntryPrice > 0 ? position.EntryPrice : strategyOrder.EntryPrice;
        if (entryPrice <= 0)
        {
            logger.LogWarning(
                "Cannot evaluate technical exit for {Ticker} {StrategyName}; entry price is unavailable.",
                ticker,
                strategy.StrategyName);
            return false;
        }

        var latestSnapshot = executionSnapshots[^1];
        var initialStopLossPrice = strategyOrder.StopLossPrice > 0
            ? strategyOrder.StopLossPrice
            : entryPrice - ((latestSnapshot.Atr ?? 0m) * strategy.ExitRules.StopAtrMultiple);
        var stopDistance = entryPrice - initialStopLossPrice;
        if (stopDistance <= 0)
        {
            logger.LogWarning(
                "Cannot evaluate technical exit for {Ticker} {StrategyName}; stop distance is invalid. Entry={EntryPrice} Stop={StopLossPrice}",
                ticker,
                strategy.StrategyName,
                entryPrice,
                initialStopLossPrice);
            return false;
        }

        var takeProfitPrice = strategyOrder.TakeProfitPrice > 0
            ? strategyOrder.TakeProfitPrice
            : entryPrice + (stopDistance * strategy.ExitRules.TargetRMultiple);
        var currentStopLossPrice = initialStopLossPrice;
        var highestHighSinceEntry = entryPrice;

        string? exitReason = null;
        decimal? exitPrice = null;
        DateTimeOffset exitSignalTimestamp = DateTimeOffset.UtcNow;
        for (var index = entryIndex; index < executionBars.Count; index++)
        {
            var exitPriceLogTrend = TechnicalExecutionEngine.ComputeLogTrend(
                executionBars,
                index,
                strategy.ExitRules.ExitLogPriceLookbackBars,
                x => x.Close,
                addOne: false);
            var exitVolumeLogTrend = TechnicalExecutionEngine.ComputeLogTrend(
                executionBars,
                index,
                strategy.ExitRules.ExitLogVolumeLookbackBars,
                x => x.Volume,
                addOne: true);
            if (_technicalExecutionEngine.ShouldExitLongOnConfirmedVwapFailure(
                strategy,
                executionSnapshots,
                index,
                entryIndex,
                entryPrice,
                stopDistance,
                Math.Max(highestHighSinceEntry, executionBars[index].High),
                index - entryIndex))
            {
                exitReason = "confirmed_vwap_failure";
                exitPrice = executionBars[index].Close;
                exitSignalTimestamp = executionBars[index].Timestamp;
                break;
            }

            var (candidateExitPrice, candidateExitReason) = _technicalExecutionEngine.EvaluateBarForExit(
                strategy,
                executionBars[index],
                executionSnapshots[index],
                entryPrice,
                initialStopLossPrice,
                takeProfitPrice,
                entryTimestamp,
                stopDistance,
                index - entryIndex,
                ref currentStopLossPrice,
                ref highestHighSinceEntry,
                exitPriceLogTrend?.Slope,
                exitVolumeLogTrend?.Slope,
                index > 0 ? executionSnapshots[index - 1] : null);

            if (candidateExitReason is not null)
            {
                exitReason = candidateExitReason;
                exitPrice = candidateExitPrice;
                exitSignalTimestamp = executionBars[index].Timestamp;
                break;
            }
        }

        if (exitReason is null)
        {
            await TryRaiseBrokerTrailingStopAsync(
                strategy,
                ticker,
                strategyOrder,
                openOrders,
                initialStopLossPrice,
                currentStopLossPrice,
                cancellationToken,
                progress);

            return false;
        }

        logger.LogInformation(
            "Technical exit triggered for {Ticker} {StrategyName}. Reason={ExitReason} SignalTime={SignalTime} EstimatedExitPrice={ExitPrice}",
            ticker,
            strategy.StrategyName,
            exitReason,
            exitSignalTimestamp,
            exitPrice);
        progress?.Report($"Technical exit triggered for {ticker}: {exitReason}. Closing broker position...");

        foreach (var order in openOrders.Where(order =>
            order.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
            order.Side.Equals("sell", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                await _brokerClient.CancelOrderAsync(order.OrderId, cancellationToken);
                logger.LogInformation(
                    "Cancelled open sell order {OrderId} before technical exit close for {Ticker}.",
                    order.OrderId,
                    ticker);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to cancel sell order {OrderId} before technical exit close for {Ticker}.",
                    order.OrderId,
                    ticker);
            }
        }

        var closeQuantity = Math.Max(1, strategyOrder.ShareQuantity);
        var closed = await _brokerClient.ClosePositionAsync(ticker, closeQuantity, cancellationToken);
        if (closed)
        {
            await _orderRepo.UpdateOrderStatusAsync(strategyOrder.OrderId, "technical_exit_submitted", cancellationToken);
            _auditor.LogEvent(ticker, strategy.StrategyName, DateTimeOffset.UtcNow, ExecutionState.PositionClosed, $"Technical exit submitted: {exitReason}");
            if (_auditRepo is not null)
            {
                await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                {
                    RunName = run.RunName,
                    Ticker = ticker,
                    StrategyName = strategy.StrategyName,
                    Timestamp = DateTimeOffset.UtcNow,
                    Decision = "ExitSubmitted",
                    RejectionReason = exitReason,
                    SignalJson = JsonSerializer.Serialize(new
                    {
                        ticker,
                        strategy = strategy.StrategyName,
                        exitReason,
                        exitSignalTimestamp,
                        estimatedExitPrice = exitPrice,
                        entryPrice,
                        currentPrice = position.CurrentPrice,
                        unrealizedPl = position.UnrealizedPl
                    })
                }, cancellationToken);
            }

            progress?.Report($"Submitted technical exit close for {ticker}: {exitReason}.");
            return true;
        }

        logger.LogWarning(
            "Broker rejected technical exit close for {Ticker} {StrategyName}. Reason={ExitReason}",
            ticker,
            strategy.StrategyName,
            exitReason);
        if (_auditRepo is not null)
        {
            await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
            {
                RunName = run.RunName,
                Ticker = ticker,
                StrategyName = strategy.StrategyName,
                Timestamp = DateTimeOffset.UtcNow,
                Decision = "ExitRejected",
                RejectionReason = exitReason,
                SignalJson = JsonSerializer.Serialize(new
                {
                    ticker,
                    strategy = strategy.StrategyName,
                    exitReason,
                    entryPrice,
                    currentPrice = position.CurrentPrice,
                    unrealizedPl = position.UnrealizedPl
                })
            }, cancellationToken);
        }

        return true;
    }

    private async Task TryRaiseBrokerTrailingStopAsync(
        StrategyDefinition strategy,
        string ticker,
        PersistedOrder strategyOrder,
        IReadOnlyCollection<ActiveBrokerOrder> openOrders,
        decimal initialStopLossPrice,
        decimal calculatedStopLossPrice,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        if (_brokerClient is null ||
            _orderRepo is null ||
            !strategy.ExitRules.EnableAtrTrailingStop ||
            calculatedStopLossPrice <= initialStopLossPrice ||
            calculatedStopLossPrice <= strategyOrder.StopLossPrice + 0.01m)
        {
            return;
        }

        var stopOrder = openOrders
            .Where(order =>
                order.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                order.Side.Equals("sell", StringComparison.OrdinalIgnoreCase) &&
                order.StopPrice is not null)
            .OrderByDescending(order => order.CreatedAt)
            .FirstOrDefault();
        if (stopOrder is null)
        {
            logger.LogWarning(
                "Trailing stop for {Ticker} {StrategyName} calculated at {StopLossPrice}, but no open broker stop leg was found.",
                ticker,
                strategy.StrategyName,
                calculatedStopLossPrice);
            progress?.Report($"Trailing stop ready for {ticker} at {calculatedStopLossPrice:0.00}, but no broker stop leg is open.");
            return;
        }

        if (stopOrder.StopPrice is { } brokerStopPrice &&
            calculatedStopLossPrice <= brokerStopPrice + 0.01m)
        {
            return;
        }

        var modified = await _brokerClient.ModifyOrderAsync(stopOrder.OrderId, calculatedStopLossPrice, 0m, cancellationToken);
        if (!modified)
        {
            logger.LogWarning(
                "Broker did not accept trailing stop update for {Ticker} {StrategyName}. OrderId={OrderId} NewStop={StopLossPrice}",
                ticker,
                strategy.StrategyName,
                stopOrder.OrderId,
                calculatedStopLossPrice);
            return;
        }

        strategyOrder.StopLossPrice = calculatedStopLossPrice;
        strategyOrder.UpdatedAt = DateTimeOffset.UtcNow;
        await _orderRepo.SaveOrderAsync(strategyOrder, cancellationToken);

        logger.LogInformation(
            "Raised trailing stop for {Ticker} {StrategyName}. OrderId={OrderId} NewStop={StopLossPrice}",
            ticker,
            strategy.StrategyName,
            stopOrder.OrderId,
            calculatedStopLossPrice);
        progress?.Report($"Raised trailing stop for {ticker} to {calculatedStopLossPrice:0.00}.");
    }

    private async Task SaveTickerPipelineFailureAuditsAsync(
        BacktestRunConfig run,
        IReadOnlyCollection<StrategyDefinition> strategies,
        string ticker,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_auditRepo is null)
        {
            return;
        }

        var signalJson = JsonSerializer.Serialize(new
        {
            ticker,
            error = reason,
            timestamp = DateTimeOffset.UtcNow
        });

        foreach (var strategy in strategies)
        {
            await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
            {
                RunName = run.RunName,
                Ticker = ticker,
                StrategyName = strategy.StrategyName,
                Timestamp = DateTimeOffset.UtcNow,
                Decision = "Rejected",
                RejectionReason = $"ticker_pipeline_failed ({reason})",
                SignalJson = signalJson
            }, cancellationToken);
        }
    }

    private static StrategyDefinition[] ApplyRunSessionPolicy(BacktestRunConfig run, IReadOnlyCollection<StrategyDefinition> strategies)
    {
        if (!run.Execution.ExtendedHours)
        {
            return strategies.ToArray();
        }

        return strategies
            .Select(strategy => strategy with
            {
                Session = strategy.Session with { UseExtendedHours = true }
            })
            .ToArray();
    }

    private static string[] ResolveRequiredTimeframes(BacktestRunConfig run, IReadOnlyCollection<StrategyDefinition> strategies)
    {
        var required = new HashSet<string>(run.Intervals.Where(x => !String.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
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

    private static int FindFirstBarIndexAtOrAfter(IReadOnlyList<OhlcvBar> bars, DateTimeOffset timestamp)
    {
        for (var index = 0; index < bars.Count; index++)
        {
            if (bars[index].Timestamp >= timestamp)
            {
                return index;
            }
        }

        return bars.Count;
    }

    private static TimeSpan ParseTimeframe(string timeframe) => TimeframeParser.Parse(timeframe);

    private static decimal? ResolveEntryRelativeVolume(StrategyDefinition strategy, IndicatorSnapshot snapshot)
    {
        return strategy.EntryRules.MinVolumeSpikeSource.ToLowerInvariant() switch
        {
            "session_vs_average_day" or "session" or "finviz_style" => snapshot.SessionRelativeVolume,
            "slot_bar" or "bar_same_time" => snapshot.SlotRelativeVolume,
            _ => snapshot.RelativeVolume
        };
    }

    private static string FormatLocalTime(DateTimeOffset timestamp)
    {
        try
        {
            var timezone = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");
            return TimeZoneInfo.ConvertTime(timestamp, timezone).ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return timestamp.LocalDateTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static string BuildSignalAuditJson(
        TradeSignal signal,
        decimal relativeVolumeUsed,
        string relativeVolumeSource,
        decimal? calculatedRelativeVolume,
        decimal? slotRelativeVolume,
        decimal? sessionRelativeVolume,
        decimal? slotAverageVolume,
        decimal? cumulativeAverageVolume,
        decimal? averageSessionVolume,
        int relativeVolumeSampleCount)
    {
        return JsonSerializer.Serialize(new
        {
            signal,
            relativeVolumeUsed,
            relativeVolumeSource,
            calculatedRelativeVolume,
            slotRelativeVolume,
            sessionRelativeVolume,
            slotAverageVolume,
            cumulativeAverageVolume,
            averageSessionVolume,
            relativeVolumeSampleCount
        });
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
            String.IsNullOrWhiteSpace(run.Mode) ? "paper" : run.Mode,
            run.RunName,
            run.Provider);
    }

    private static bool IsEntryExposureOrder(ActiveBrokerOrder order)
    {
        return order.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) &&
               IsOpenBrokerStatus(order.Status);
    }

    private static bool AllowsLong(StrategyDefinition strategy)
    {
        return strategy.Direction.Equals("long", StringComparison.OrdinalIgnoreCase) ||
            strategy.Direction.Equals("long_short", StringComparison.OrdinalIgnoreCase) ||
            strategy.Direction.Equals("both", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AllowsShort(StrategyDefinition strategy)
    {
        return strategy.EntryRules.EnableShort &&
            (strategy.Direction.Equals("short", StringComparison.OrdinalIgnoreCase) ||
             strategy.Direction.Equals("long_short", StringComparison.OrdinalIgnoreCase) ||
             strategy.Direction.Equals("both", StringComparison.OrdinalIgnoreCase));
    }

    private static string ChoosePrimaryRejection(string longRejection, string shortRejection)
    {
        if (!longRejection.StartsWith("direction_", StringComparison.OrdinalIgnoreCase))
        {
            return longRejection;
        }

        if (!shortRejection.StartsWith("direction_", StringComparison.OrdinalIgnoreCase))
        {
            return shortRejection;
        }

        return longRejection;
    }

    private static bool IsOpenExposurePosition(BrokerPosition position)
    {
        return position.Qty != 0;
    }

    private static bool IsOpenBrokerStatus(string status)
    {
        return status.Equals("new", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("accepted", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("pending_new", StringComparison.OrdinalIgnoreCase) ||
               status.Equals("partially_filled", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveWorkerCount(int configuredWorkerCount)
    {
        if (configuredWorkerCount > 0)
        {
            return configuredWorkerCount;
        }

        return Math.Max(1, Environment.ProcessorCount - 1);
    }

    private static decimal? ParseFinvizRelativeVolumeFilter(string filter)
    {
        if (String.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        var query = Uri.UnescapeDataString(filter);
        var match = System.Text.RegularExpressions.Regex.Match(
            query,
            @"sh_relvol_o(?<value>\d+(?:\.\d+)?)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(250));

        return match.Success &&
            Decimal.TryParse(
                match.Groups["value"].Value,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
            ? value
            : null;
    }
}


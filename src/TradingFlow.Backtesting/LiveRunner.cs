using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Sessions;
using TradingFlow.Engine.Strategies;
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
    ILogger<LiveRunner> logger)
{
    private readonly IndicatorEngine _indicatorEngine = new();
    private readonly SignalGenerator _signalGenerator = new();
    private readonly BasicStrategyEvaluator _evaluator = new();
    private readonly StrategySessionClock _sessionClock = new();
    private readonly BarResampler _barResampler = new();
    private readonly ExecutionAuditor _auditor = new();
    private readonly IBrokerClient? _brokerClient = brokerClient;
    private readonly TradingFlow.Domain.Locking.ITickerLockService? _lockService = lockService;
    private readonly TradingFlow.Domain.Orders.IOrderStateRepository? _orderRepo = orderRepo;
    private readonly TradingFlow.Domain.Audit.IDecisionAuditRepository? _auditRepo = auditRepo;

    public async Task RunAsync(
        BacktestRunConfig run,
        StrategyDefinition[] strategies,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
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
                    var source = new TradingFlow.Finviz.FinvizScreenerTickerSource(finvizClient, filter);
                    var screenTickers = await source.GetTickersAsync(cancellationToken);
                    foreach (var t in screenTickers) mergedTickers.Add(t);
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
            progress?.Report($"LiveRunner iteration starting at {DateTimeOffset.UtcNow:T}");

            var end = DateTimeOffset.UtcNow;
            
            var maxLookbackDays = 5;
            foreach(var st in strategies)
            {
                var timeframes = new List<string> { st.Execution.Timeframe, st.Timeframe };
                if (st.Confluence.Enabled) timeframes.Add(st.Confluence.Timeframe);

                foreach (var tf in timeframes.Select(t => t.ToLowerInvariant()))
                {
                    if (tf.EndsWith("min") || tf.EndsWith("m")) maxLookbackDays = Math.Max(maxLookbackDays, 10);
                    else if (tf.EndsWith("hour") || tf.EndsWith("h")) maxLookbackDays = Math.Max(maxLookbackDays, 40);
                    else if (tf.EndsWith("day") || tf.EndsWith("d")) maxLookbackDays = Math.Max(maxLookbackDays, 120);
                }
            }
            var start = end.AddDays(-maxLookbackDays); // Warmup

            var activeTickers = new List<string>();
            if (_brokerClient != null)
            {
                try
                {
                    var openOrders = await _brokerClient.GetOpenOrdersAsync(cancellationToken);
                    
                    // State Reconciliation: Adopt any open orders from the broker that are not in our database
                    if (_orderRepo != null)
                    {
                        foreach (var order in openOrders)
                        {
                            var existing = await _orderRepo.GetOrderAsync(order.OrderId, cancellationToken);
                            if (existing == null)
                            {
                                logger.LogInformation("State reconciliation: Adopting orphan broker order {OrderId} for {Ticker}.", order.OrderId, order.Ticker);
                                progress?.Report($"State reconciliation: Adopting orphan broker order {order.OrderId} for {order.Ticker}.");
                                
                                var newOrder = new TradingFlow.Domain.Orders.PersistedOrder
                                {
                                    OrderId = order.OrderId,
                                    Ticker = order.Ticker,
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
                    }

                    var openPositions = await _brokerClient.GetOpenPositionsAsync(cancellationToken);
                    foreach (var order in openOrders) activeTickers.Add(order.Ticker);
                    foreach (var pos in openPositions) activeTickers.Add(pos.Ticker);

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
                var activeTickerSnapshot = activeTickers.ToArray();
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
                            progress?.Report($"Fetching data and processing pipeline for {ticker}");
                            await ProcessTickerLiveAsync(run, strategies, ticker, start, end, catalystStreamer, activeTickerSnapshot, token, progress);
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
        DateTimeOffset start,
        DateTimeOffset end,
        CatalystStreamer catalystStreamer,
        IReadOnlyCollection<string> activeTickers,
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

        // 1. Fetch configured feeds, then derive missing strategy timeframes to keep paper/live aligned with backtests.
        var requiredTimeframes = ResolveRequiredTimeframes(run, strategies);
        var requestedIntervals = run.Intervals
            .Where(x => !String.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requestedIntervals.Length == 0)
        {
            requestedIntervals = requiredTimeframes;
        }

        var barsByTimeframe = new Dictionary<string, List<OhlcvBar>>(StringComparer.OrdinalIgnoreCase);
        await foreach (var bar in provider.GetBarsAsync(new[] { ticker }, requestedIntervals, start, end, cancellationToken))
        {
            if (!barsByTimeframe.ContainsKey(bar.Timeframe))
            {
                barsByTimeframe[bar.Timeframe] = new List<OhlcvBar>();
            }
            barsByTimeframe[bar.Timeframe].Add(bar);
        }

        AddDerivedTimeframes(run, requiredTimeframes, barsByTimeframe);

        // 2. Compute Indicators
        var snapshotsByTimeframe = new Dictionary<string, IReadOnlyList<IndicatorSnapshot>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in barsByTimeframe)
        {
            var bars = kvp.Value.OrderBy(b => b.Timestamp).ToList();
            var snaps = _indicatorEngine.Compute(bars).ToList();
            snapshotsByTimeframe[kvp.Key] = snaps;
        }

        var resultsDir = Path.Combine(run.ResultsRoot, "live", run.RunName, ticker);
        Directory.CreateDirectory(resultsDir);

        // 3. Process Strategies & Write Chart Data
        foreach (var strategy in strategies)
        {
            if (!snapshotsByTimeframe.TryGetValue(strategy.Timeframe, out var snapshots) || snapshots.Count == 0)
                continue;
            
            if (!barsByTimeframe.TryGetValue(strategy.Timeframe, out var barsList))
                continue;

            var lastSnapshot = snapshots[^1];
            var lastBar = barsList[^1];

            var chartData = new
            {
                Timestamp = lastSnapshot.Timestamp,
                Ticker = ticker,
                StrategyName = strategy.StrategyName,
                Close = lastSnapshot.CurrentPrice,
                Atr = lastSnapshot.Atr ?? 0m,
                RelativeVolume = lastSnapshot.RelativeVolume ?? 0m,
                Volume = lastSnapshot.CurrentVolume,
                Rsi = lastSnapshot.Rsi,
                Vwap = lastSnapshot.Vwap
            };
            var json = System.Text.Json.JsonSerializer.Serialize(chartData);
            File.WriteAllText(Path.Combine(resultsDir, $"{ticker}_chart.json"), json); 
            
            // Check for existing position/order limits
            var activeCount = activeTickers.Count(t => t.Equals(ticker, StringComparison.OrdinalIgnoreCase));
            if (run.Portfolio.MaxOpenTradesPerTicker > 0 && activeCount >= run.Portfolio.MaxOpenTradesPerTicker)
            {
                logger.LogDebug("Skipping {Ticker} because {Count} open position(s) or order(s) already exist, hitting the limit of {Max}.", ticker, activeCount, run.Portfolio.MaxOpenTradesPerTicker);
                continue;
            }

            // Evaluate signal
            var signal = _signalGenerator.CreateTradeSignal(strategy, barsList, snapshots, snapshots.Count - 1);
            if (signal != null)
            {
                var rejectionReason = _evaluator.GetLongEntryRejection(strategy, signal, lastSnapshot.RelativeVolume ?? 0m);
                var signalJson = JsonSerializer.Serialize(signal);
                
                if (rejectionReason != null)
                {
                    if (_auditRepo != null)
                    {
                        await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                        {
                            RunName = run.RunName,
                            Ticker = ticker,
                            StrategyName = strategy.StrategyName,
                            Timestamp = lastSnapshot.Timestamp,
                            Decision = "Rejected",
                            RejectionReason = rejectionReason,
                            SignalJson = signalJson
                        }, cancellationToken);
                    }
                    continue;
                }

                // Check News Veto
                if (run.News.Enabled)
                {
                    progress?.Report($"Checking recent news sentiment for {ticker}...");
                    var recentNews = await catalystStreamer.LoadTickerCatalystsAsync(ticker, DateTimeOffset.UtcNow.AddMinutes(-run.News.VetoTtlMinutes), DateTimeOffset.UtcNow, cancellationToken);
                    var badNews = recentNews.FirstOrDefault(c => c.SentimentScore <= run.News.VetoNegativeThreshold);
                    if (badNews != null)
                    {
                        var vetoMsg = $"VETOED: Negative news detected ({badNews.SentimentScore:F2}): {badNews.Headline}";
                        logger.LogWarning("{Message}", vetoMsg);
                        progress?.Report(vetoMsg);
                        _auditor.LogEvent(ticker, strategy.StrategyName, lastSnapshot.Timestamp, ExecutionState.OrderRejected, vetoMsg);
                        
                        if (_auditRepo != null)
                        {
                            await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                            {
                                RunName = run.RunName,
                                Ticker = ticker,
                                StrategyName = strategy.StrategyName,
                                Timestamp = lastSnapshot.Timestamp,
                                Decision = "Rejected",
                                RejectionReason = "news_veto",
                                SignalJson = signalJson
                            }, cancellationToken);
                        }
                        continue;
                    }
                }

                var msg = $"LIVE SIGNAL: {strategy.StrategyName} {ticker} {strategy.Direction} at {signal.CurrentPrice}";
                logger.LogInformation("{Message}", msg);
                progress?.Report(msg);
                _auditor.LogEvent(ticker, strategy.StrategyName, lastSnapshot.Timestamp, ExecutionState.SignalGenerated, msg);

                if (_auditRepo != null)
                {
                    await _auditRepo.SaveAuditAsync(new TradingFlow.Domain.Audit.DecisionAuditRecord
                    {
                        RunName = run.RunName,
                        Ticker = ticker,
                        StrategyName = strategy.StrategyName,
                        Timestamp = lastSnapshot.Timestamp,
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
                    var order = riskEngine.CreateLongBracketOrder(
                        strategy, 
                        signal, 
                        run.Portfolio.StartingCapital, 
                        run.Portfolio.RiskPerTradePct);
                    
                    if (order != null)
                    {
                        var shares = order.ShareQuantity;
                        
                        try
                        {
                            progress?.Report($"Submitting Bracket Order: {shares} shares of {ticker}...");
                            var orderId = await _brokerClient.SubmitOrderAsync(order, cancellationToken);
                            var successMsg = $"Order submitted successfully: ID {orderId}";
                            logger.LogInformation("{Message}", successMsg);
                            progress?.Report(successMsg);
                            _auditor.LogEvent(ticker, strategy.StrategyName, DateTimeOffset.UtcNow, ExecutionState.BracketOrderSubmitted, successMsg);

                            if (_orderRepo != null)
                            {
                                var persistedOrder = new TradingFlow.Domain.Orders.PersistedOrder
                                {
                                    OrderId = orderId,
                                    Ticker = ticker,
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
                            logger.LogError(ex, "{Message}", errMsg);
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

    private void AddDerivedTimeframes(
        BacktestRunConfig run,
        IReadOnlyCollection<string> requiredTimeframes,
        IDictionary<string, List<OhlcvBar>> barsByTimeframe)
    {
        var missing = requiredTimeframes
            .Where(timeframe => !barsByTimeframe.ContainsKey(timeframe))
            .ToArray();

        if (missing.Length == 0)
        {
            return;
        }

        if (!barsByTimeframe.TryGetValue(run.DerivedTimeframes.Source, out var sourceBars))
        {
            throw new InvalidOperationException(
                $"Cannot derive required paper/live timeframes [{String.Join(", ", missing)}] because source timeframe {run.DerivedTimeframes.Source} was not loaded.");
        }

        foreach (var target in missing)
        {
            barsByTimeframe[target] = _barResampler.Resample(sourceBars, target).ToList();
        }
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

    private static int ResolveWorkerCount(int configuredWorkerCount)
    {
        if (configuredWorkerCount > 0)
        {
            return configuredWorkerCount;
        }

        return Math.Max(1, Environment.ProcessorCount - 1);
    }
}

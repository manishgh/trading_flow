using System.Text.Json;
using TradingFlow.Data.Csv;
using TradingFlow.Data.Yahoo;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Sessions;
using TradingFlow.Engine.Strategies;
using TradingFlow.Data.Catalysts;

namespace TradingFlow.Backtesting;

public sealed class BacktestRunner(SimpleYamlReader yamlReader)
{
    private readonly IndicatorEngine _indicatorEngine = new();
    private readonly BasicStrategyEvaluator _strategyEvaluator = new();
    private readonly StrategySessionClock _sessionClock = new();
    private readonly BarResampler _barResampler = new();
    private readonly BacktestValidator _validator = new();
    private readonly SignalGenerator _signalGenerator = new();
    private readonly TradingFlow.Engine.Execution.ExecutionAuditor _auditor = new();

    public async Task<BacktestResult> RunAsync(
        string configPath,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null)
    {
        var startedAt = DateTimeOffset.UtcNow;
        progress?.Report(BacktestProgress.StageOnly("loading_config", $"Loading run config {Path.GetFileName(configPath)}."));
        var run = yamlReader.ReadBacktestRun(configPath);
        progress?.Report(BacktestProgress.StageOnly("loading_strategies", $"Loading {run.Strategies.Count} strategy config(s)."));
        var strategies = run.Strategies.Select(yamlReader.ReadStrategy).ToArray();
        
        return await RunAsync(run, strategies, startedAt, GetResultPath(run), cancellationToken, progress);
    }

    public async Task<BacktestResult> RunAsync(
        BacktestRunConfig run,
        StrategyDefinition[] strategies,
        DateTimeOffset startedAt,
        string resultPath,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null)
    {
        var provider = CreateProvider(run);
        var catalystStreamer = new CatalystStreamer(CreateNewsProvider(run));
        var (windowStart, windowEnd) = ResolveWindow(run.TimeWindow);

        ValidateRun(run, strategies);

        var completedTickerCount = 0;
        var totalTickerCount = run.Tickers.Count;
        var pipeline = new TplBacktestPipeline();
        progress?.Report(new BacktestProgress(
            "running_tickers",
            $"Running {totalTickerCount} ticker pipeline(s) with {strategies.Length} strategy config(s).",
            null,
            completedTickerCount,
            totalTickerCount));
        var batches = await pipeline.RunByTickerAsync<TickerBacktestBatch>(
            run,
            async (ticker, token) =>
            {
                progress?.Report(new BacktestProgress(
                    "running_ticker",
                    $"Processing {ticker}.",
                    ticker,
                    Volatile.Read(ref completedTickerCount),
                    totalTickerCount));
                var batch = await ProcessTickerAsync(run, provider, catalystStreamer, strategies, ticker, windowStart, windowEnd, token);
                var completed = Interlocked.Increment(ref completedTickerCount);
                progress?.Report(new BacktestProgress(
                    "running_tickers",
                    $"Finished {ticker}.",
                    ticker,
                    completed,
                    totalTickerCount));
                return batch;
            },
            cancellationToken);

        progress?.Report(BacktestProgress.StageOnly("building_results", "Building portfolio, strategy, and analyzer results."));
        var tickerResults = batches
            .Select(x => x.Result)
            .OrderBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allBars = batches
            .SelectMany(x => x.ProcessedBars)
            .ToArray();
        var candidates = batches
            .SelectMany(x => x.CandidateTrades)
            .OrderBy(x => x.EntryTimestamp)
            .ThenBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var strategyResults = BuildStrategyResults(run.Portfolio, strategies, candidates);
        var diagnostics = BuildDiagnostics(
            strategies,
            strategyResults,
            batches.SelectMany(x => x.Diagnostics).ToArray());
        var completedTrades = strategyResults.SelectMany(x => x.CompletedTrades).ToArray();
        var bestStrategy = strategyResults
            .OrderByDescending(x => x.NetProfit)
            .ThenBy(x => x.MaxDrawdownPct)
            .FirstOrDefault();
        var winner = bestStrategy is null
            ? null
            : new WinnerStrategySummary(
                bestStrategy.StrategyId,
                bestStrategy.StrategyName,
                bestStrategy.EndingCapital,
                bestStrategy.NetProfit,
                bestStrategy.TotalReturnPct,
                bestStrategy.MaxDrawdownPct,
                bestStrategy.AcceptedTradeCount,
                bestStrategy.RejectedTradeCount);
        var benchmarkBars = await LoadBenchmarkBarsAsync(run, provider, windowStart, windowEnd, cancellationToken);
        var validation = _validator.Validate(run, allBars, benchmarkBars, strategyResults);

        progress?.Report(BacktestProgress.StageOnly("writing_result", $"Writing result {Path.GetFileName(resultPath)}."));
        var result = new BacktestResult(
            run.RunName,
            resultPath,
            startedAt,
            DateTimeOffset.UtcNow,
            run.Portfolio.StartingCapital,
            bestStrategy?.EndingCapital ?? run.Portfolio.StartingCapital,
            bestStrategy?.NetProfit ?? 0,
            bestStrategy?.TotalReturnPct ?? 0,
            bestStrategy?.MaxDrawdownPct ?? 0,
            winner,
            tickerResults.Sum(x => x.ProcessedBarCount),
            candidates.Length,
            strategyResults.Sum(x => x.AcceptedTradeCount),
            strategyResults.Sum(x => x.RejectedTradeCount),
            strategyResults.Sum(x => x.WinningTradeCount),
            strategyResults.Sum(x => x.LosingTradeCount),
            strategyResults,
            tickerResults,
            validation,
            completedTrades,
            diagnostics,
            Array.Empty<FinalizedOrder>());

        result = await WriteResultAsync(resultPath, result, cancellationToken);
        progress?.Report(BacktestProgress.StageOnly("completed", "Backtest completed."));
        return result;
    }

    private static void ValidateRun(BacktestRunConfig run, IReadOnlyCollection<StrategyDefinition> strategies)
    {
        if (!run.Mode.Equals("backtest", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Expected mode backtest, got {run.Mode}.");
        }

        if (strategies.Count == 0)
        {
            throw new InvalidOperationException("At least one strategy is required.");
        }

        if (!run.Engine.Pipeline.Equals("tpl", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported pipeline: {run.Engine.Pipeline}.");
        }

        if (run.Portfolio.MaxConcurrentPositions <= 0)
        {
            throw new InvalidOperationException("portfolio.max_concurrent_positions must be greater than zero.");
        }

        if (run.Mode.Equals("live", StringComparison.OrdinalIgnoreCase) &&
            run.Execution.AllowLiveOrders &&
            run.Execution.DryRun)
        {
            throw new InvalidOperationException("Live orders cannot be enabled while dry_run is true.");
        }
    }

    private static (DateTimeOffset Start, DateTimeOffset End) ResolveWindow(TimeWindowConfig timeWindow)
    {
        if (timeWindow.Type.Equals("fixed", StringComparison.OrdinalIgnoreCase))
        {
            if (timeWindow.Start is null || timeWindow.End is null)
            {
                throw new InvalidOperationException("Fixed time windows require start and end.");
            }

            return (timeWindow.Start.Value, timeWindow.End.Value);
        }

        if (timeWindow.Type.Equals("rolling", StringComparison.OrdinalIgnoreCase))
        {
            var end = timeWindow.End ?? DateTimeOffset.UtcNow;
            return (timeWindow.Start ?? end.AddDays(-timeWindow.LookbackDays), end);
        }

        throw new NotSupportedException($"Unsupported time_window.type: {timeWindow.Type}");
    }

    private async Task<TickerBacktestBatch> ProcessTickerAsync(
        BacktestRunConfig run,
        IMarketDataProvider provider,
        CatalystStreamer catalystStreamer,
        IReadOnlyCollection<StrategyDefinition> strategies,
        string ticker,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        try
        {
            var bars = await LoadTickerBarsAsync(provider, ticker, run.Intervals, windowStart, windowEnd, cancellationToken);
            var barsByTimeframe = bars
                .GroupBy(x => x.Timeframe, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => (IReadOnlyList<OhlcvBar>)x.OrderBy(y => y.Timestamp).ToArray(), StringComparer.OrdinalIgnoreCase);
            AddRequiredDerivedTimeframes(run, strategies, barsByTimeframe);
            var processedBars = barsByTimeframe.Values.SelectMany(x => x).ToArray();
            var snapshotsByTimeframe = barsByTimeframe.ToDictionary(
                x => x.Key,
                x => _indicatorEngine.Compute(x.Value).Select(s => s).ToList(),
                StringComparer.OrdinalIgnoreCase);

            var catalysts = await catalystStreamer.LoadTickerCatalystsAsync(ticker, windowStart, windowEnd, cancellationToken);
            foreach (var timeframe in snapshotsByTimeframe.Keys)
            {
                var snaps = snapshotsByTimeframe[timeframe];
                for (int i = 0; i < snaps.Count; i++)
                {
                    var snap = snaps[i];
                    var catalyst = catalysts.LastOrDefault(c => c.Timestamp <= snap.Timestamp && c.Timestamp >= snap.Timestamp.AddDays(-3));
                    if (catalyst != null)
                    {
                        snaps[i] = snap with { Catalyst = catalyst };
                    }
                }
            }
            
            // convert back to IReadOnlyList
            var readonlySnapshots = snapshotsByTimeframe.ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<IndicatorSnapshot>)x.Value,
                StringComparer.OrdinalIgnoreCase);

            var candidates = new List<BacktestCandidateTrade>();
            var diagnostics = new List<StrategyCandidateDiagnostics>();
            foreach (var strategy in strategies)
            {
                if (!barsByTimeframe.TryGetValue(strategy.Timeframe, out var strategyBars) ||
                    !readonlySnapshots.TryGetValue(strategy.Timeframe, out var strategySnapshots))
                {
                    diagnostics.Add(StrategyCandidateDiagnostics.MissingTimeframe(strategy, "missing_signal_timeframe"));
                    continue;
                }

                if (!barsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionBars) ||
                    !readonlySnapshots.TryGetValue(strategy.Execution.Timeframe, out var executionSnapshots))
                {
                    diagnostics.Add(StrategyCandidateDiagnostics.MissingTimeframe(strategy, "missing_execution_timeframe"));
                    continue;
                }

                var strategyEvaluation = CreateStrategyCandidates(
                    run,
                    strategy,
                    strategyBars,
                    strategySnapshots,
                    executionBars,
                    executionSnapshots,
                    readonlySnapshots);
                candidates.AddRange(strategyEvaluation.Candidates);
                diagnostics.Add(strategyEvaluation.Diagnostics);
            }

            return new TickerBacktestBatch(
                TickerBacktestResult.Completed(ticker, barsByTimeframe.Values.Sum(x => x.Count), candidates.Count),
                candidates,
                diagnostics,
                processedBars);
        }
        catch (Exception exception) when (!run.Engine.FailFast)
        {
            return new TickerBacktestBatch(
                TickerBacktestResult.Failed(ticker, exception),
                Array.Empty<BacktestCandidateTrade>(),
                Array.Empty<StrategyCandidateDiagnostics>(),
                Array.Empty<OhlcvBar>());
        }
    }

    private void AddRequiredDerivedTimeframes(
        BacktestRunConfig run,
        IReadOnlyCollection<StrategyDefinition> strategies,
        IDictionary<string, IReadOnlyList<OhlcvBar>> barsByTimeframe)
    {
        var requiredTargets = ResolveRequiredDerivedTimeframes(run, strategies, barsByTimeframe.Keys);
        if (requiredTargets.Count == 0)
        {
            return;
        }

        if (!barsByTimeframe.TryGetValue(run.DerivedTimeframes.Source, out var sourceBars))
        {
            throw new InvalidOperationException(
                $"Cannot derive required timeframes [{String.Join(", ", requiredTargets)}] because source timeframe {run.DerivedTimeframes.Source} was not loaded.");
        }

        foreach (var target in requiredTargets)
        {
            if (barsByTimeframe.ContainsKey(target))
            {
                continue;
            }

            barsByTimeframe[target] = _barResampler.Resample(sourceBars, target);
        }
    }

    private static IReadOnlyList<string> ResolveRequiredDerivedTimeframes(
        BacktestRunConfig run,
        IReadOnlyCollection<StrategyDefinition> strategies,
        IEnumerable<string> loadedTimeframes)
    {
        var loaded = new HashSet<string>(loadedTimeframes, StringComparer.OrdinalIgnoreCase);
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

        return required
            .Where(timeframe => !loaded.Contains(timeframe))
            .OrderBy(TimeframeSortKey)
            .ToArray();
    }

    private static int TimeframeSortKey(string timeframe)
    {
        if (timeframe.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
            Int32.TryParse(timeframe[..^1], out var minutes))
        {
            return minutes;
        }

        if (timeframe.EndsWith("h", StringComparison.OrdinalIgnoreCase) &&
            Int32.TryParse(timeframe[..^1], out var hours))
        {
            return hours * 60;
        }

        if (timeframe.EndsWith("d", StringComparison.OrdinalIgnoreCase) &&
            Int32.TryParse(timeframe[..^1], out var days))
        {
            return days * 24 * 60;
        }

        return Int32.MaxValue;
    }

    private static TimeSpan ParseTimeframe(string timeframe)
    {
        if (timeframe.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
            Int32.TryParse(timeframe[..^1], out var minutes))
        {
            return TimeSpan.FromMinutes(minutes);
        }

        if (timeframe.EndsWith("h", StringComparison.OrdinalIgnoreCase) &&
            Int32.TryParse(timeframe[..^1], out var hours))
        {
            return TimeSpan.FromHours(hours);
        }

        if (timeframe.EndsWith("d", StringComparison.OrdinalIgnoreCase) &&
            Int32.TryParse(timeframe[..^1], out var days))
        {
            return TimeSpan.FromDays(days);
        }

        throw new NotSupportedException($"Unsupported timeframe: {timeframe}");
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

    private StrategyCandidateEvaluation CreateStrategyCandidates(
        BacktestRunConfig run,
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        IReadOnlyList<OhlcvBar> executionBars,
        IReadOnlyList<IndicatorSnapshot> executionSnapshots,
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe)
    {
        var candidates = new List<BacktestCandidateTrade>();
        var diagnostics = new StrategyCandidateDiagnostics(strategy.StrategyId, strategy.StrategyName);
        var startIndex = Math.Max(run.Engine.IndicatorWarmupBars, 1);

        for (var i = startIndex; i < snapshots.Count - 1; i++)
        {
            diagnostics.EvaluatedBarCount++;
            var snapshot = snapshots[i];
            var signal = _signalGenerator.CreateTradeSignal(strategy, bars, snapshots, i);
            if (signal is null)
            {
                diagnostics.IncrementRejection("missing_indicator_warmup_or_null");
                continue;
            }

            if (!_sessionClock.ValidateExecutionWindow(snapshot.Timestamp, strategy.Timeframe, strategy.Session))
            {
                diagnostics.IncrementRejection("outside_session_window");
                continue;
            }

            var signalAvailableTimestamp = snapshot.Timestamp.Add(ParseTimeframe(strategy.Timeframe));
            if (!_signalGenerator.PassesConfluenceGate(strategy, signalAvailableTimestamp, snapshotsByTimeframe))
            {
                diagnostics.IncrementRejection("confluence_gate_failed");
                continue;
            }

            var rejection = _strategyEvaluator.GetLongEntryRejection(strategy, signal, snapshot.RelativeVolume!.Value);
            if (rejection is not null)
            {
                diagnostics.IncrementRejection(rejection);
                continue;
            }

            var candidate = CreateCandidate(run, strategy, signal, executionBars, executionSnapshots, _auditor);
            if (candidate is null)
            {
                diagnostics.IncrementRejection("no_next_bar_or_invalid_stop");
                continue;
            }

            candidates.Add(candidate.Value.Candidate);
            i = Math.Max(i, FindFirstSignalIndexAtOrAfter(bars, candidate.Value.Candidate.ExitTimestamp));
        }

        diagnostics.CandidateTradeCount = candidates.Count;
        return new StrategyCandidateEvaluation(candidates, diagnostics);
    }


    private static (BacktestCandidateTrade Candidate, int ExitIndex)? CreateCandidate(
        BacktestRunConfig run,
        StrategyDefinition strategy,
        TradeSignal signal,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        TradingFlow.Engine.Execution.ExecutionAuditor auditor)
    {
        var signalCloseTimestamp = signal.Timestamp.Add(ParseTimeframe(strategy.Timeframe));
        var entryIndex = FindFirstBarIndexAtOrAfter(bars, signalCloseTimestamp);
        if (entryIndex >= bars.Count)
        {
            return null;
        }

        var entryBar = bars[entryIndex];
        var entrySnapshot = snapshots[entryIndex];
        var relativeVolume = entrySnapshot.RelativeVolume ?? 1.0m;
        
        // Approximate trade amount based on portfolio config
        var approximateTradeAmount = run.Portfolio.StartingCapital / run.Portfolio.MaxConcurrentPositions;
        var entryPrice = TradingFlow.Engine.Risk.DynamicSlippageModel.ApplyLongSlippage(entryBar.Open, relativeVolume, strategy.Execution.SlippageBps, approximateTradeAmount);
        
        var stopDistance = strategy.ExitRules.StopAtrMultiple * signal.CurrentAtr;
        if (stopDistance <= 0)
        {
            return null;
        }

        var initialStopLossPrice = entryPrice - stopDistance;
        var currentStopLossPrice = initialStopLossPrice;
        var takeProfitPrice = entryPrice + (stopDistance * strategy.ExitRules.TargetRMultiple);
        var entryTimestamp = entryBar.Timestamp;
        var highestHighSinceEntry = entryPrice;

        auditor.LogEvent(signal.Ticker, strategy.StrategyName, signal.Timestamp, TradingFlow.Engine.Execution.ExecutionState.SignalGenerated, $"LONG signal generated at {signal.CurrentPrice}");
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, entryTimestamp, TradingFlow.Engine.Execution.ExecutionState.OrderFilled, $"Simulated fill at {entryPrice} (Stop: {initialStopLossPrice}, TP: {takeProfitPrice})");

        var technicalEngine = new TradingFlow.Engine.Execution.TechnicalExecutionEngine();

        for (var i = entryIndex; i < bars.Count; i++)
        {
            var bar = bars[i];
            
            var (exitPrice, exitReason) = technicalEngine.EvaluateBarForExit(
                strategy,
                bar,
                snapshots[i],
                entryPrice,
                initialStopLossPrice,
                takeProfitPrice,
                entryTimestamp,
                stopDistance,
                i - entryIndex,
                ref currentStopLossPrice,
                ref highestHighSinceEntry);

            if (exitPrice.HasValue && exitReason is not null)
            {
                // In backtest, for technical exit, we exit on next bar open
                if (exitReason.StartsWith("technical_exit") && i + 1 < bars.Count)
                {
                    var nextBar = bars[i + 1];
                    auditor.LogEvent(signal.Ticker, strategy.StrategyName, nextBar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via {exitReason} at {nextBar.Open}");
                    return (BuildCandidate(strategy, signal, entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, nextBar.Timestamp, nextBar.Open, exitReason, stopDistance), i + 1);
                }

                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via {exitReason} at {exitPrice.Value}");
                return (BuildCandidate(strategy, signal, entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, exitPrice.Value, exitReason, stopDistance), i);
            }
        }

        var finalBar = bars[^1];
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, finalBar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via end_of_data at {finalBar.Close}");
        return (BuildCandidate(strategy, signal, entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, finalBar.Timestamp, finalBar.Close, "end_of_data", stopDistance), bars.Count - 1);
    }

    private static BacktestCandidateTrade BuildCandidate(
        StrategyDefinition strategy,
        TradeSignal signal,
        DateTimeOffset entryTimestamp,
        decimal entryPrice,
        decimal stopLossPrice,
        decimal takeProfitPrice,
        DateTimeOffset exitTimestamp,
        decimal exitPrice,
        string exitReason,
        decimal stopDistance)
    {
        return new BacktestCandidateTrade(
            signal.Ticker,
            strategy.StrategyName,
            entryTimestamp,
            Decimal.Round(entryPrice, 4),
            Decimal.Round(stopLossPrice, 4),
            Decimal.Round(takeProfitPrice, 4),
            exitTimestamp,
            Decimal.Round(exitPrice, 4),
            exitReason,
            Decimal.Round(stopDistance, 4));
    }

    private static IReadOnlyList<StrategyBacktestResult> BuildStrategyResults(
        PortfolioConfig portfolio,
        IReadOnlyCollection<StrategyDefinition> strategies,
        IReadOnlyList<BacktestCandidateTrade> candidates)
    {
        return strategies
            .Select(strategy =>
            {
                var strategyCandidates = candidates
                    .Where(x => x.StrategyName.Equals(strategy.StrategyName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.EntryTimestamp)
                    .ThenBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var trades = BuildPortfolioTrades(portfolio, strategyCandidates);
                var netProfit = trades.Sum(x => x.NetProfit);
                var endingCapital = portfolio.StartingCapital + netProfit;
                var totalReturnPct = portfolio.StartingCapital == 0 ? 0 : (netProfit / portfolio.StartingCapital) * 100m;
                var maxDrawdownPct = CalculateMaxDrawdown(portfolio.StartingCapital, trades);

                return new StrategyBacktestResult(
                    strategy.StrategyId,
                    strategy.StrategyName,
                    strategy.Source,
                    portfolio.StartingCapital,
                    Decimal.Round(endingCapital, 4),
                    Decimal.Round(netProfit, 4),
                    Decimal.Round(totalReturnPct, 4),
                    Decimal.Round(maxDrawdownPct, 4),
                    strategyCandidates.Length,
                    trades.Count,
                    strategyCandidates.Length - trades.Count,
                    trades.Count(x => x.NetProfit > 0),
                    trades.Count(x => x.NetProfit < 0),
                    trades);
            })
            .ToArray();
    }

    private static IReadOnlyList<StrategyDiagnosticReport> BuildDiagnostics(
        IReadOnlyCollection<StrategyDefinition> strategies,
        IReadOnlyCollection<StrategyBacktestResult> strategyResults,
        IReadOnlyCollection<StrategyCandidateDiagnostics> candidateDiagnostics)
    {
        return strategies
            .Select(strategy =>
            {
                var result = strategyResults.Single(x => x.StrategyId.Equals(strategy.StrategyId, StringComparison.OrdinalIgnoreCase));
                var diagnostics = candidateDiagnostics
                    .Where(x => x.StrategyId.Equals(strategy.StrategyId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var rejectionCounts = diagnostics
                    .SelectMany(x => x.RejectionCounts)
                    .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Sum(y => y.Value), StringComparer.OrdinalIgnoreCase);
                var evaluatedBarCount = diagnostics.Sum(x => x.EvaluatedBarCount);
                var exitReasonCounts = result.CompletedTrades
                    .GroupBy(x => x.ExitReason, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
                var wins = result.CompletedTrades.Where(x => x.NetProfit > 0).ToArray();
                var losses = result.CompletedTrades.Where(x => x.NetProfit < 0).ToArray();
                var averageWin = wins.Length == 0 ? 0 : wins.Average(x => x.NetProfit);
                var averageLoss = losses.Length == 0 ? 0 : Math.Abs(losses.Average(x => x.NetProfit));
                var realizedRewardRiskRatio = averageLoss == 0
                    ? (averageWin > 0 ? 999m : 0m)
                    : averageWin / averageLoss;
                var winRatePct = result.AcceptedTradeCount == 0
                    ? 0
                    : ((decimal)result.WinningTradeCount / result.AcceptedTradeCount) * 100m;

                return new StrategyDiagnosticReport(
                    strategy.StrategyId,
                    strategy.StrategyName,
                    evaluatedBarCount,
                    result.CandidateTradeCount,
                    result.AcceptedTradeCount,
                    Decimal.Round(winRatePct, 4),
                    Decimal.Round(averageWin, 4),
                    Decimal.Round(averageLoss, 4),
                    Decimal.Round(realizedRewardRiskRatio, 4),
                    exitReasonCounts,
                    rejectionCounts
                        .OrderByDescending(x => x.Value)
                        .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
                    BuildDiagnosticSuggestions(result, rejectionCounts, exitReasonCounts, realizedRewardRiskRatio, winRatePct));
            })
            .ToArray();
    }

    private static IReadOnlyList<string> BuildDiagnosticSuggestions(
        StrategyBacktestResult result,
        IReadOnlyDictionary<string, int> rejectionCounts,
        IReadOnlyDictionary<string, int> exitReasonCounts,
        decimal realizedRewardRiskRatio,
        decimal winRatePct)
    {
        var suggestions = new List<string>();
        if (result.AcceptedTradeCount == 0)
        {
            if (rejectionCounts.Count == 0)
            {
                suggestions.Add("No evaluated bars were available for this strategy timeframe. Check market_data.download_timeframes, market_data.derive_from, and selected strategy timeframes.");
                return suggestions;
            }

            var top = rejectionCounts.OrderByDescending(x => x.Value).First();
            suggestions.Add($"No trades executed. Top blocker was {top.Key} ({top.Value} bars).");
            if (top.Key.Contains("setup_", StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Add("Review setup_type-specific filters; the setup may be too strict for this ticker universe and window.");
            }

            if (top.Key.Equals("confluence_gate_failed", StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Add("Strategy confluence gate rejected most bars. Consider testing with a looser confluence EMA, different confluence timeframe, or disabling confluence for this strategy version.");
            }

            if (top.Key.Equals("relative_volume_below_minimum", StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Add("Volume filter rejected most bars. Consider lowering min_volume_spike or using a session-relative volume window.");
            }

            return suggestions;
        }

        if (realizedRewardRiskRatio < 1m && winRatePct < 50m)
        {
            suggestions.Add("Average loss is larger than average win with sub-50% win rate. Consider higher target_r_multiple, tighter entry quality, or smaller stop_atr_multiple.");
        }

        if (exitReasonCounts.TryGetValue("stop_loss", out var stopLossCount) &&
            stopLossCount > result.AcceptedTradeCount / 2m)
        {
            suggestions.Add("More than half of trades hit stop loss. Consider widening stop_atr_multiple or adding stronger trend/setup confirmation.");
        }

        if (exitReasonCounts.TryGetValue("max_hold", out var maxHoldCount) &&
            maxHoldCount > result.AcceptedTradeCount * 0.4m)
        {
            suggestions.Add("Many trades are timing out at max_hold. Consider tightening entries, extending max_hold_hours, or adding technical exits.");
        }

        if (result.TotalReturnPct > 0 && result.MaxDrawdownPct <= 2m)
        {
            suggestions.Add("Positive return with controlled drawdown. Candidate for broader ticker/window validation before promotion.");
        }

        if (suggestions.Count == 0)
        {
            suggestions.Add("No obvious single blocker. Review ticker universe, sample length, and walk-forward stability.");
        }

        return suggestions;
    }

    private static IReadOnlyList<BacktestTrade> BuildPortfolioTrades(
        PortfolioConfig portfolio,
        IReadOnlyList<BacktestCandidateTrade> candidates)
    {
        var accepted = new List<BacktestTrade>();

        foreach (var candidate in candidates)
        {
            var closedProfit = accepted
                .Where(x => x.ExitTimestamp <= candidate.EntryTimestamp)
                .Sum(x => x.NetProfit);
            var equity = portfolio.StartingCapital + closedProfit;
            var activePositions = accepted
                .Where(x => x.EntryTimestamp <= candidate.EntryTimestamp && x.ExitTimestamp > candidate.EntryTimestamp)
                .ToArray();

            if (portfolio.PreventOverlappingTickerPositions &&
                activePositions.Any(x => x.Ticker.Equals(candidate.Ticker, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (activePositions.Length >= portfolio.MaxConcurrentPositions)
            {
                continue;
            }

            var riskBudget = equity * (portfolio.RiskPerTradePct / 100m);
            var riskSizedQuantity = (int)Math.Floor(riskBudget / candidate.StopDistance);
            var slotPositionValue = equity / portfolio.MaxConcurrentPositions;
            var configuredPositionValue = equity * (portfolio.MaxPositionValuePct / 100m);
            var reservedPositionValue = activePositions.Sum(x => x.ShareQuantity * x.EntryPrice);
            var availablePositionValue = Math.Max(0, equity - reservedPositionValue);
            var maxPositionValue = Math.Min(Math.Min(slotPositionValue, configuredPositionValue), availablePositionValue);
            var capitalSizedQuantity = (int)Math.Floor(maxPositionValue / candidate.EntryPrice);
            var shareQuantity = Math.Min(riskSizedQuantity, capitalSizedQuantity);
            if (shareQuantity <= 0)
            {
                continue;
            }

            var grossProfit = (candidate.ExitPrice - candidate.EntryPrice) * shareQuantity;
            var fees = portfolio.FixedBuyFee + portfolio.FixedSellFee;
            var netProfit = grossProfit - fees;

            accepted.Add(new BacktestTrade(
                candidate.Ticker,
                candidate.StrategyName,
                candidate.EntryTimestamp,
                candidate.ExitTimestamp,
                shareQuantity,
                candidate.EntryPrice,
                candidate.ExitPrice,
                candidate.StopLossPrice,
                candidate.TakeProfitPrice,
                candidate.ExitReason,
                Decimal.Round(grossProfit, 4),
                Decimal.Round(fees, 4),
                Decimal.Round(netProfit, 4)));
        }

        return accepted;
    }

    private static decimal CalculateMaxDrawdown(decimal startingCapital, IReadOnlyList<BacktestTrade> completedTrades)
    {
        var equity = startingCapital;
        var highWaterMark = startingCapital;
        var maxDrawdown = 0m;

        foreach (var trade in completedTrades.OrderBy(x => x.ExitTimestamp))
        {
            equity += trade.NetProfit;
            highWaterMark = Math.Max(highWaterMark, equity);
            if (highWaterMark > 0)
            {
                maxDrawdown = Math.Max(maxDrawdown, ((highWaterMark - equity) / highWaterMark) * 100m);
            }
        }

        return maxDrawdown;
    }

    private static ICatalystProvider? CreateNewsProvider(BacktestRunConfig run)
    {
        if (!run.News.Enabled) return null;
        
        return run.News.ProviderName.ToLowerInvariant() switch
        {
            "alpaca" => new TradingFlow.Alpaca.AlpacaNewsProvider(
                new HttpClient(), 
                TradingFlow.Alpaca.AlpacaOptions.CreateDefault() with
                {
                    KeyId = Environment.GetEnvironmentVariable("ALPACA_KEY_ID") ?? "",
                    SecretKey = Environment.GetEnvironmentVariable("ALPACA_SECRET_KEY") ?? "",
                    MarketDataFeed = run.Providers.Alpaca.DataFeed
                }),
            "finviz" => new TradingFlow.Finviz.FinvizNewsProvider(
                new TradingFlow.Finviz.FinvizClient(
                    new HttpClient(),
                    TradingFlow.Finviz.FinvizOptions.CreateDefault() with { AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? "" }
                )),
            "none" => null,
            _ => throw new NotSupportedException($"Unsupported news provider: {run.News.ProviderName}")
        };
    }

    private static IMarketDataProvider CreateProvider(BacktestRunConfig run)
    {
        var etoroOptions = TradingFlow.Etoro.Configuration.EtoroOptions.CreateDefault(TradingFlow.Etoro.Configuration.EtoroEnvironment.Demo) with
        {
            Demo = new TradingFlow.Etoro.Configuration.EtoroCredentialProfile(
                "ETORO_DEMO_API_KEY",
                "ETORO_DEMO_USER_KEY",
                false)
        };
        // Setup Etoro API dependencies
        var etoroCreds = new TradingFlow.Etoro.Authentication.EtoroCredentialsProvider(etoroOptions);
        var etoroRateLimiter = new TradingFlow.Etoro.Http.EtoroRateLimiter(etoroOptions.RateLimits);

        return run.Provider.ToLowerInvariant() switch
        {
            "csv" => new CsvMarketDataProvider(run.NormalizedRoot),
            "yahoo" => new YahooFinanceProvider(
                YahooFinanceProvider.CreateBrowserLikeClient(run.Providers.Yahoo),
                run.Providers.Yahoo,
                run.RawRoot,
                run.NormalizedRoot,
                run.CachePolicy),
            "alpaca" => new TradingFlow.Alpaca.AlpacaMarketDataProvider(
                new HttpClient(),
                TradingFlow.Alpaca.AlpacaOptions.CreateDefault() with
                {
                    KeyId = Environment.GetEnvironmentVariable("ALPACA_KEY_ID") ?? "",
                    SecretKey = Environment.GetEnvironmentVariable("ALPACA_SECRET_KEY") ?? ""
                }),
            "finviz" => new TradingFlow.Finviz.FinvizMarketDataProvider(
                new TradingFlow.Finviz.FinvizClient(
                    new HttpClient(),
                    TradingFlow.Finviz.FinvizOptions.CreateDefault() with { AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? "" }
                )),
            "etoro" => new TradingFlow.Etoro.MarketData.EtoroMarketDataProvider(
                new TradingFlow.Etoro.Http.EtoroApiClient(
                    new HttpClient(),
                    etoroOptions,
                    etoroCreds,
                    etoroRateLimiter),
                new TradingFlow.Etoro.MarketData.EtoroInstrumentResolver(
                    new TradingFlow.Etoro.Http.EtoroApiClient(
                        new HttpClient(),
                        etoroOptions,
                        etoroCreds,
                        etoroRateLimiter)
                )
            ),
            _ => throw new NotSupportedException($"Unsupported market data provider: {run.Provider}")
        };
    }

    private static string GetResultPath(BacktestRunConfig run)
    {
        var owner = Environment.GetEnvironmentVariable("TRADINGFLOW_RESULT_OWNER");
        var safeOwner = String.IsNullOrWhiteSpace(owner) ? "shared" : SanitizePathSegment(owner);
        return Path.Combine(run.ResultsRoot, safeOwner, "portfolio", $"{run.RunName}.json");
    }

    private static async Task<BacktestResult> WriteResultAsync(string resultPath, BacktestResult result, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        var exclusivePath = ReserveResultPath(resultPath);
        var persistedResult = result with { ResultPath = exclusivePath };
        var temporaryPath = $"{exclusivePath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Open(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, persistedResult, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
        }

        File.Move(temporaryPath, exclusivePath, overwrite: false);
        return persistedResult;
    }

    private static string ReserveResultPath(string resultPath)
    {
        if (!File.Exists(resultPath))
        {
            return resultPath;
        }

        var directory = Path.GetDirectoryName(resultPath)!;
        var fileName = Path.GetFileNameWithoutExtension(resultPath);
        var extension = Path.GetExtension(resultPath);
        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{fileName}-{index:0000}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not reserve a unique result path for {resultPath}.");
    }

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
        CancellationToken cancellationToken)
    {
        var bars = new List<OhlcvBar>();
        try
        {
            await foreach (var bar in provider.GetBarsAsync([ticker], intervals, start, end, cancellationToken))
            {
                bars.Add(bar);
            }
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
        CancellationToken cancellationToken)
    {
        if (!run.Validation.Benchmark.Enabled || String.IsNullOrWhiteSpace(run.Validation.Benchmark.Ticker))
        {
            return Array.Empty<OhlcvBar>();
        }

        return await LoadTickerBarsAsync(
            provider,
            run.Validation.Benchmark.Ticker,
            [run.Intervals[0]],
            start,
            end,
            cancellationToken);
    }
}

internal sealed record TickerBacktestBatch(
    TickerBacktestResult Result,
    IReadOnlyList<BacktestCandidateTrade> CandidateTrades,
    IReadOnlyList<StrategyCandidateDiagnostics> Diagnostics,
    IReadOnlyList<OhlcvBar> ProcessedBars);

internal sealed record StrategyCandidateEvaluation(
    IReadOnlyList<BacktestCandidateTrade> Candidates,
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

    public static StrategyCandidateDiagnostics MissingTimeframe(StrategyDefinition strategy, string reason)
    {
        var diagnostics = new StrategyCandidateDiagnostics(strategy.StrategyId, strategy.StrategyName);
        diagnostics.IncrementRejection(reason);
        return diagnostics;
    }

    public void IncrementRejection(string reason)
    {
        RejectionCounts[reason] = RejectionCounts.TryGetValue(reason, out var count) ? count + 1 : 1;
    }
}

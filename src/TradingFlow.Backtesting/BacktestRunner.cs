using System.Text.Json;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Data.Csv;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Sessions;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Strategies;
using TradingFlow.Data.Catalysts;

namespace TradingFlow.Backtesting;

public sealed record PreparedBacktestMarket(
    CandlePipelineResult MarketState,
    IReadOnlyList<OhlcvBar> BenchmarkBars,
    DateTimeOffset DataStart,
    DateTimeOffset EvaluationStart,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd);

public sealed class BacktestRunner(SimpleYamlReader yamlReader, IArtifactWriter? artifactWriter = null)
{
    private readonly IArtifactWriter _artifactWriter = artifactWriter ?? AtomicFileArtifactWriter.Instance;
    private readonly BasicStrategyEvaluator _strategyEvaluator = new();
    private readonly StrategySessionClock _sessionClock = new();
    private readonly CandlePipelineEngine _candlePipeline = new();
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
        var preparedMarket = await PrepareMarketAsync(run, strategies, cancellationToken, progress);
        return await RunPreparedAsync(run, strategies, startedAt, resultPath, preparedMarket, cancellationToken, progress);
    }

    public async Task<PreparedBacktestMarket> PrepareMarketAsync(
        BacktestRunConfig run,
        IReadOnlyCollection<StrategyDefinition> strategies,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null)
    {
        ValidateRun(run, strategies);

        var provider = CreateProvider(run);
        var (dataStart, evaluationStart, windowEnd) = ResolveWindow(run.TimeWindow);

        var completedTickerCount = 0;
        var totalTickerCount = run.Tickers.Count;
        var requiredTimeframes = ResolveRequiredTimeframes(strategies);
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
                run.Execution.ExtendedHours,
                ResolveExchangeTimezone(strategies)),
            provider,
            cancellationToken,
            new Progress<string>(message => progress?.Report(new BacktestProgress(
                "market_pipeline",
                message,
                null,
                Volatile.Read(ref completedTickerCount),
                totalTickerCount))));

        var benchmarkBars = await LoadBenchmarkBarsAsync(run, provider, dataStart, windowEnd, cancellationToken);
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
            windowEnd);
    }

    public async Task<BacktestResult> RunPreparedAsync(
        BacktestRunConfig run,
        StrategyDefinition[] strategies,
        DateTimeOffset startedAt,
        string resultPath,
        PreparedBacktestMarket preparedMarket,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null)
    {
        ValidateRun(run, strategies);
        var catalystStreamer = new CatalystStreamer(CreateNewsProvider(run));

        var completedTickerCount = 0;
        var totalTickerCount = run.Tickers.Count;
        progress?.Report(new BacktestProgress(
            "running_tickers",
            $"Running {totalTickerCount} ticker pipeline(s) with {strategies.Length} strategy config(s).",
            null,
            completedTickerCount,
            totalTickerCount));

        var pipeline = new TplBacktestPipeline();
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
                var batch = await ProcessPreparedTickerAsync(
                    run,
                    preparedMarket.MarketState,
                    catalystStreamer,
                    strategies,
                    ticker,
                    preparedMarket.DataStart,
                    preparedMarket.WindowStart,
                    preparedMarket.WindowEnd,
                    token);
                var completed = Interlocked.Increment(ref completedTickerCount);
                progress?.Report(new BacktestProgress(
                    "running_tickers",
                    $"Finished {ticker}.",
                    ticker,
                    completed,
                    totalTickerCount));
                return batch;
            },
            cancellationToken,
            (ticker, exception) => new TickerBacktestBatch(
                TickerBacktestResult.Failed(ticker, exception),
                Array.Empty<BacktestCandidateTrade>(),
                Array.Empty<StrategyCandidateDiagnostics>(),
                Array.Empty<OhlcvBar>()));

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
        var strategyResults = BuildStrategyResults(run.Portfolio, strategies, candidates, allBars);
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
                bestStrategy.AverageDailyReturnPct,
                bestStrategy.MaxDrawdownPct,
                bestStrategy.AcceptedTradeCount,
                bestStrategy.RejectedTradeCount);
        var validation = _validator.Validate(run, allBars, preparedMarket.BenchmarkBars, strategyResults);

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
            bestStrategy?.AverageDailyReturnPct ?? 0,
            bestStrategy?.TradingDayCount ?? 0,
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

        result = result with
        {
            ResultPath = await WriteResultAsync(resultPath, result, run.Artifacts, cancellationToken)
        };
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

    private async Task<TickerBacktestBatch> ProcessPreparedTickerAsync(
        BacktestRunConfig run,
        CandlePipelineResult marketState,
        CatalystStreamer catalystStreamer,
        IReadOnlyCollection<StrategyDefinition> strategies,
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
                throw new InvalidOperationException(failure);
            }

            var barsByTimeframe = tickerState.BarsByTimeframe;
            var snapshotsByTimeframe = tickerState.SnapshotsByTimeframe
                .ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            var processedBars = tickerState.AllBars
                .Where(bar => bar.Timestamp >= windowStart && bar.Timestamp <= windowEnd)
                .ToArray();

            var catalysts = (await catalystStreamer.LoadTickerCatalystsAsync(ticker, dataStart, windowEnd, cancellationToken))
                .OrderBy(catalyst => catalyst.Timestamp)
                .ToArray();
            foreach (var timeframe in snapshotsByTimeframe.Keys)
            {
                AttachCatalystsToSnapshots(snapshotsByTimeframe[timeframe], catalysts, cancellationToken);
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
                    readonlySnapshots,
                    windowStart,
                    windowEnd,
                    cancellationToken);
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

    private static void AttachCatalystsToSnapshots(
        IList<IndicatorSnapshot> snapshots,
        IReadOnlyList<CatalystEvent> catalysts,
        CancellationToken cancellationToken)
    {
        if (snapshots.Count == 0 || catalysts.Count == 0)
        {
            return;
        }

        var catalystIndex = 0;
        CatalystEvent? latestCatalyst = null;
        for (var snapshotIndex = 0; snapshotIndex < snapshots.Count; snapshotIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = snapshots[snapshotIndex];
            while (catalystIndex < catalysts.Count &&
                   catalysts[catalystIndex].Timestamp <= snapshot.Timestamp)
            {
                latestCatalyst = catalysts[catalystIndex];
                catalystIndex++;
            }

            if (latestCatalyst is not null &&
                latestCatalyst.Timestamp >= snapshot.Timestamp.AddDays(-3))
            {
                snapshots[snapshotIndex] = snapshot with { Catalyst = latestCatalyst };
            }
        }
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

    private static bool IsDailyOrHigher(string timeframe)
    {
        return timeframe.EndsWith("d", StringComparison.OrdinalIgnoreCase);
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
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe,
        DateTimeOffset evaluationStart,
        DateTimeOffset evaluationEnd,
        CancellationToken cancellationToken)
    {
        var candidates = new List<BacktestCandidateTrade>();
        var diagnostics = new StrategyCandidateDiagnostics(strategy.StrategyId, strategy.StrategyName);
        var entriesByExchangeDate = new Dictionary<DateOnly, int>();
        var startIndex = Math.Max(run.Engine.IndicatorWarmupBars, 1);

        for (var i = startIndex; i < snapshots.Count - 1; i++)
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
            if (snapshot.RelativeVolume is null)
            {
                diagnostics.IncrementRejection("missing_indicator_warmup_or_null");
                continue;
            }

            if (!_sessionClock.ValidateExecutionWindow(snapshot.Timestamp, strategy.Timeframe, strategy.Session))
            {
                diagnostics.IncrementRejection("outside_session_window");
                continue;
            }

            if (strategy.EntryRules.MaxEntriesPerTickerPerDay > 0)
            {
                var signalExchangeDate = ToExchangeDate(signalAvailableTimestamp, strategy.Session.ExchangeTimezone);
                if (entriesByExchangeDate.TryGetValue(signalExchangeDate, out var entryCount) &&
                    entryCount >= strategy.EntryRules.MaxEntriesPerTickerPerDay)
                {
                    diagnostics.IncrementRejection(
                        $"max_entries_per_ticker_day_reached (Actual: {entryCount}, Allowed: {strategy.EntryRules.MaxEntriesPerTickerPerDay})");
                    continue;
                }
            }

            var confluenceRejection = _signalGenerator.GetConfluenceRejection(strategy, signalAvailableTimestamp, snapshotsByTimeframe);
            if (confluenceRejection is not null)
            {
                diagnostics.IncrementRejection(confluenceRejection);
                continue;
            }

            if (snapshot.RelativeVolume.Value < strategy.EntryRules.MinVolumeSpike)
            {
                diagnostics.IncrementRejection(
                    $"relative_volume_below_minimum (Actual: {snapshot.RelativeVolume.Value:F2}, Required: {strategy.EntryRules.MinVolumeSpike:F2})");
                continue;
            }

            var signal = _signalGenerator.CreateTradeSignal(strategy, bars, snapshots, i);
            if (signal is null)
            {
                diagnostics.IncrementRejection("missing_indicator_warmup_or_null");
                continue;
            }

            var longRejection = AllowsLong(strategy)
                ? _strategyEvaluator.GetLongEntryRejection(strategy, signal, snapshot.RelativeVolume.Value)
                : "direction_not_long";
            var shortRejection = AllowsShort(strategy)
                ? _strategyEvaluator.GetShortEntryRejection(strategy, signal, snapshot.RelativeVolume.Value)
                : "direction_not_short";
            if (longRejection is not null && shortRejection is not null)
            {
                diagnostics.IncrementRejection(ChoosePrimaryRejection(longRejection, shortRejection));
                continue;
            }

            var entryPlan = GetExecutionEntryPlan(strategy, signal, executionBars);
            if (entryPlan.Rejection is not null)
            {
                diagnostics.IncrementRejection(entryPlan.Rejection);
                continue;
            }

            var direction = longRejection is null ? "long" : "short";
            var candidate = CreateCandidate(run, strategy, signal, direction, executionBars, executionSnapshots, _auditor, cancellationToken, entryPlan.EntryIndex);
            if (candidate is null)
            {
                diagnostics.IncrementRejection("no_next_bar_or_invalid_stop");
                continue;
            }

            candidates.Add(candidate.Value.Candidate);
            if (strategy.EntryRules.MaxEntriesPerTickerPerDay > 0)
            {
                var entryExchangeDate = ToExchangeDate(candidate.Value.Candidate.EntryTimestamp, strategy.Session.ExchangeTimezone);
                entriesByExchangeDate[entryExchangeDate] = entriesByExchangeDate.TryGetValue(entryExchangeDate, out var count)
                    ? count + 1
                    : 1;
            }

            i = Math.Max(i, FindFirstSignalIndexAtOrAfter(bars, candidate.Value.Candidate.ExitTimestamp));
        }

        diagnostics.CandidateTradeCount = candidates.Count;
        return new StrategyCandidateEvaluation(candidates, diagnostics);
    }

    private (string? Rejection, int EntryIndex) GetExecutionEntryPlan(
        StrategyDefinition strategy,
        TradeSignal signal,
        IReadOnlyList<OhlcvBar> executionBars)
    {
        var signalCloseTimestamp = signal.Timestamp.Add(ParseTimeframe(strategy.Timeframe));
        var confirmationIndex = FindFirstBarIndexAtOrAfter(executionBars, signalCloseTimestamp);
        if (confirmationIndex >= executionBars.Count)
        {
            return ("no_next_bar_or_invalid_stop", confirmationIndex);
        }

        var confirmationBar = executionBars[confirmationIndex];
        if (!_sessionClock.ValidateExecutionWindow(confirmationBar.Timestamp, strategy.Execution.Timeframe, strategy.Session))
        {
            return ("entry_outside_session_window", confirmationIndex);
        }

        var entryIndex = strategy.EntryRules.EnableEntryBarConfirmation
            ? confirmationIndex + 1
            : confirmationIndex;
        if (entryIndex >= executionBars.Count)
        {
            return ("no_next_bar_after_entry_confirmation", entryIndex);
        }

        var entryBar = executionBars[entryIndex];
        if (!_sessionClock.ValidateExecutionWindow(entryBar.Timestamp, strategy.Execution.Timeframe, strategy.Session))
        {
            return ("entry_outside_session_window", entryIndex);
        }

        var confirmationCloseLocation = ComputeCloseLocationValue(confirmationBar);
        if (strategy.EntryRules.EnableEntryBarConfirmation)
        {
            if (strategy.EntryRules.RejectEntryBarCloseLocationBelowMinimum &&
                (confirmationCloseLocation is null || confirmationCloseLocation.Value < strategy.EntryRules.MinEntryBarCloseLocationValue))
            {
                return ($"entry_bar_close_location_below_minimum (Actual: {confirmationCloseLocation?.ToString("F2") ?? "n/a"}, Required: {strategy.EntryRules.MinEntryBarCloseLocationValue:F2})", confirmationIndex);
            }

            if (strategy.EntryRules.RejectEntryBarBreaksSignalMidpoint &&
                signal.SignalBarMidpoint is { } signalMidpoint &&
                confirmationBar.Low < signalMidpoint)
            {
                return ($"entry_bar_broke_signal_midpoint (EntryLow: {confirmationBar.Low:F2}, SignalMidpoint: {signalMidpoint:F2})", confirmationIndex);
            }
        }

        if (strategy.EntryRules.MaxVwapExtensionPctForDirectEntry is { } maxVwapExtension &&
            signal.VwapExtensionAtr is not null &&
            signal.VwapExtensionAtr.Value > 0m &&
            signal.IsAboveVwap &&
            signal.CurrentPrice > 0m)
        {
            var vwapExtensionPct = signal.VwapExtensionAtr.Value * signal.CurrentAtr / signal.CurrentPrice * 100m;
            if (vwapExtensionPct > maxVwapExtension &&
                strategy.EntryRules.ExtendedVwapMinEntryBarCloseLocationValue is { } requiredExtendedVwapClv &&
                (confirmationCloseLocation is null || confirmationCloseLocation.Value < requiredExtendedVwapClv))
            {
                return ($"extended_vwap_entry_not_confirmed (VwapExtensionPct: {vwapExtensionPct:F2}, EntryCloseLocation: {confirmationCloseLocation?.ToString("F2") ?? "n/a"}, RequiredCloseLocation: {requiredExtendedVwapClv:F2})", confirmationIndex);
            }
        }

        if (strategy.EntryRules.MaxBollingerPositionForDirectEntry is { } maxBollingerPosition &&
            signal.BollingerPosition is { } bollingerPosition &&
            bollingerPosition > maxBollingerPosition &&
            strategy.EntryRules.ExtendedBollingerMinEntryBarCloseLocationValue is { } requiredExtendedBollingerClv &&
            (confirmationCloseLocation is null || confirmationCloseLocation.Value < requiredExtendedBollingerClv))
        {
            return ($"extended_bollinger_entry_not_confirmed (BollingerPosition: {bollingerPosition:F2}, EntryCloseLocation: {confirmationCloseLocation?.ToString("F2") ?? "n/a"}, RequiredCloseLocation: {requiredExtendedBollingerClv:F2})", confirmationIndex);
        }

        return (null, entryIndex);
    }

    private static decimal? ComputeCloseLocationValue(OhlcvBar bar)
    {
        var range = bar.High - bar.Low;
        return range <= 0m
            ? null
            : (bar.Close - bar.Low) / range;
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

    private (BacktestCandidateTrade Candidate, int ExitIndex)? CreateCandidate(
        BacktestRunConfig run,
        StrategyDefinition strategy,
        TradeSignal signal,
        string direction,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        TradingFlow.Engine.Execution.ExecutionAuditor auditor,
        CancellationToken cancellationToken,
        int? plannedEntryIndex = null)
    {
        var signalCloseTimestamp = signal.Timestamp.Add(ParseTimeframe(strategy.Timeframe));
        var entryIndex = plannedEntryIndex ?? FindFirstBarIndexAtOrAfter(bars, signalCloseTimestamp);
        if (entryIndex >= bars.Count)
        {
            return null;
        }

        var entryBar = bars[entryIndex];
        var entrySnapshot = snapshots[entryIndex];
        var relativeVolume = entrySnapshot.RelativeVolume ?? 1.0m;
        if (direction.Equals("short", StringComparison.OrdinalIgnoreCase))
        {
            return CreateShortCandidate(run, strategy, signal, entryIndex, bars, snapshots, auditor, relativeVolume, cancellationToken);
        }
        
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
            cancellationToken.ThrowIfCancellationRequested();
            var bar = bars[i];
            var exitPriceLogTrend = TradingFlow.Engine.Execution.TechnicalExecutionEngine.ComputeLogTrend(
                bars,
                i,
                strategy.ExitRules.ExitLogPriceLookbackBars,
                x => x.Close,
                addOne: false);
            var exitVolumeLogTrend = TradingFlow.Engine.Execution.TechnicalExecutionEngine.ComputeLogTrend(
                bars,
                i,
                strategy.ExitRules.ExitLogVolumeLookbackBars,
                x => x.Volume,
                addOne: true);
            if (IsIntradayFlatStrategy(strategy) &&
                _sessionClock.ShouldFlattenBeforeSessionClose(bar.Timestamp, strategy.Execution.Timeframe, strategy.Session))
            {
                var eodRelativeVolume = snapshots[i].RelativeVolume ?? relativeVolume;
                var eodExitPrice = ApplyLongExitSlippage(bar.Open, eodRelativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via end_of_day_exit at {eodExitPrice}");
                return (BuildCandidate(strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, eodExitPrice, "end_of_day_exit", stopDistance), i);
            }

            var barsHeld = i - entryIndex;
            currentStopLossPrice = technicalEngine.CalculateEffectiveStopLoss(
                strategy,
                snapshots[i],
                entryPrice,
                stopDistance,
                initialStopLossPrice,
                currentStopLossPrice,
                highestHighSinceEntry);
            if (bar.Low <= currentStopLossPrice)
            {
                var stopExitReason = currentStopLossPrice > initialStopLossPrice ? "trailing_stop" : "stop_loss";
                var slippedStopPrice = ApplyLongExitSlippage(currentStopLossPrice, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via {stopExitReason} at {slippedStopPrice}");
                return (BuildCandidate(strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, slippedStopPrice, stopExitReason, stopDistance), i);
            }

            if (bar.High >= takeProfitPrice)
            {
                var slippedTakeProfit = ApplyLongExitSlippage(takeProfitPrice, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via take_profit at {slippedTakeProfit}");
                return (BuildCandidate(strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, slippedTakeProfit, "take_profit", stopDistance), i);
            }

            if (ShouldExitLongOnConfirmedVwapFailure(strategy, snapshots, i, entryIndex, entryPrice, stopDistance, Math.Max(highestHighSinceEntry, bar.High), barsHeld))
            {
                var exitBar = i + 1 < bars.Count ? bars[i + 1] : bar;
                var exitSnapshot = i + 1 < snapshots.Count ? snapshots[i + 1] : snapshots[i];
                var exitBasis = i + 1 < bars.Count ? exitBar.Open : exitBar.Close;
                var confirmedExitPrice = ApplyLongExitSlippage(exitBasis, exitSnapshot.RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, exitBar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via confirmed_vwap_failure at {confirmedExitPrice}");
                return (BuildCandidate(strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, exitBar.Timestamp, confirmedExitPrice, "confirmed_vwap_failure", stopDistance), Math.Min(i + 1, bars.Count - 1));
            }

            var (exitPrice, exitReason) = technicalEngine.EvaluateBarForExit(
                strategy,
                bar,
                snapshots[i],
                entryPrice,
                initialStopLossPrice,
                takeProfitPrice,
                entryTimestamp,
                stopDistance,
                barsHeld,
                ref currentStopLossPrice,
                ref highestHighSinceEntry,
                exitPriceLogTrend?.Slope,
                exitVolumeLogTrend?.Slope,
                i > 0 ? snapshots[i - 1] : null);

            if (exitPrice.HasValue && exitReason is not null)
            {
                // In backtest, for technical exit, we exit on next bar open
                if (exitReason.StartsWith("technical_exit") && i + 1 < bars.Count)
                {
                    var nextBar = bars[i + 1];
                    var nextRelativeVolume = snapshots[i + 1].RelativeVolume ?? relativeVolume;
                    var nextExitPrice = ApplyLongExitSlippage(nextBar.Open, nextRelativeVolume, strategy, approximateTradeAmount);
                    auditor.LogEvent(signal.Ticker, strategy.StrategyName, nextBar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via {exitReason} at {nextExitPrice}");
                    return (BuildCandidate(strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, nextBar.Timestamp, nextExitPrice, exitReason, stopDistance), i + 1);
                }

                var slippedExitPrice = ApplyLongExitSlippage(exitPrice.Value, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via {exitReason} at {slippedExitPrice}");
                return (BuildCandidate(strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, slippedExitPrice, exitReason, stopDistance), i);
            }
        }

        var finalBar = bars[^1];
        var finalRelativeVolume = snapshots[^1].RelativeVolume ?? relativeVolume;
        var finalExitPrice = ApplyLongExitSlippage(finalBar.Close, finalRelativeVolume, strategy, approximateTradeAmount);
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, finalBar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Exit via end_of_data at {finalExitPrice}");
        return (BuildCandidate(strategy, signal, "long", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, finalBar.Timestamp, finalExitPrice, "end_of_data", stopDistance), bars.Count - 1);
    }

    private (BacktestCandidateTrade Candidate, int ExitIndex)? CreateShortCandidate(
        BacktestRunConfig run,
        StrategyDefinition strategy,
        TradeSignal signal,
        int entryIndex,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        TradingFlow.Engine.Execution.ExecutionAuditor auditor,
        decimal relativeVolume,
        CancellationToken cancellationToken)
    {
        var entryBar = bars[entryIndex];
        var approximateTradeAmount = run.Portfolio.StartingCapital / run.Portfolio.MaxConcurrentPositions;
        var entryPrice = ApplyShortEntrySlippage(entryBar.Open, relativeVolume, strategy, approximateTradeAmount);
        var stopDistance = strategy.ExitRules.StopAtrMultiple * signal.CurrentAtr;
        if (stopDistance <= 0)
        {
            return null;
        }

        var initialStopLossPrice = entryPrice + stopDistance;
        var currentStopLossPrice = initialStopLossPrice;
        var takeProfitPrice = entryPrice - (stopDistance * strategy.ExitRules.TargetRMultiple);
        if (takeProfitPrice <= 0)
        {
            return null;
        }

        var entryTimestamp = entryBar.Timestamp;
        var lowestLowSinceEntry = entryPrice;
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, signal.Timestamp, TradingFlow.Engine.Execution.ExecutionState.SignalGenerated, $"SHORT signal generated at {signal.CurrentPrice}");
        auditor.LogEvent(signal.Ticker, strategy.StrategyName, entryTimestamp, TradingFlow.Engine.Execution.ExecutionState.OrderFilled, $"Simulated short fill at {entryPrice} (Stop: {initialStopLossPrice}, TP: {takeProfitPrice})");

        for (var i = entryIndex; i < bars.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bar = bars[i];
            if (IsIntradayFlatStrategy(strategy) &&
                _sessionClock.ShouldFlattenBeforeSessionClose(bar.Timestamp, strategy.Execution.Timeframe, strategy.Session))
            {
                var eodRelativeVolume = snapshots[i].RelativeVolume ?? relativeVolume;
                var eodExitPrice = ApplyShortExitSlippage(bar.Open, eodRelativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Short exit via end_of_day_exit at {eodExitPrice}");
                return (BuildCandidate(strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, eodExitPrice, "end_of_day_exit", stopDistance), i);
            }

            var barsHeld = i - entryIndex;
            if (bar.High >= currentStopLossPrice)
            {
                var stopExitReason = currentStopLossPrice < initialStopLossPrice ? "trailing_stop" : "stop_loss";
                var slippedStopPrice = ApplyShortExitSlippage(currentStopLossPrice, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Short exit via {stopExitReason} at {slippedStopPrice}");
                return (BuildCandidate(strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, slippedStopPrice, stopExitReason, stopDistance), i);
            }

            if (bar.Low <= takeProfitPrice)
            {
                var slippedTakeProfit = ApplyShortExitSlippage(takeProfitPrice, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Short exit via take_profit at {slippedTakeProfit}");
                return (BuildCandidate(strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, slippedTakeProfit, "take_profit", stopDistance), i);
            }

            if (ShouldExitShortOnConfirmedVwapReclaim(strategy, snapshots, i, entryIndex, entryPrice, stopDistance, Math.Min(lowestLowSinceEntry, bar.Low), barsHeld))
            {
                var exitBar = i + 1 < bars.Count ? bars[i + 1] : bar;
                var exitSnapshot = i + 1 < snapshots.Count ? snapshots[i + 1] : snapshots[i];
                var exitBasis = i + 1 < bars.Count ? exitBar.Open : exitBar.Close;
                var confirmedExitPrice = ApplyShortExitSlippage(exitBasis, exitSnapshot.RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, exitBar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Short exit via confirmed_vwap_reclaim at {confirmedExitPrice}");
                return (BuildCandidate(strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, exitBar.Timestamp, confirmedExitPrice, "confirmed_vwap_reclaim", stopDistance), Math.Min(i + 1, bars.Count - 1));
            }

            var (exitPrice, exitReason) = EvaluateShortBarForExit(
                strategy,
                bar,
                snapshots[i],
                entryPrice,
                initialStopLossPrice,
                takeProfitPrice,
                entryTimestamp,
                stopDistance,
                barsHeld,
                ref currentStopLossPrice,
                ref lowestLowSinceEntry,
                i > 0 ? snapshots[i - 1] : null);

            if (exitPrice.HasValue && exitReason is not null)
            {
                var slippedExitPrice = ApplyShortExitSlippage(exitPrice.Value, snapshots[i].RelativeVolume ?? relativeVolume, strategy, approximateTradeAmount);
                auditor.LogEvent(signal.Ticker, strategy.StrategyName, bar.Timestamp, TradingFlow.Engine.Execution.ExecutionState.PositionClosed, $"Short exit via {exitReason} at {slippedExitPrice}");
                return (BuildCandidate(strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, bar.Timestamp, slippedExitPrice, exitReason, stopDistance), i);
            }
        }

        var finalBar = bars[^1];
        var finalRelativeVolume = snapshots[^1].RelativeVolume ?? relativeVolume;
        var finalExitPrice = ApplyShortExitSlippage(finalBar.Close, finalRelativeVolume, strategy, approximateTradeAmount);
        return (BuildCandidate(strategy, signal, "short", entryTimestamp, entryPrice, initialStopLossPrice, takeProfitPrice, finalBar.Timestamp, finalExitPrice, "end_of_data", stopDistance), bars.Count - 1);
    }

    private static (decimal? ExitPrice, string? ExitReason) EvaluateShortBarForExit(
        StrategyDefinition strategy,
        OhlcvBar bar,
        IndicatorSnapshot snapshot,
        decimal entryPrice,
        decimal initialStopLossPrice,
        decimal takeProfitPrice,
        DateTimeOffset entryTimestamp,
        decimal stopDistance,
        int barsHeld,
        ref decimal currentStopLossPrice,
        ref decimal lowestLowSinceEntry,
        IndicatorSnapshot? previousSnapshot = null)
    {
        if (strategy.ExitRules.EnableAtrTrailingStop &&
            snapshot.Atr is { } atr &&
            atr > 0)
        {
            var profitR = (entryPrice - lowestLowSinceEntry) / stopDistance;
            if (profitR >= strategy.ExitRules.TrailingActivationR)
            {
                var trailingStop = lowestLowSinceEntry + (strategy.ExitRules.TrailingStopAtrMultiple * atr);
                currentStopLossPrice = Math.Min(initialStopLossPrice, Math.Min(currentStopLossPrice, trailingStop));
            }
        }

        if (bar.High >= currentStopLossPrice)
        {
            var exitReason = currentStopLossPrice < initialStopLossPrice ? "trailing_stop" : "stop_loss";
            return (currentStopLossPrice, exitReason);
        }

        if (bar.Low <= takeProfitPrice)
        {
            return (takeProfitPrice, "take_profit");
        }

        if (barsHeld >= strategy.ExitRules.MinHoldBarsBeforeTechnicalExit)
        {
            if (strategy.ExitRules.ExitOnCloseBelowVwap &&
                snapshot.Vwap is { } vwap &&
                snapshot.CurrentPrice > vwap)
            {
                return (bar.Close, "technical_exit_above_vwap");
            }

            if (strategy.ExitRules.ExitOnMacdHistogramNegative &&
                snapshot.MacdHistogram is { } histogram &&
                histogram > 0)
            {
                return (bar.Close, "technical_exit_macd_histogram_positive");
            }

            if (strategy.ExitRules.ExitShortOnSma10CrossAboveSma20 &&
                TechnicalExecutionEngine.IsSma10CrossedAboveSma20(snapshot, previousSnapshot))
            {
                return (bar.Close, "technical_exit_sma10_cross_above_sma20");
            }
        }

        var maxExitTimestamp = entryTimestamp.AddHours((double)strategy.ExitRules.MaxHoldHours);
        if (bar.Timestamp >= maxExitTimestamp)
        {
            return (bar.Close, "max_hold");
        }

        lowestLowSinceEntry = Math.Min(lowestLowSinceEntry, bar.Low);
        return (null, null);
    }

    private static bool ShouldExitLongOnConfirmedVwapFailure(
        StrategyDefinition strategy,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index,
        int entryIndex,
        decimal entryPrice,
        decimal stopDistance,
        decimal highestHighSinceEntry,
        int barsHeld)
    {
        if (!strategy.ExitRules.EnableConfirmedVwapExit ||
            barsHeld < strategy.ExitRules.MinHoldBarsBeforeTechnicalExit ||
            stopDistance <= 0)
        {
            return false;
        }

        if (strategy.ExitRules.DisableConfirmedVwapExitAfterR is { } disableAfterR &&
            ((highestHighSinceEntry - entryPrice) / stopDistance) >= disableAfterR)
        {
            return false;
        }

        var confirmationBars = Math.Max(strategy.ExitRules.ConfirmedVwapExitBars, 1);
        if (index - confirmationBars + 1 < entryIndex)
        {
            return false;
        }

        for (var i = index - confirmationBars + 1; i <= index; i++)
        {
            var snapshot = snapshots[i];
            if (snapshot.Vwap is null || snapshot.Atr is null)
            {
                return false;
            }

            var failureLevel = snapshot.Vwap.Value - (snapshot.Atr.Value * strategy.ExitRules.ConfirmedVwapExitAtrBuffer);
            if (snapshot.CurrentPrice >= failureLevel)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ShouldExitShortOnConfirmedVwapReclaim(
        StrategyDefinition strategy,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index,
        int entryIndex,
        decimal entryPrice,
        decimal stopDistance,
        decimal lowestLowSinceEntry,
        int barsHeld)
    {
        if (!strategy.ExitRules.EnableConfirmedVwapExit ||
            barsHeld < strategy.ExitRules.MinHoldBarsBeforeTechnicalExit ||
            stopDistance <= 0)
        {
            return false;
        }

        if (strategy.ExitRules.DisableConfirmedVwapExitAfterR is { } disableAfterR &&
            ((entryPrice - lowestLowSinceEntry) / stopDistance) >= disableAfterR)
        {
            return false;
        }

        var confirmationBars = Math.Max(strategy.ExitRules.ConfirmedVwapExitBars, 1);
        if (index - confirmationBars + 1 < entryIndex)
        {
            return false;
        }

        for (var i = index - confirmationBars + 1; i <= index; i++)
        {
            var snapshot = snapshots[i];
            if (snapshot.Vwap is null || snapshot.Atr is null)
            {
                return false;
            }

            var reclaimLevel = snapshot.Vwap.Value + (snapshot.Atr.Value * strategy.ExitRules.ConfirmedVwapExitAtrBuffer);
            if (snapshot.CurrentPrice <= reclaimLevel)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIntradayFlatStrategy(StrategyDefinition strategy)
    {
        return !IsDailyOrHigher(strategy.Timeframe) &&
            !IsDailyOrHigher(strategy.Execution.Timeframe) &&
            strategy.ExitRules.MaxHoldHours <= 8m;
    }

    private static decimal ApplyLongExitSlippage(
        decimal price,
        decimal relativeVolume,
        StrategyDefinition strategy,
        decimal approximateTradeAmount)
    {
        return TradingFlow.Engine.Risk.DynamicSlippageModel.ApplyLongExitSlippage(
            price,
            relativeVolume,
            strategy.Execution.SlippageBps,
            approximateTradeAmount);
    }

    private static decimal ApplyShortEntrySlippage(
        decimal price,
        decimal relativeVolume,
        StrategyDefinition strategy,
        decimal approximateTradeAmount)
    {
        return TradingFlow.Engine.Risk.DynamicSlippageModel.ApplyShortEntrySlippage(
            price,
            relativeVolume,
            strategy.Execution.SlippageBps,
            approximateTradeAmount);
    }

    private static decimal ApplyShortExitSlippage(
        decimal price,
        decimal relativeVolume,
        StrategyDefinition strategy,
        decimal approximateTradeAmount)
    {
        return TradingFlow.Engine.Risk.DynamicSlippageModel.ApplyShortExitSlippage(
            price,
            relativeVolume,
            strategy.Execution.SlippageBps,
            approximateTradeAmount);
    }

    private static BacktestCandidateTrade BuildCandidate(
        StrategyDefinition strategy,
        TradeSignal signal,
        string direction,
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
            direction,
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
        IReadOnlyList<BacktestCandidateTrade> candidates,
        IReadOnlyCollection<OhlcvBar> allBars)
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
                var dailyMetrics = CalculateDailyMetrics(portfolio.StartingCapital, strategy, trades, allBars);

                return new StrategyBacktestResult(
                    strategy.StrategyId,
                    strategy.StrategyName,
                    strategy.Source,
                    portfolio.StartingCapital,
                    Decimal.Round(endingCapital, 4),
                    Decimal.Round(netProfit, 4),
                    Decimal.Round(totalReturnPct, 4),
                    dailyMetrics.AverageDailyReturnPct,
                    dailyMetrics.TradingDayCount,
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
                var dailyPnl = BuildDailyPnlSummary(result);
                var directionPnl = BuildDirectionPnlSummary(result);

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
                    dailyPnl,
                    directionPnl,
                    exitReasonCounts,
                    rejectionCounts
                        .OrderByDescending(x => x.Value)
                        .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
                    BuildDiagnosticSuggestions(result, rejectionCounts, exitReasonCounts, realizedRewardRiskRatio, winRatePct));
            })
            .ToArray();
    }

    private static IReadOnlyList<DirectionPnlSummary> BuildDirectionPnlSummary(StrategyBacktestResult result)
    {
        return result.CompletedTrades
            .GroupBy(trade => String.IsNullOrWhiteSpace(trade.Direction) ? "long" : trade.Direction, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var trades = group.ToArray();
                return new DirectionPnlSummary(
                    group.Key,
                    trades.Length,
                    trades.Count(trade => trade.NetProfit > 0),
                    trades.Count(trade => trade.NetProfit < 0),
                    Decimal.Round(trades.Sum(trade => trade.NetProfit), 4),
                    Decimal.Round(trades.Average(trade => trade.NetProfit), 4),
                    Decimal.Round(trades.Max(trade => trade.NetProfit), 4),
                    Decimal.Round(trades.Min(trade => trade.NetProfit), 4));
            })
            .OrderByDescending(summary => summary.NetProfit)
            .ToArray();
    }

    private static DailyPnlSummary BuildDailyPnlSummary(StrategyBacktestResult result)
    {
        var profitByDay = result.CompletedTrades
            .GroupBy(trade => ToExchangeDate(trade.ExitTimestamp, "America/New_York"))
            .Select(group => new
            {
                Day = group.Key,
                NetProfit = group.Sum(trade => trade.NetProfit)
            })
            .OrderBy(x => x.Day)
            .ToArray();

        var activeDayCount = profitByDay.Length;
        var winningDayCount = profitByDay.Count(x => x.NetProfit > 0);
        var losingDayCount = profitByDay.Count(x => x.NetProfit < 0);
        var averagePerTradingDay = result.TradingDayCount <= 0
            ? 0m
            : result.NetProfit / result.TradingDayCount;
        var averagePerActiveDay = activeDayCount == 0
            ? 0m
            : profitByDay.Average(x => x.NetProfit);
        var bestDay = profitByDay.OrderByDescending(x => x.NetProfit).FirstOrDefault();
        var worstDay = profitByDay.OrderBy(x => x.NetProfit).FirstOrDefault();

        return new DailyPnlSummary(
            result.TradingDayCount,
            activeDayCount,
            winningDayCount,
            losingDayCount,
            Decimal.Round(result.NetProfit, 4),
            Decimal.Round(averagePerTradingDay, 4),
            Decimal.Round(averagePerActiveDay, 4),
            Decimal.Round(bestDay?.NetProfit ?? 0m, 4),
            bestDay?.Day,
            Decimal.Round(worstDay?.NetProfit ?? 0m, 4),
            worstDay?.Day);
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

            if (top.Key.StartsWith("confluence_", StringComparison.OrdinalIgnoreCase))
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

            var grossProfit = candidate.Direction.Equals("short", StringComparison.OrdinalIgnoreCase)
                ? (candidate.EntryPrice - candidate.ExitPrice) * shareQuantity
                : (candidate.ExitPrice - candidate.EntryPrice) * shareQuantity;
            var fees = portfolio.FixedBuyFee + portfolio.FixedSellFee;
            var netProfit = grossProfit - fees;

            accepted.Add(new BacktestTrade(
                candidate.Ticker,
                candidate.StrategyName,
                candidate.Direction,
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

    private static DailyReturnMetrics CalculateDailyMetrics(
        decimal startingCapital,
        StrategyDefinition strategy,
        IReadOnlyList<BacktestTrade> completedTrades,
        IReadOnlyCollection<OhlcvBar> allBars)
    {
        var tradingDays = allBars
            .Where(bar => bar.Timeframe.Equals(strategy.Execution.Timeframe, StringComparison.OrdinalIgnoreCase))
            .Select(bar => ToExchangeDate(bar.Timestamp, strategy.Session.ExchangeTimezone))
            .Distinct()
            .OrderBy(day => day)
            .ToArray();

        if (tradingDays.Length == 0 && completedTrades.Count > 0)
        {
            tradingDays = completedTrades
                .Select(trade => ToExchangeDate(trade.ExitTimestamp, strategy.Session.ExchangeTimezone))
                .Distinct()
                .OrderBy(day => day)
                .ToArray();
        }

        if (startingCapital <= 0 || tradingDays.Length == 0)
        {
            return new DailyReturnMetrics(0, tradingDays.Length);
        }

        var profitByDay = completedTrades
            .GroupBy(trade => ToExchangeDate(trade.ExitTimestamp, strategy.Session.ExchangeTimezone))
            .ToDictionary(group => group.Key, group => group.Sum(trade => trade.NetProfit));
        var averageDailyReturnPct = tradingDays
            .Select(day => profitByDay.TryGetValue(day, out var netProfit) ? (netProfit / startingCapital) * 100m : 0m)
            .Average();

        return new DailyReturnMetrics(Decimal.Round(averageDailyReturnPct, 4), tradingDays.Length);
    }

    private static DateOnly ToExchangeDate(DateTimeOffset timestamp, string timezoneId)
    {
        var zone = ResolveTimezone(timezoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, zone).DateTime);
    }

    private static TimeZoneInfo ResolveTimezone(string timezoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException) when (timezoneId.Equals("America/New_York", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    private static ICatalystProvider? CreateNewsProvider(BacktestRunConfig run)
    {
        if (!run.News.Enabled) return null;
        
        ICatalystProvider? provider = run.News.ProviderName.ToLowerInvariant() switch
        {
            "alpaca" => new TradingFlow.Alpaca.AlpacaNewsProvider(
                new HttpClient(), 
                ResolveAlpacaOptions(run),
                sentimentAnalyzer: CreateSentimentAnalyzer(run.News.SentimentTimeoutSeconds),
                maxArticlesPerTicker: run.News.MaxArticlesPerTicker),
            "finviz" => new TradingFlow.Finviz.FinvizNewsProvider(
                new TradingFlow.Finviz.FinvizClient(
                    new HttpClient(),
                    TradingFlow.Finviz.FinvizOptions.CreateDefault() with { AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? "" }
                )),
            "none" => null,
            _ => throw new NotSupportedException($"Unsupported news provider: {run.News.ProviderName}")
        };

        if (provider is null ||
            run.CachePolicy.Equals("bypass", StringComparison.OrdinalIgnoreCase))
        {
            return provider;
        }

        return new CachedCatalystProvider(
            provider,
            Path.Combine(run.NormalizedRoot, GetCacheWindowSegment(run.TimeWindow)),
            run.CachePolicy);
    }

    private static TradingFlow.Engine.Abstractions.ISentimentAnalyzer CreateSentimentAnalyzer(int timeoutSeconds)
    {
        var endpoint = Environment.GetEnvironmentVariable("FINBERT_SENTIMENT_URL");
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return new TradingFlow.Alpaca.FinbertHttpSentimentAnalyzer(
                new HttpClient(),
                uri,
                requestTimeout: TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        }

        return new TradingFlow.Alpaca.VaderSentimentAnalyzer();
    }

    private static IMarketDataProvider CreateProvider(BacktestRunConfig run)
    {
        IMarketDataProvider provider = run.Provider.ToLowerInvariant() switch
        {
            "csv" => new CsvMarketDataProvider(run.NormalizedRoot),
            "alpaca" => new TradingFlow.Alpaca.AlpacaMarketDataProvider(
                new HttpClient(),
                ResolveAlpacaOptions(run)),
            "finviz" => new TradingFlow.Finviz.FinvizMarketDataProvider(
                new TradingFlow.Finviz.FinvizClient(
                    new HttpClient(),
                    TradingFlow.Finviz.FinvizOptions.CreateDefault() with { AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? "" }
                )),
            _ => throw new NotSupportedException($"Unsupported market data provider: {run.Provider}")
        };

        if (run.Provider.Equals("csv", StringComparison.OrdinalIgnoreCase) ||
            run.CachePolicy.Equals("bypass", StringComparison.OrdinalIgnoreCase))
        {
            return provider;
        }

        return new CachedMarketDataProvider(
            provider,
            Path.Combine(run.NormalizedRoot, GetCacheWindowSegment(run.TimeWindow)),
            run.CachePolicy);
    }

    private static string GetCacheWindowSegment(TimeWindowConfig timeWindow)
    {
        if (timeWindow.Type.Equals("rolling", StringComparison.OrdinalIgnoreCase))
        {
            return $"{timeWindow.LookbackDays + Math.Max(timeWindow.WarmupLookbackDays, 0)}d";
        }

        if (timeWindow.Start is not null && timeWindow.End is not null)
        {
            return $"{timeWindow.Start:yyyyMMdd}-{timeWindow.End:yyyyMMdd}";
        }

        return "custom-window";
    }

    private static TradingFlow.Alpaca.AlpacaOptions ResolveAlpacaOptions(BacktestRunConfig run)
    {
        return TradingFlow.Alpaca.AlpacaOptions.CreateDefault() with
        {
            KeyId = ResolveSecret("Alpaca", "KeyId", "ALPACA_KEY_ID"),
            SecretKey = ResolveSecret("Alpaca", "SecretKey", "ALPACA_SECRET_KEY"),
            MarketDataFeed = run.Providers.Alpaca.DataFeed
        };
    }

    private static string ResolveSecret(string section, string key, string environmentVariable)
    {
        var settingsPath = FindRepositoryFile(Path.Combine("src", "TradingFlow.Web", "appsettings.local.json"));
        if (settingsPath is not null)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.TryGetProperty(section, out var sectionElement) &&
                sectionElement.TryGetProperty(key, out var keyElement))
            {
                var localValue = keyElement.GetString();
                if (!String.IsNullOrWhiteSpace(localValue))
                {
                    return localValue;
                }
            }

        }

        return Environment.GetEnvironmentVariable(environmentVariable) ?? String.Empty;
    }

    internal static string ResolveSecretForTesting(string section, string key, string environmentVariable)
    {
        return ResolveSecret(section, key, environmentVariable);
    }

    private static string? FindRepositoryFile(string relativePath)
    {
        foreach (var startPath in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private static string GetResultPath(BacktestRunConfig run)
    {
        var owner = Environment.GetEnvironmentVariable("TRADINGFLOW_RESULT_OWNER");
        var safeOwner = String.IsNullOrWhiteSpace(owner) ? "shared" : SanitizePathSegment(owner);
        return Path.Combine(run.ResultsRoot, safeOwner, "portfolio", $"{run.RunName}.json");
    }

    private async Task<string> WriteResultAsync(
        string resultPath,
        BacktestResult result,
        ArtifactRetentionConfig artifacts,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        var exclusivePath = ReserveResultPath(resultPath);
        var persistedResult = BacktestArtifactProjector.Project(
            result with { ResultPath = exclusivePath },
            artifacts);
        await _artifactWriter.WriteTextExclusiveAsync(
            exclusivePath,
            JsonSerializer.Serialize(persistedResult, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        return exclusivePath;
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

        try
        {
            return await LoadTickerBarsAsync(
                provider,
                run.Validation.Benchmark.Ticker,
                [run.Intervals[0]],
                start,
                end,
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<OhlcvBar>();
        }
    }
}

internal sealed record DailyReturnMetrics(
    decimal AverageDailyReturnPct,
    int TradingDayCount);

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
        var normalizedReason = NormalizeReason(reason);
        RejectionCounts[normalizedReason] = RejectionCounts.TryGetValue(normalizedReason, out var count) ? count + 1 : 1;
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

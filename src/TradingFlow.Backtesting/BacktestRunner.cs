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
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;
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
    DateTimeOffset WindowEnd);

public sealed partial class BacktestRunner(
    SimpleYamlReader yamlReader,
    IArtifactWriter? artifactWriter = null,
    ICandleStore? candleStore = null,
    IRawArchiveWriter? rawArchiveWriter = null)
{
    private readonly IArtifactWriter _artifactWriter = artifactWriter ?? AtomicFileArtifactWriter.Instance;
    private readonly IRawArchiveWriter? _rawArchiveWriter = rawArchiveWriter;
    private readonly StrategyDecisionBrain _decisionBrain = new();
    private readonly StrategySessionClock _sessionClock = new();
    private readonly CandlePipelineEngine _candlePipeline = new(candleStore);
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
        var strategies = ApplyRunSessionPolicy(run, run.Strategies.Select(yamlReader.ReadStrategy).ToArray());

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
        strategies = ApplyRunSessionPolicy(run, strategies);
        var (resolvedRun, membership) = await ResolveUniverseAsync(run, cancellationToken, progress);
        run = resolvedRun;
        var preparedMarket = await PrepareMarketAsync(run, strategies, cancellationToken, progress);
        return await RunPreparedAsync(run, strategies, startedAt, resultPath, preparedMarket, cancellationToken, progress, membership);
    }

    /// <summary>
    /// Replaces the static ticker list with a point-in-time screened universe when the run
    /// opts into historical_screener mode. No-op for static runs, so existing configs are
    /// unaffected. The screened list is derived only from data before the evaluation start.
    /// </summary>
    private async Task<(BacktestRunConfig Run, UniverseMembership? Membership)> ResolveUniverseAsync(
        BacktestRunConfig run,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress)
    {
        if (run.Universe is not { } universe || !universe.IsHistoricalScreener)
        {
            return (run, null);
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
        return (resolvedRun, membership);
    }

    public async Task<PreparedBacktestMarket> PrepareMarketAsync(
        BacktestRunConfig run,
        IReadOnlyCollection<StrategyDefinition> strategies,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null)
    {
        strategies = ApplyRunSessionPolicy(run, strategies);
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
                ResolveExchangeTimezone(strategies),
                CreateCandleStoreContext(run)),
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
        IProgress<BacktestProgress>? progress = null,
        UniverseMembership? universeMembership = null)
    {
        strategies = ApplyRunSessionPolicy(run, strategies);
        ValidateRun(run, strategies);
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

        foreach (var strategy in strategies)
        {
            progress?.Report(BacktestProgress.StrategyGroup(strategy.StrategyName, totalTickerCount));
        }

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
                try
                {
                    strategyTickerBatches.Add(ProcessPreparedStrategyTicker(
                        run,
                        tickerContexts,
                        workItem.Strategy,
                        workItem.Ticker,
                        preparedMarket.WindowStart,
                        preparedMarket.WindowEnd,
                        workItemCancellation.Token));
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    strategyTickerBatches.Add(StrategyTickerBacktestBatch.Failed(
                        workItem.Ticker,
                        workItem.Strategy,
                        $"strategy_ticker_timeout_after_{(int)strategyWorkItemTimeout.TotalSeconds}s"));
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
        var regimeCalendars = await BuildRegimeCalendarsAsync(
            run,
            strategies,
            preparedMarket.DataStart,
            preparedMarket.WindowEnd,
            cancellationToken);
        var strategyResults = BuildStrategyResults(run.Portfolio, strategies, candidates, allBars, universeMembership, regimeCalendars);
        var diagnostics = BuildDiagnostics(
            strategies,
            strategyResults,
            batches.Select(x => x.Diagnostics).ToArray());
        var completedTrades = strategyResults.SelectMany(x => x.CompletedTrades).ToArray();
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
        var missedMoves = BuildMissedMoveAudits(strategies, candidates, allBars);

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
            missedMoves,
            Array.Empty<FinalizedOrder>());

        result = result with
        {
            ResultPath = await WriteResultAsync(resultPath, result, run.Artifacts, cancellationToken)
        };
        progress?.Report(BacktestProgress.StageOnly("completed", "Backtest completed."));
        return result;
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

    private static CandleStoreContext CreateCandleStoreContext(BacktestRunConfig run)
    {
        return new CandleStoreContext(
            String.IsNullOrWhiteSpace(run.Mode) ? "backtest" : run.Mode,
            run.RunName,
            run.Provider);
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
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!run.Engine.FailFast)
        {
            return PreparedTickerEvaluationContext.Failed(ticker, exception.Message);
        }
    }

    private StrategyTickerBacktestBatch ProcessPreparedStrategyTicker(
        BacktestRunConfig run,
        IReadOnlyDictionary<string, PreparedTickerEvaluationContext> tickerContexts,
        StrategyDefinition strategy,
        string ticker,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
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
                return new StrategyTickerBacktestBatch(
                    ticker,
                    strategy.StrategyId,
                    strategy.StrategyName,
                    true,
                    null,
                    Array.Empty<BacktestCandidateTrade>(),
                    StrategyCandidateDiagnostics.MissingTimeframe(strategy, "missing_signal_timeframe"));
            }

            if (!context.BarsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionBars) ||
                !context.SnapshotsByTimeframe.TryGetValue(strategy.Execution.Timeframe, out var executionSnapshots))
            {
                return new StrategyTickerBacktestBatch(
                    ticker,
                    strategy.StrategyId,
                    strategy.StrategyName,
                    true,
                    null,
                    Array.Empty<BacktestCandidateTrade>(),
                    StrategyCandidateDiagnostics.MissingTimeframe(strategy, "missing_execution_timeframe"));
            }

            var evaluation = CreateStrategyCandidates(
                run,
                strategy,
                strategyBars,
                strategySnapshots,
                executionBars,
                executionSnapshots,
                context.SnapshotsByTimeframe,
                windowStart,
                windowEnd,
                cancellationToken);

            return new StrategyTickerBacktestBatch(
                ticker,
                strategy.StrategyId,
                strategy.StrategyName,
                true,
                null,
                evaluation.Candidates,
                evaluation.Diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!run.Engine.FailFast)
        {
            return StrategyTickerBacktestBatch.Failed(ticker, strategy, exception.Message);
        }
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
                CatalystSnapshotAttacher.AttachToSnapshots(snapshotsByTimeframe[timeframe], catalysts, cancellationToken);
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

    private static TimeSpan ParseTimeframe(string timeframe) => TimeframeParser.Parse(timeframe);

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

    private int FindFirstValidExecutionBarIndexAtOrAfter(
        IReadOnlyList<OhlcvBar> bars,
        DateTimeOffset timestamp,
        StrategyDefinition strategy)
    {
        var index = FindFirstBarIndexAtOrAfter(bars, timestamp);
        while (index < bars.Count &&
               !_sessionClock.ValidateExecutionWindow(bars[index].Timestamp, strategy.Execution.Timeframe, strategy.Session))
        {
            index++;
        }

        return index;
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
            var entryRelativeVolume = StrategyDecisionBrain.ResolveEntryRelativeVolume(strategy, snapshot);
            if (entryRelativeVolume is null)
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

            var volumeRejection = _decisionBrain.GetVolumeConfirmationRejection(strategy, snapshot, entryRelativeVolume.Value);
            if (volumeRejection is not null)
            {
                diagnostics.IncrementRejection(volumeRejection);
                continue;
            }

            var signal = _signalGenerator.CreateTradeSignal(strategy, bars, snapshots, i);
            if (signal is null)
            {
                diagnostics.IncrementRejection("missing_indicator_warmup_or_null");
                continue;
            }

            var longRejection = AllowsLong(strategy)
                ? _decisionBrain.GetLongEntryRejection(strategy, signal, snapshot, entryRelativeVolume.Value)
                : "direction_not_long";
            var shortRejection = AllowsShort(strategy)
                ? _decisionBrain.GetShortEntryRejection(strategy, signal, snapshot, entryRelativeVolume.Value)
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
        var confirmationIndex = FindFirstValidExecutionBarIndexAtOrAfter(executionBars, signalCloseTimestamp, strategy);
        if (confirmationIndex >= executionBars.Count)
        {
            return ("no_next_bar_or_invalid_stop", confirmationIndex);
        }

        var confirmationBar = executionBars[confirmationIndex];
        var entryIndex = strategy.EntryRules.EnableEntryBarConfirmation
            ? FindNextValidExecutionBarIndex(executionBars, confirmationIndex + 1, strategy)
            : confirmationIndex;
        if (entryIndex >= executionBars.Count)
        {
            return ("no_next_bar_after_entry_confirmation", entryIndex);
        }

        var entryBar = executionBars[entryIndex];
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
                    cancellationToken);
                if (bars.Count > 0)
                {
                    return bars;
                }
            }
            catch (InvalidOperationException)
            {
                // This timeframe has no data for the benchmark; try the next.
            }
        }

        return Array.Empty<OhlcvBar>();
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

internal sealed record PreparedTickerEvaluationContext(
    string Ticker,
    IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> BarsByTimeframe,
    IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> SnapshotsByTimeframe,
    IReadOnlyList<OhlcvBar> ProcessedBars,
    int TotalPreparedBarCount,
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
    StrategyCandidateDiagnostics Diagnostics)
{
    public static StrategyTickerBacktestBatch Failed(string ticker, StrategyDefinition strategy, string errorMessage)
    {
        var diagnostics = StrategyCandidateDiagnostics.MissingTimeframe(strategy, $"strategy_ticker_failed ({errorMessage})");
        return new StrategyTickerBacktestBatch(
            ticker,
            strategy.StrategyId,
            strategy.StrategyName,
            false,
            errorMessage,
            Array.Empty<BacktestCandidateTrade>(),
            diagnostics);
    }
}

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

    private static string NormalizeReason(string reason)
    {
        var trimmed = reason.Trim();
        var detailStart = trimmed.IndexOf(" (", StringComparison.Ordinal);
        return detailStart > 0
            ? trimmed[..detailStart]
            : trimmed;
    }
}








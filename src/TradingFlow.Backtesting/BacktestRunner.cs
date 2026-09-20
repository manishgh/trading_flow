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
    DateTimeOffset WindowEnd);

public sealed partial class BacktestRunner(
    SimpleYamlReader yamlReader,
    StrategyArtifactCatalog strategyArtifacts,
    IArtifactWriter? artifactWriter = null,
    ICandleStore? candleStore = null,
    IRawArchiveWriter? rawArchiveWriter = null)
{
    private readonly IArtifactWriter _artifactWriter = artifactWriter ?? AtomicFileArtifactWriter.Instance;
    private readonly IRawArchiveWriter? _rawArchiveWriter = rawArchiveWriter;
    private readonly StrategySessionClock _sessionClock = new();
    private readonly CandlePipelineEngine _candlePipeline = new(candleStore);
    private readonly BacktestValidator _validator = new();

    public async Task<BacktestResult> RunAsync(
        string configPath,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null)
    {
        var run = yamlReader.ReadBacktestRun(configPath);
        return await RunAsync(
            configPath,
            GetResultPath(run),
            DateTimeOffset.UtcNow,
            cancellationToken,
            progress);
    }

    public async Task<BacktestResult> RunAsync(
        string configPath,
        string resultPath,
        DateTimeOffset evaluationCutoffUtc,
        CancellationToken cancellationToken,
        IProgress<BacktestProgress>? progress = null)
    {
        var startedAt = DateTimeOffset.UtcNow;
        progress?.Report(BacktestProgress.StageOnly("loading_config", $"Loading run config {Path.GetFileName(configPath)}."));
        var run = yamlReader.ReadBacktestRun(configPath);
        if (run.TimeWindow.Type.Equals("rolling", StringComparison.OrdinalIgnoreCase))
        {
            run = run with { TimeWindow = run.TimeWindow with { End = evaluationCutoffUtc } };
        }
        progress?.Report(BacktestProgress.StageOnly("loading_strategies", $"Loading {run.Strategies.Count} strategy config(s)."));
        var artifactValidator = new StrategyRunArtifactValidator(yamlReader, strategyArtifacts);
        var strategies = ApplyRunSessionPolicy(
            run,
            run.Strategies
                .Select(path => artifactValidator.ReadAndValidate(path, StrategySelectionMode.Backtest))
                .ToArray());

        return await RunAsync(run, strategies, startedAt, resultPath, cancellationToken, progress);
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
                IncludeExtendedHours: true,
                ResolveExchangeTimezone(strategies),
                CreateCandleStoreContext(run),
                run.Engine.MaxRetainedReplayBars),
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
        UniverseMembership? universeMembership = null,
        UniversePromotionEvidence? universePromotionEvidence = null)
    {
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
        using var candidateHypothesisJournal = new ExecutionEventJournal(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(resultPath))!, ".execution-journal", $"candidate-{Guid.NewGuid():N}.ndjson"));
        using var portfolioExecutionJournal = new ExecutionEventJournal(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(resultPath))!, ".execution-journal", $"portfolio-{Guid.NewGuid():N}.ndjson"));
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
                var candidateJournalPath = CreateCandidateAuditJournalPath(run, workItem.Strategy, workItem.Ticker);
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
        var candidateAuditJournals = batches
            .Select(batch => batch.CandidateAuditJournalPath)
            .Where(path => !String.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
        var candidateDiagnostics = batches.Select(batch => batch.Diagnostics).ToArray();
        var unifiedPortfolio = BuildUnifiedPortfolioResult(
            run.Portfolio,
            strategies,
            candidates,
            allBars,
            universeMembership,
            regimeCalendars,
            portfolioExecutionJournal);
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
            regimeCalendars);
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
        var missedMoves = BuildMissedMoveAudits(strategies, candidates, allBars);
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
            EconomicResultsComplete: economicResultsComplete);

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
            CompletedTrades = Array.Empty<BacktestTrade>(),
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
            CompletedTrades = Array.Empty<BacktestTrade>(),
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
                return new StrategyTickerBacktestBatch(
                    ticker,
                    strategy.StrategyId,
                    strategy.StrategyName,
                    true,
                    null,
                    Array.Empty<BacktestCandidateTrade>(),
                    Array.Empty<BacktestCandidateDecisionAudit>(),
                    null,
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
                    Array.Empty<BacktestCandidateDecisionAudit>(),
                    null,
                    StrategyCandidateDiagnostics.MissingTimeframe(strategy, "missing_execution_timeframe"));
            }

            var signalTimeframe = ParseTimeframe(strategy.Timeframe);
            var availableWarmupBars = strategyBars.Count(
                bar => bar.Timestamp.Add(signalTimeframe) <= windowStart);
            if (availableWarmupBars < run.Engine.IndicatorWarmupBars)
            {
                return StrategyTickerBacktestBatch.Failed(
                    ticker,
                    strategy,
                    $"insufficient_indicator_warmup: {ticker} {strategy.Timeframe} has " +
                    $"{availableWarmupBars} completed pre-evaluation bars; " +
                    $"{run.Engine.IndicatorWarmupBars} required. Increase time_window.warmup_lookback_days.");
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
                evaluation.Diagnostics);
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
        var startIndex = Math.Max(run.Engine.IndicatorWarmupBars, 1);
        var runtimeStrategy = StrategyDecisionRequestAssembler.CreateBacktestRuntimeStrategy(
            strategy,
            strategyArtifacts);
        var candidateDecisions = new StrategyCandidateDecisionOrchestrator(
            new StrategyDecisionKernel(),
            candidateJournal);
        var simulationRun = BacktestCandidateJournal.CreateRun(
            new { Run = run, Strategy = runtimeStrategy.Identity, Ticker = snapshots[0].Ticker },
            $"{run.RunName}:{runtimeStrategy.Identity.StrategyId}:{snapshots[0].Ticker}",
            evaluationStart);

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
            var discoveryExpiry = snapshots[i + 1].Timestamp.Add(ParseTimeframe(strategy.Timeframe));
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
                var nextEntryIndex = FindNextValidExecutionBarIndex(
                    executionBars,
                    FindFirstBarIndexAtOrAfter(executionBars, decisionTime),
                    strategy);
                var eligibilityDay = nextEntryIndex < executionBars.Count
                    ? DateOnly.FromDateTime(executionBars[nextEntryIndex].Timestamp.UtcDateTime)
                    : DateOnly.FromDateTime(decisionTime.UtcDateTime);
                var universeEligible = universeMembership is null ||
                    universeMembership.IsMember(snapshot.Ticker, eligibilityDay);
                var regimeEligible = regimeCalendar is null || regimeCalendar.IsOn(eligibilityDay);
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
                    simulationRun.RunId);
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

                var candidate = CreateCandidate(
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
                candidates.Add(candidate.Value.Candidate);
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

    private static string CreateCandidateAuditJournalPath(
        BacktestRunConfig run,
        StrategyDefinition strategy,
        string ticker)
    {
        var owner = Environment.GetEnvironmentVariable("TRADINGFLOW_RESULT_OWNER");
        var safeOwner = String.IsNullOrWhiteSpace(owner) ? "shared" : SanitizePathSegment(owner);
        var directory = Path.Combine(
            run.ResultsRoot,
            ".candidate-journal",
            safeOwner,
            SanitizePathSegment(run.RunName));
        return Path.Combine(
            directory,
            $"{SanitizePathSegment(strategy.StrategyId)}-{SanitizePathSegment(ticker)}-{Guid.NewGuid():N}.ndjson");
    }

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
        var exclusivePath = ReserveResultPath(resultPath);
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

    private static bool IsResultPathAvailable(string path) =>
        !File.Exists(path) &&
        !File.Exists(Path.ChangeExtension(path, ".candidate-decisions.json")) &&
        !File.Exists(Path.ChangeExtension(path, ".candidate-hypotheses.json")) &&
        !File.Exists(Path.ChangeExtension(path, ".portfolio-execution.json")) &&
        !File.Exists(Path.ChangeExtension(path, ".unified-completed-trades.json")) &&
        !File.Exists(Path.ChangeExtension(path, ".manifest.json"));

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

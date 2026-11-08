using System.Text.Json;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Backtesting;

public sealed partial class BacktestRunner
{
    // Admit the separate diagnostic policy only with its supported session/entry/protection contract.
    private static void ValidateIntradayRun(BacktestRunConfig run, IReadOnlyCollection<StrategyDefinition> strategies)
    {
        if (strategies.Count != 1 || run.Universe is { IsHistoricalScreener: true })
            throw new InvalidOperationException("The intraday diagnostic requires one strategy and a frozen stock cohort per portfolio.");
        var strategy = strategies.Single();
        if (strategy.ExitRules.ContinuationProtection is null && strategy.EntryRules.OpeningRangeBreakout is not { IsPublishedRecipe: true })
            throw new InvalidOperationException("The intraday replay requires continuation protection.");
        if (strategy.EntryRules.OpeningRangeBreakout is { } opening)
        {
            opening.Validate(strategy);
            if (run.Engine.ExecutionReplayPolicyId != ExecutionReplayPolicy.ForEntryMode(
                opening.PolicyId == OpeningRangeBreakoutRules.CompletedBreakoutPolicyId ? IntradayEntryOrderMode.NextBarMarket : IntradayEntryOrderMode.StopMarket))
                throw new InvalidOperationException("Opening-range entries require the versioned entry-mode replay policy.");
        }
        else throw new InvalidOperationException("The intraday replay requires an explicit supported entry policy.");
        if (run.Intervals.Count != 1 || run.Intervals[0] != "5m")
            throw new InvalidOperationException("The intraday diagnostic requires native five-minute source candles.");
    }

    // Feed shared decisions into the chronological intraday adapter and publish through ordinary result retention.
    private async Task<BacktestResult> RunIntradayPreparedAsync(BacktestRunConfig run, StrategyDefinition strategy,
        DateTimeOffset startedAt, string resultPath, PreparedBacktestMarket preparedMarket,
        CancellationToken token, IProgress<BacktestProgress>? progress, UniversePromotionEvidence? promotionEvidence)
    {
        progress?.Report(BacktestProgress.StageOnly("intraday_candidates", "Evaluating completed intraday entry evidence."));
        await BuildRegimeCalendarsAsync(run, [strategy], preparedMarket.DataStart, preparedMarket.WindowEnd, token);
        var sessions = candidateSessionSchedules ?? throw new InvalidDataException("Intraday execution requires the archived exchange calendar.");
        var batches = await BuildIntradayCandidatesAsync(run, strategy, preparedMarket, token);
        var candidates = batches.SelectMany(batch => batch.CandidateTrades)
            .OrderBy(candidate => candidate.EntryTimestamp).ThenBy(candidate => candidate.Ticker, StringComparer.Ordinal).ToArray();
        var bars = preparedMarket.MarketState.TickerStates.Values
            .Where(state => state.BarsByTimeframe.ContainsKey("5m"))
            .SelectMany(state => state.BarsByTimeframe["5m"]).ToArray();
        using var hypotheses = new ExecutionEventJournal(CreateExecutionJournalPath("candidate"));
        using var execution = new ExecutionEventJournal(CreateExecutionJournalPath("portfolio"));
        var replay = IntradayPortfolioExecution.Replay(new(run.Portfolio, [strategy], candidates, bars,
            sessions, preparedMarket.WindowStart, preparedMarket.WindowEnd), token);
        var outcome = IntradayOutcome(replay, candidates, batches, preparedMarket.WindowStart);
        IntradayPortfolioEvidence.Publish(hypotheses, execution, candidates, replay);
        // Warm-up candles remain execution/indicator inputs; reported days and bar counts describe only completed evaluation intervals.
        var evaluationBars = bars.Where(bar => bar.Timestamp >= preparedMarket.EvaluationStart &&
            bar.Timestamp.AddMinutes(5) <= preparedMarket.WindowEnd).ToArray();
        var unified = BuildUnifiedPortfolioResult(run.Portfolio, [strategy], candidates, evaluationBars, null, null, execution, outcome);
        var strategyResults = BuildStrategyResults(run.Portfolio, [strategy], candidates, evaluationBars,
            batches.Select(batch => batch.Diagnostics).ToArray(), eventOutcome: outcome);
        var complete = unified.EconomicResultsComplete && batches.All(batch => batch.Succeeded);
        if (!complete)
        {
            unified = SuppressUnifiedPortfolioEconomicClaims(unified);
            strategyResults = strategyResults.Select(SuppressStrategyEconomicClaims).ToArray();
        }
        var tickerResults = batches.Select(batch => batch.Succeeded
            ? TickerBacktestResult.Completed(batch.Ticker, evaluationBars.Count(bar => bar.Ticker == batch.Ticker), batch.CandidateTrades.Count)
            : TickerBacktestResult.Failed(batch.Ticker, new InvalidDataException(batch.ErrorMessage))).ToArray();
        var single = strategyResults.Single();
        var result = new BacktestResult(run.RunName, resultPath, startedAt, DateTimeOffset.UtcNow,
            run.Portfolio.StartingCapital, unified.EndingCapital, unified.NetProfit, unified.TotalReturnPct,
            single.AverageDailyReturnPct, single.TradingDayCount, unified.MaxDrawdownPct, null,
            evaluationBars.Length, candidates.Length, unified.AcceptedTradeCount, unified.RejectedTradeCount,
            unified.WinningTradeCount, unified.LosingTradeCount, strategyResults, tickerResults,
            _validator.Validate(run, evaluationBars, preparedMarket.BenchmarkBars, strategyResults),
            complete ? outcome.Trades : [], BuildDiagnostics([strategy], strategyResults,
                batches.Select(batch => batch.Diagnostics).ToArray()), [], Array.Empty<FinalizedOrder>(), unified,
            new UniversePromotionEligibilityValidator().Validate(promotionEvidence),
            EconomicResultsComplete: complete, PreparedMarketData: preparedData?.Summary ?? preparedMarket.PreparedMarketData)
        { PortfolioState = outcome.State };
        var journals = batches.Where(batch => batch.CandidateAuditJournalPath is not null)
            .Select(batch => batch.CandidateAuditJournalPath!).ToArray();
        var artifacts = await WriteResultAsync(resultPath, result, run.Artifacts, hypotheses, execution,
            outcome.Trades, journals, run.Tickers.Count, batches, token);
        progress?.Report(BacktestProgress.StageOnly("completed", complete
            ? "Intraday diagnostic completed; candle fill and short-borrow assumptions apply."
            : "Intraday diagnostic completed with unavailable economics; inspect execution failures and source evidence."));
        // Keep run-owned journals until the independent quant approves this exact run's cleanup.
        return result with
        {
            ResultPath = artifacts.ResultPath, CandidateDecisionAuditPath = artifacts.CandidateDecisionAuditPath,
            ExecutionAuditPath = artifacts.PortfolioExecutionAuditPath,
            CandidateHypothesisAuditPath = artifacts.CandidateHypothesisAuditPath,
            PortfolioExecutionAuditPath = artifacts.PortfolioExecutionAuditPath,
            ArtifactManifestPath = artifacts.ArtifactManifestPath, ArtifactReferences = artifacts.ArtifactReferences
        };
    }

    // Adapt replay accounting to the existing result builders and withhold claims for failed source work items.
    private static PortfolioBuildOutcome IntradayOutcome(IntradayPortfolioReplayOutcome replay,
        IReadOnlyList<BacktestCandidateTrade> candidates, IReadOnlyList<StrategyTickerBacktestBatch> batches,
        DateTimeOffset start)
    {
        var audit = new PortfolioAdmissionAudit();
        var byId = candidates.ToDictionary(candidate => candidate.CandidateId, StringComparer.Ordinal);
        foreach (var disposition in replay.Dispositions.Where(item => !item.Accepted))
            if (byId.TryGetValue(disposition.CandidateId, out var candidate)) audit.Reject(candidate, disposition.Reason);
        var state = IntradayDecisionEvidenceProjection.Attach(replay.PortfolioState, candidates, replay);
        if (batches.Any(batch => !batch.Succeeded))
        {
            var unavailableFrom = batches.Where(batch => !batch.Succeeded)
                .Min(batch => batch.IntradayUnavailableFromUtc ?? start);
            state = NativeDailySessionLedger.MarkUnavailableFrom(state,
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(unavailableFrom, TimeZoneInfo.FindSystemTimeZoneById("America/New_York")).DateTime),
                "Selected stocks have unavailable source evidence: " + String.Join("; ",
                    batches.Where(batch => !batch.Succeeded).Select(batch => $"{batch.Ticker}: {batch.ErrorMessage}")));
        }
        return new(replay.Trades, audit, replay.Failures) { State = state };
    }

}

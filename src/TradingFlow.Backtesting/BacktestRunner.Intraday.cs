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
        if (strategy.ExitRules.ContinuationProtection is null && strategy.EntryRules.IntradayZone is not { IsPythonFixedTargetComparison: true })
            throw new InvalidOperationException("The intraday replay requires continuation protection.");
        if (strategy.EntryRules.IntradayZone is { } intradayZone)
        {
            intradayZone.Validate(strategy);
            var zonePolicy = intradayZone.IsPythonVolumeSpreadComparison ? ExecutionReplayPolicy.PythonZoneVolumeSpreadV1
                : intradayZone.IsPythonComparison ? ExecutionReplayPolicy.PythonZoneBounceBreakdownV1
                : ExecutionReplayPolicy.IntradayRthFiveMinuteQuoteMarketV1;
            if (run.Engine.ExecutionReplayPolicyId != zonePolicy)
                throw new InvalidOperationException("Intraday zones require the execution policy frozen for their selected strategy.");
            if (run.Portfolio.MaxConcurrentPositions != 1)
                throw new InvalidOperationException("Intraday zones require one open position per portfolio.");
        }
        else if (strategy.EntryRules.OpeningRangeBreakout is { } opening)
        {
            opening.Validate(strategy);
            if (run.Engine.ExecutionReplayPolicyId != ExecutionReplayPolicy.ForEntryMode(
                opening.PolicyId == OpeningRangeBreakoutRules.CompletedBreakoutPolicyId ? IntradayEntryOrderMode.NextBarMarket : IntradayEntryOrderMode.StopMarket))
                throw new InvalidOperationException("Opening-range entries require the versioned entry-mode replay policy.");
        }
        else if (strategy.EntryRules.PreviousSessionBreakout is { } previous)
        {
            previous.Validate(strategy);
            if (run.Engine.ExecutionReplayPolicyId != ExecutionReplayPolicy.IntradayRthFiveMinuteV1)
                throw new InvalidOperationException("Previous-session entries require the retained limit replay policy.");
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
        var zoneStrategy = strategy.EntryRules.IntradayZone is not null;
        var quoteReplay = run.Engine.ExecutionReplayPolicyId == ExecutionReplayPolicy.IntradayRthFiveMinuteQuoteMarketV1;
        var batches = zoneStrategy ? await BuildZoneCandidatesAsync(run, strategy, preparedMarket, token)
            : await BuildIntradayCandidatesAsync(run, strategy, preparedMarket, token);
        var candidates = batches.SelectMany(batch => batch.CandidateTrades)
            .OrderBy(candidate => candidate.EntryTimestamp).ThenBy(candidate => candidate.Ticker, StringComparer.Ordinal).ToArray();
        var bars = preparedMarket.MarketState.TickerStates.Values
            .Where(state => state.BarsByTimeframe.ContainsKey("5m"))
            .SelectMany(state => state.BarsByTimeframe["5m"])
            .Where(bar => !zoneStrategy || IsIntradayRegularBar(bar, sessions,
                TimeZoneInfo.FindSystemTimeZoneById(strategy.Session.ExchangeTimezone))).ToArray();
        using var hypotheses = new ExecutionEventJournal(CreateExecutionJournalPath("candidate"));
        using var execution = new ExecutionEventJournal(CreateExecutionJournalPath("portfolio"));
        IntradayPortfolioReplayOutcome? replay = null;
        PortfolioBuildOutcome outcome;
        if (quoteReplay)
        {
            foreach (var candidate in candidates)
                hypotheses.Append(new(candidate.Ticker, candidate.StrategyName, candidate.EntryTimestamp,
                    ExecutionState.SignalGenerated, "Completed zone confirmation; future quote executions remain unobserved.",
                    "candidate_hypothesis", candidate.CandidateId, JsonSerializer.Serialize(candidate)));
            outcome = await ExecuteEventPortfolioAsync(run, [strategy], candidates, bars, null,
                new Dictionary<string, TradingFlow.Engine.Regime.RegimeCalendar>(), execution, token);
        }
        else
        {
            replay = IntradayPortfolioExecution.Replay(new(run.Portfolio, [strategy], candidates, bars,
                sessions, preparedMarket.WindowStart, preparedMarket.WindowEnd), token);
            candidates = ApplySelectedZoneAlternatives(candidates, replay.SelectedCandidates);
            outcome = IntradayOutcome(replay, candidates, batches, preparedMarket.WindowStart);
            IntradayPortfolioEvidence.Publish(hypotheses, execution, candidates, replay);
        }
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
        { PortfolioState = outcome.State, ZoneDecisions = zoneStrategy ? ProjectZoneDecisions(candidates, outcome, replay) : null };
        var journals = batches.Where(batch => batch.CandidateAuditJournalPath is not null)
            .Select(batch => batch.CandidateAuditJournalPath!).ToArray();
        var artifacts = await WriteResultAsync(resultPath, result, run.Artifacts, hypotheses, execution,
            outcome.Trades, journals, run.Tickers.Count, batches, token);
        progress?.Report(BacktestProgress.StageOnly("completed", complete
            ? quoteReplay ? "Intraday SIP replay completed; actual historical quote/trade execution and research short-borrow assumptions apply."
                : "Intraday diagnostic completed; candle fill and short-borrow assumptions apply."
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

    // Substitute only the alternative chosen by chronological admission so every downstream audit uses its actual geometry.
    internal static BacktestCandidateTrade[] ApplySelectedZoneAlternatives(
        IReadOnlyList<BacktestCandidateTrade> candidates, IReadOnlyList<BacktestCandidateTrade> selected)
    {
        if (selected.Count == 0) return candidates.ToArray();
        var byId = selected.ToDictionary(candidate => candidate.CandidateId, StringComparer.Ordinal);
        return candidates.Select(candidate => byId.GetValueOrDefault(candidate.CandidateId, candidate)).ToArray();
    }

    // Bound display rows to frozen hypotheses and either observed SIP or diagnostic candle lifecycle.
    internal static IReadOnlyList<BacktestZoneDecisionDetail> ProjectZoneDecisions(
        IReadOnlyList<BacktestCandidateTrade> candidates, PortfolioBuildOutcome outcome, IntradayPortfolioReplayOutcome? candleReplay)
    {
        var dispositions = (outcome.EventReplay?.Dispositions ?? candleReplay?.Dispositions ?? [])
            .ToDictionary(item => item.CandidateId);
        var trades = outcome.Trades.ToDictionary(item => item.CandidateId);
        var failures = outcome.ExecutionFailures.ToDictionary(item => item.CandidateId);
        var observed = (candleReplay?.Events ?? []).ToLookup(item => item.CandidateId);
        var entries = (candleReplay?.PortfolioState.Fills ?? []).Where(fill => fill.IsEntry).ToLookup(fill => fill.CandidateId);
        var alternativeDecisions = (candleReplay?.AlternativeDecisions ?? []).ToLookup(item => item.CandidateId);
        return candidates.Select(candidate =>
        {
            var signal = candidate.ZoneExecution!;
            var evidence = signal.Evidence;
            var zone = evidence.Zone;
            trades.TryGetValue(candidate.CandidateId, out var trade);
            failures.TryGetValue(candidate.CandidateId, out var failure);
            dispositions.TryGetValue(candidate.CandidateId, out var disposition);
            var latest = observed[candidate.CandidateId].LastOrDefault();
            var entryFills = entries[candidate.CandidateId].ToArray();
            var entryQuantity = entryFills.Sum(fill => fill.Quantity);
            var actualEntry = trade?.EntryPrice ?? (entryQuantity > 0
                ? entryFills.Sum(fill => fill.Price * fill.Quantity) / entryQuantity : (decimal?)null);
            return new BacktestZoneDecisionDetail(candidate.CandidateId, candidate.StrategyId, candidate.Ticker,
                candidate.Direction, signal.Pattern, zone.ZoneId, zone.Version, zone.Role.ToString(), zone.Bottom, zone.Top,
                zone.PivotCount, zone.LatestPivot.PivotBarStartUtc, zone.LatestPivot.PivotCompletedAtUtc,
                zone.LatestPivot.ConfirmedAtUtc, zone.KnownAtUtc, evidence.FlippedAtUtc, signal.ConfirmationBar.Timestamp,
                evidence.ConfirmationCompletedAtUtc, signal.ConfirmationBar.Close, evidence.Vwap, evidence.SlotRelativeVolume,
                evidence.SlotMedianVolume, evidence.SlotRelativeVolumeSampleCount, signal.InitialStopPrice,
                signal.SessionDeadlineUtc, trade is not null ? "closed" : failure is not null ? "unresolved" : entryQuantity > 0 ? "open" : "not_filled",
                trade?.ExitReason ?? failure?.Reason ?? latest?.Reason ?? disposition?.Reason ?? "no_fill_observed", actualEntry, trade?.ExitPrice,
                signal.Evidence.PolicyId, signal.EntryCutoffUtc, signal.FixedTargetPrice, evidence.Atr,
                evidence.TargetZone?.ZoneId, evidence.TargetZone?.Bottom, evidence.TargetZone?.Top,
                ZoneVolumeSpreadDetail(evidence.VolumeSpread), alternativeDecisions[candidate.CandidateId].Select(item =>
                    new BacktestZoneAlternativeDecisionDetail(item.AlternativeIndex, item.Direction, item.ZoneId,
                        item.PlannedQuantity, item.EstimatedNetRiskDollars, item.EstimatedNetRewardDollars,
                        item.RewardRisk, item.Selected, item.Reason, item.TargetPlan)).ToArray(), evidence.TargetPlan, evidence.LongBounceVolumeMode,
                evidence.StopBasis, evidence.PreviousBar?.Low, signal.ConfirmationBar.Low, evidence.RawStopPrice, signal.TickSize,
                evidence.Retest, evidence.TargetZone?.Version, evidence.TargetZone?.KnownAtUtc);
        }).ToArray();
    }

    // Copy the selected alternative's fee estimate verbatim; the admission ratio is separate from simulator fill accounting.
    private static BacktestZoneVolumeSpreadDetail? ZoneVolumeSpreadDetail(PythonZoneVolumeSpreadEvidence? item) =>
        item is null ? null : new(item.BidPrice, item.AskPrice, item.FullSpreadBps, item.MaximumSpreadBps,
            item.MinimumRewardRisk, item.OrderedAlternativeIndex, item.PlannedQuantity, item.EstimatedRoundTripTicketFees,
            item.EstimatedNetRiskDollars, item.EstimatedNetRewardDollars, item.SelectedRewardRisk);

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

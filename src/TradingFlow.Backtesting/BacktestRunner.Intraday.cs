using System.Text.Json;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
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
        CancellationToken token, IProgress<BacktestProgress>? progress, UniversePromotionEvidence? promotionEvidence,
        UniverseMembership? membership = null)
    {
        progress?.Report(BacktestProgress.StageOnly("intraday_candidates", "Evaluating completed intraday entry evidence."));
        await BuildRegimeCalendarsAsync(run, [strategy], preparedMarket.DataStart, preparedMarket.WindowEnd, token);
        var sessions = candidateSessionSchedules ?? throw new InvalidDataException("Intraday execution requires the archived exchange calendar.");
        // A frozen ranked list limits evaluation, coverage checks and one-minute bars to each stock's member sessions.
        var rankedMembers = run.Universe?.RankedMembership is not null ? membership : null;
        var batches = await BuildIntradayCandidatesAsync(run, strategy, preparedMarket, token, rankedMembers);
        var candidates = batches.SelectMany(batch => batch.CandidateTrades)
            .OrderBy(candidate => candidate.EntryTimestamp).ThenBy(candidate => candidate.Ticker, StringComparer.Ordinal).ToArray();
        var bars = preparedMarket.MarketState.TickerStates.Values
            .Where(state => state.BarsByTimeframe.ContainsKey("5m"))
            .SelectMany(state => state.BarsByTimeframe["5m"]).ToArray();
        using var hypotheses = new ExecutionEventJournal(CreateExecutionJournalPath("candidate"));
        using var execution = new ExecutionEventJournal(CreateExecutionJournalPath("portfolio"));
        var (eligible, notEligible) = SplitByDailyEligibility(candidates, membership);
        var quiet = await PublishQuietIntervalsAsync(run, batches, token);
        var closes = strategy.EntryRules.OpeningRangeBreakout is { ExitAtSessionClose: true }
            ? await LoadSessionClosePricesAsync(run, preparedMarket.DataStart, preparedMarket.WindowEnd, token) : null;
        var minutes = rankedMembers is null ? null
            : await LoadMemberMinuteBarsAsync(run, rankedMembers, preparedMarket.DataStart, preparedMarket.WindowEnd, token);
        var replay = IntradayPortfolioExecution.Replay(new(run.Portfolio, [strategy], eligible, bars,
            sessions, preparedMarket.WindowStart, preparedMarket.WindowEnd, quiet, closes, minutes), token);
        if (minutes is not null) await PublishEntryBarMinuteOrderAsync(run, replay, minutes.Count, token);
        var outcome = IntradayOutcome(replay, candidates, batches, preparedMarket.WindowStart, notEligible);
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

    /// <summary>
    /// Each stock's official closing price per session (the daily bar close, keyed by the daily bar's date, which is the New
    /// York session date for midnight-stamped daily bars) for strategies that exit at the close. Read through the run's own
    /// provider, so a prepared run reads the frozen daily bars its plan admitted. A prepared run serves only the exact range
    /// its plan requested, so <paramref name="start"/> must be the warmup start the plan used (the run's data start).
    /// </summary>
    private async Task<IReadOnlyDictionary<(string Ticker, DateOnly Session), decimal>> LoadSessionClosePricesAsync(
        BacktestRunConfig run, DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        var closes = new Dictionary<(string, DateOnly), decimal>();
        await foreach (var bar in CreateProvider(run).GetBarsAsync(run.Tickers, ["1d"], start, end, token))
            if (bar.Timestamp < end) closes[(bar.Ticker.ToUpperInvariant(), DateOnly.FromDateTime(bar.Timestamp.UtcDateTime))] = bar.Close;
        return closes;
    }

    /// <summary>
    /// The one-minute bars of each stock on its member sessions (New York dates), read through the run's own provider with the
    /// exact range the prepared plan admitted (the run's data start to its end). Minutes on other sessions are dropped.
    /// </summary>
    private async Task<IReadOnlyCollection<OhlcvBar>> LoadMemberMinuteBarsAsync(BacktestRunConfig run, UniverseMembership members,
        DateTimeOffset start, DateTimeOffset end, CancellationToken token)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var minutes = new List<OhlcvBar>();
        await foreach (var bar in CreateProvider(run).GetBarsAsync(run.Tickers, ["1m"], start, end, token))
            if (members.IsMember(bar.Ticker, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bar.Timestamp, zone).DateTime))) minutes.Add(bar);
        return minutes;
    }

    /// <summary>
    /// Publishes, beside the results, how the entry five-minute bars that also touched the stop were settled: by one-minute
    /// bars (stop after the entry minute, or no stop after it), as same-minute losses, or left unknown with no minute bars or
    /// with minutes that do not match the five-minute high and low (both counted as losses). Each order is counted once.
    /// </summary>
    private async Task PublishEntryBarMinuteOrderAsync(BacktestRunConfig run, IntradayPortfolioReplayOutcome replay, int minuteBars,
        CancellationToken token)
    {
        var decided = replay.Events.Where(item => item.State == IntradayPortfolioExecution.EntryBarMinuteOrderState)
            .GroupBy(item => item.CandidateId, StringComparer.Ordinal).Select(group => group.First()).ToArray();
        var counts = IntradayPortfolioExecution.EntryBarMinuteOrderReasons.ToDictionary(reason => reason,
            reason => decided.Count(item => item.Reason == reason), StringComparer.Ordinal);
        var payload = new
        {
            runName = run.RunName, minuteBarsLoaded = minuteBars,
            entryBarsSettledByMinutes = counts[IntradayPortfolioExecution.MinutesSettledStopAfterEntry] + counts[IntradayPortfolioExecution.MinutesSettledNoStopAfterEntry],
            sameMinuteLosses = counts[IntradayPortfolioExecution.SameMinuteLoss],
            unknownNoMinuteBars = counts[IntradayPortfolioExecution.UnknownNoMinuteBars],
            unknownMinutesDoNotMatch = counts[IntradayPortfolioExecution.UnknownMinutesDoNotMatch],
            byReason = counts,
            orders = decided.Select(item => new { item.CandidateId, item.Ticker, entryBarEndUtc = item.TimestampUtc, item.Reason }).ToArray()
        };
        await _artifactWriter.WriteTextAsync(Path.Combine(run.ResultsRoot, "evidence", $"{run.RunName}-entry-bar-minute-order.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), token);
    }

    // Same per-day rule as the daily paths: a candidate whose stock did not qualify on its entry day (UTC date, which equals
    // the New York date during regular hours) never reaches the order replay. No membership means every candidate proceeds.
    // A ranked membership (known at 09:35) also refuses an entry decided before it was known.
    private static (IReadOnlyList<BacktestCandidateTrade> Eligible, IReadOnlyList<BacktestCandidateTrade> NotEligible) SplitByDailyEligibility(
        IReadOnlyList<BacktestCandidateTrade> candidates, UniverseMembership? membership)
    {
        if (membership is null) return (candidates, []);
        var lookup = candidates.ToLookup(candidate => membership.IsMemberAt(candidate.Ticker, candidate.EntryTimestamp));
        return (lookup[true].ToArray(), lookup[false].ToArray());
    }

    // Adapt replay accounting to the existing result builders and withhold claims for failed source work items. Candidates
    // refused before the replay are recorded in the admission audit and shown with that reason in the decision view.
    private static PortfolioBuildOutcome IntradayOutcome(IntradayPortfolioReplayOutcome replay,
        IReadOnlyList<BacktestCandidateTrade> candidates, IReadOnlyList<StrategyTickerBacktestBatch> batches,
        DateTimeOffset start, IReadOnlyList<BacktestCandidateTrade>? notEligible = null)
    {
        const string NotEligibleReason = "universe_membership_missing";
        var audit = new PortfolioAdmissionAudit();
        foreach (var candidate in notEligible ?? []) audit.Reject(candidate, NotEligibleReason);
        var refused = (notEligible ?? []).ToDictionary(candidate => candidate.CandidateId, _ => NotEligibleReason, StringComparer.Ordinal);
        var byId = candidates.ToDictionary(candidate => candidate.CandidateId, StringComparer.Ordinal);
        foreach (var disposition in replay.Dispositions.Where(item => !item.Accepted))
            if (byId.TryGetValue(disposition.CandidateId, out var candidate)) audit.Reject(candidate, disposition.Reason);
        var state = IntradayDecisionEvidenceProjection.Attach(replay.PortfolioState, candidates, replay, refused);
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

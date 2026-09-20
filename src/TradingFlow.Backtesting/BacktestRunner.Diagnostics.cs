using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Regime;
using TradingFlow.Engine.Risk;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Backtesting;

// Result assembly, diagnostics, and portfolio construction for BacktestRunner: missed-move
// audits, strategy selection, per-strategy portfolio trades (sizing, fees, per-ticker loss
// guard, universe gate), diagnostic reports, and drawdown/daily-return metrics. Split into a
// partial file for readability; behavior is identical to the inline version.
public sealed partial class BacktestRunner
{
    private static IReadOnlyList<MissedMoveAudit> BuildMissedMoveAudits(
        IReadOnlyCollection<StrategyDefinition> strategies,
        IReadOnlyList<BacktestCandidateTrade> candidates,
        IReadOnlyCollection<OhlcvBar> allBars)
    {
        const decimal majorMoveThresholdPct = 8m;
        var strategyNames = strategies
            .Select(strategy => strategy.StrategyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var executionTimeframes = strategies
            .Select(strategy => strategy.Execution.Timeframe)
            .Where(timeframe => !String.IsNullOrWhiteSpace(timeframe))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var audits = new List<MissedMoveAudit>();

        foreach (var group in allBars
            .Where(bar => executionTimeframes.Contains(bar.Timeframe))
            .GroupBy(bar => $"{bar.Ticker.ToUpperInvariant()}|{bar.Timeframe}", StringComparer.OrdinalIgnoreCase))
        {
            var keyParts = group.Key.Split('|', 2);
            var ticker = keyParts[0];
            var timeframe = keyParts.Length > 1 ? keyParts[1] : String.Empty;
            var bars = group
                .OrderBy(bar => bar.Timestamp)
                .ToArray();
            if (bars.Length < 2)
            {
                continue;
            }

            var lookaheadBars = ResolveMissedMoveLookaheadBars(timeframe);
            for (var startIndex = 0; startIndex < bars.Length - 1; startIndex++)
            {
                var startBar = bars[startIndex];
                if (startBar.Close <= 0m)
                {
                    continue;
                }

                var endIndex = Math.Min(bars.Length - 1, startIndex + lookaheadBars);
                var peakIndex = startIndex + 1;
                var peakHigh = bars[peakIndex].High;
                for (var index = startIndex + 2; index <= endIndex; index++)
                {
                    if (bars[index].High > peakHigh)
                    {
                        peakHigh = bars[index].High;
                        peakIndex = index;
                    }
                }

                var movePct = ((peakHigh - startBar.Close) / startBar.Close) * 100m;
                if (movePct < majorMoveThresholdPct)
                {
                    continue;
                }

                var peakTimestamp = bars[peakIndex].Timestamp;
                var entries = candidates
                    .Where(candidate =>
                        candidate.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                        candidate.EntryTimestamp >= startBar.Timestamp &&
                        candidate.EntryTimestamp <= peakTimestamp)
                    .Select(candidate => candidate.StrategyName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var missing = strategyNames
                    .Where(strategy => !entries.Contains(strategy, StringComparer.OrdinalIgnoreCase))
                    .ToArray();

                audits.Add(new MissedMoveAudit(
                    ticker,
                    timeframe,
                    startBar.Timestamp,
                    peakTimestamp,
                    Decimal.Round(startBar.Close, 4),
                    Decimal.Round(peakHigh, 4),
                    Decimal.Round(movePct, 4),
                    peakIndex - startIndex,
                    entries,
                    missing));

                startIndex = Math.Max(startIndex, peakIndex - 1);
            }
        }

        return audits
            .OrderByDescending(audit => audit.MovePct)
            .ThenBy(audit => audit.Ticker, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
    }

    private static int ResolveMissedMoveLookaheadBars(string timeframe)
    {
        var duration = TimeframeParser.Parse(timeframe);
        if (duration >= TimeSpan.FromDays(1))
        {
            return 20;
        }

        if (duration >= TimeSpan.FromHours(1))
        {
            return 16;
        }

        var bars = (int)Math.Ceiling(TimeSpan.FromHours(4).TotalMinutes / Math.Max(1.0, duration.TotalMinutes));
        return Math.Clamp(bars, 12, 240);
    }
    private static StrategyBacktestResult? SelectBestActiveStrategy(IReadOnlyList<StrategyBacktestResult> strategyResults)
    {
        var activeResults = strategyResults
            .Where(x => x.EconomicResultsComplete && x.AcceptedTradeCount > 0)
            .ToArray();
        if (activeResults.Length == 0)
        {
            return null;
        }

        return activeResults
            .OrderByDescending(x => x.NetProfit)
            .ThenBy(x => x.MaxDrawdownPct)
            .ThenByDescending(x => x.AcceptedTradeCount)
            .First();
    }
    private static IReadOnlyList<StrategyBacktestResult> BuildStrategyResults(
        PortfolioConfig portfolio,
        IReadOnlyCollection<StrategyDefinition> strategies,
        IReadOnlyList<BacktestCandidateTrade> candidates,
        IReadOnlyCollection<OhlcvBar> allBars,
        IReadOnlyCollection<StrategyCandidateDiagnostics> candidateDiagnostics,
        UniverseMembership? universeMembership = null,
        IReadOnlyDictionary<string, RegimeCalendar>? regimeCalendars = null)
    {
        return strategies
            .Select(strategy =>
            {
                var strategyCandidates = candidates
                    .Where(x => x.StrategyName.Equals(strategy.StrategyName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.EntryTimestamp)
                    .ThenBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var regimeCalendar = regimeCalendars is not null && regimeCalendars.TryGetValue(strategy.StrategyId, out var calendar)
                    ? calendar
                    : null;
                var portfolioOutcome = BuildPortfolioTradesWithAudit(
                    portfolio,
                    strategy,
                    strategyCandidates,
                    universeMembership,
                    regimeCalendar,
                    allBars);
                var diagnosticsTarget = candidateDiagnostics.FirstOrDefault(diagnostic =>
                    diagnostic.StrategyId.Equals(strategy.StrategyId, StringComparison.OrdinalIgnoreCase));
                diagnosticsTarget?.MergeRejections(portfolioOutcome.AdmissionAudit);
                var trades = portfolioOutcome.Trades;
                var economicResultsComplete = portfolioOutcome.ExecutionFailures.Count == 0;
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
                    economicResultsComplete ? Decimal.Round(endingCapital, 4) : portfolio.StartingCapital,
                    economicResultsComplete ? Decimal.Round(netProfit, 4) : 0m,
                    economicResultsComplete ? Decimal.Round(totalReturnPct, 4) : 0m,
                    economicResultsComplete ? dailyMetrics.AverageDailyReturnPct : 0m,
                    dailyMetrics.TradingDayCount,
                    economicResultsComplete ? Decimal.Round(maxDrawdownPct, 4) : 0m,
                    strategyCandidates.Length,
                    trades.Count,
                    Math.Max(
                        0,
                        strategyCandidates.Length - trades.Count - portfolioOutcome.ExecutionFailures.Count),
                    economicResultsComplete ? trades.Count(x => x.NetProfit > 0) : 0,
                    economicResultsComplete ? trades.Count(x => x.NetProfit < 0) : 0,
                    economicResultsComplete ? trades : Array.Empty<BacktestTrade>(),
                    portfolioOutcome.ExecutionFailures,
                    economicResultsComplete);
            })
            .ToArray();
    }

    private static UnifiedPortfolioBacktestResult BuildUnifiedPortfolioResult(
        PortfolioConfig portfolio,
        IReadOnlyCollection<StrategyDefinition> strategies,
        IReadOnlyList<BacktestCandidateTrade> candidates,
        IReadOnlyCollection<OhlcvBar> allBars,
        UniverseMembership? universeMembership,
        IReadOnlyDictionary<string, RegimeCalendar>? regimeCalendars,
        IExecutionEventSink? executionEvidence = null)
    {
        var portfolioOutcome = BuildUnifiedPortfolioTradesWithAudit(
            portfolio,
            strategies,
            candidates,
            universeMembership,
            regimeCalendars,
            allBars,
            executionEvidence);
        var trades = portfolioOutcome.Trades;
        var economicResultsComplete = portfolioOutcome.ExecutionFailures.Count == 0;
        var netProfit = trades.Sum(trade => trade.NetProfit);
        var endingCapital = portfolio.StartingCapital + netProfit;
        var totalReturnPct = portfolio.StartingCapital <= 0m
            ? 0m
            : netProfit / portfolio.StartingCapital * 100m;

        return new UnifiedPortfolioBacktestResult(
            portfolio.StartingCapital,
            economicResultsComplete ? Decimal.Round(endingCapital, 4) : portfolio.StartingCapital,
            economicResultsComplete ? Decimal.Round(netProfit, 4) : 0m,
            economicResultsComplete ? Decimal.Round(totalReturnPct, 4) : 0m,
            economicResultsComplete
                ? Decimal.Round(CalculateMaxDrawdown(portfolio.StartingCapital, trades), 4)
                : 0m,
            candidates.Count,
            trades.Count,
            Math.Max(0, candidates.Count - trades.Count - portfolioOutcome.ExecutionFailures.Count),
            economicResultsComplete ? trades.Count(trade => trade.NetProfit > 0m) : 0,
            economicResultsComplete ? trades.Count(trade => trade.NetProfit < 0m) : 0,
            trades,
            portfolioOutcome.AdmissionAudit.RejectionCounts,
            portfolioOutcome.AdmissionAudit.RejectionExamples,
            portfolioOutcome.ExecutionFailures,
            economicResultsComplete);
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
                var rejectionExamples = diagnostics
                    .SelectMany(x => x.RejectionExamples)
                    .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        x => x.Key,
                        x => (IReadOnlyList<string>)x.SelectMany(y => y.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray(),
                        StringComparer.OrdinalIgnoreCase);
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
                    rejectionExamples
                        .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
                    BuildDiagnosticSuggestions(result, rejectionCounts, exitReasonCounts, realizedRewardRiskRatio, winRatePct),
                    result.ExecutionFailures?.Count ?? 0,
                    result.EconomicResultsComplete);
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
        if (!result.EconomicResultsComplete)
        {
            suggestions.Add(
                $"Economic analysis is unavailable because {result.ExecutionFailures?.Count ?? 0} execution(s) ended with unresolved exposure.");
            return suggestions;
        }

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
        StrategyDefinition strategy,
        IReadOnlyList<BacktestCandidateTrade> candidates,
        UniverseMembership? universeMembership = null,
        RegimeCalendar? regimeCalendar = null)
    {
        return BuildPortfolioTradesWithAudit(
            portfolio,
            strategy,
            candidates,
            universeMembership,
            regimeCalendar).Trades;
    }

    private static PortfolioBuildOutcome BuildPortfolioTradesWithAudit(
        PortfolioConfig portfolio,
        StrategyDefinition strategy,
        IReadOnlyList<BacktestCandidateTrade> candidates,
        UniverseMembership? universeMembership = null,
        RegimeCalendar? regimeCalendar = null,
        IReadOnlyCollection<OhlcvBar>? marketBars = null)
    {
        return BuildPortfolioTradesCore(
            portfolio,
            candidates,
            _ => strategy,
            universeMembership,
            _ => regimeCalendar,
            marketBars ?? BuildSyntheticExecutionBars(candidates, _ => strategy));
    }

    private static IReadOnlyList<BacktestTrade> BuildUnifiedPortfolioTrades(
        PortfolioConfig portfolio,
        IReadOnlyCollection<StrategyDefinition> strategies,
        IReadOnlyList<BacktestCandidateTrade> candidates,
        UniverseMembership? universeMembership = null,
        IReadOnlyDictionary<string, RegimeCalendar>? regimeCalendars = null)
    {
        return BuildUnifiedPortfolioTradesWithAudit(
            portfolio,
            strategies,
            candidates,
            universeMembership,
            regimeCalendars).Trades;
    }

    private static PortfolioBuildOutcome BuildUnifiedPortfolioTradesWithAudit(
        PortfolioConfig portfolio,
        IReadOnlyCollection<StrategyDefinition> strategies,
        IReadOnlyList<BacktestCandidateTrade> candidates,
        UniverseMembership? universeMembership = null,
        IReadOnlyDictionary<string, RegimeCalendar>? regimeCalendars = null,
        IReadOnlyCollection<OhlcvBar>? marketBars = null,
        IExecutionEventSink? executionEvidence = null)
    {
        var strategiesById = strategies.ToDictionary(
            strategy => strategy.StrategyId,
            StringComparer.Ordinal);
        return BuildPortfolioTradesCore(
            portfolio,
            candidates,
            candidate => strategiesById.TryGetValue(candidate.StrategyId, out var strategy)
                ? strategy
                : throw new InvalidOperationException(
                    $"Candidate references unknown strategy ID '{candidate.StrategyId}'."),
            universeMembership,
            strategy => regimeCalendars is not null &&
                regimeCalendars.TryGetValue(strategy.StrategyId, out var calendar)
                    ? calendar
                    : null,
            marketBars ?? BuildSyntheticExecutionBars(
                candidates,
                candidate => strategiesById.TryGetValue(candidate.StrategyId, out var strategy)
                    ? strategy
                    : throw new InvalidOperationException(
                        $"Candidate references unknown strategy ID '{candidate.StrategyId}'.")),
            executionEvidence);
    }

    private static PortfolioBuildOutcome BuildPortfolioTradesCore(
        PortfolioConfig portfolio,
        IReadOnlyList<BacktestCandidateTrade> candidates,
        Func<BacktestCandidateTrade, StrategyDefinition> resolveStrategy,
        UniverseMembership? universeMembership,
        Func<StrategyDefinition, RegimeCalendar?> resolveRegimeCalendar,
        IReadOnlyCollection<OhlcvBar> marketBars,
        IExecutionEventSink? executionEvidence = null)
    {
        var admissionAudit = new PortfolioAdmissionAudit();
        var execution = new ChronologicalBacktestExecutionCoordinator(
            portfolio, marketBars, executionEvidence: executionEvidence);

        foreach (var entryGroup in candidates
                     .OrderBy(candidate => candidate.EntryTimestamp.ToUniversalTime())
                     .ThenByDescending(candidate => candidate.SelectionScore)
                     .ThenBy(candidate => candidate.Ticker, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(candidate => candidate.StrategyId, StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.Direction, StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.StopLossPrice)
                     .ThenBy(candidate => candidate.TakeProfitPrice)
                     .ThenBy(candidate => candidate.ExitTimestamp)
                     .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                     .GroupBy(candidate => candidate.EntryTimestamp.ToUniversalTime()))
        {
            execution.AdvanceThrough(entryGroup.Key);
            foreach (var candidate in entryGroup)
            {
                var strategy = resolveStrategy(candidate);
                var regimeCalendar = resolveRegimeCalendar(strategy);
                var entryDay = DateOnly.FromDateTime(candidate.EntryTimestamp.UtcDateTime);

                // Per-day universe gate: a trade is allowed only if its ticker qualified on the
                // entry day. Uses the entry's UTC calendar date, which for US equities is the
                // same trading day the no-lookahead membership was computed against.
                if (universeMembership is not null && !universeMembership.IsMember(candidate.Ticker, entryDay))
                {
                    admissionAudit.Reject(candidate, "universe_membership_missing");
                    continue;
                }

                // Layer-2 regime gate: no new entries on days the market regime is off.
                if (regimeCalendar is not null && !regimeCalendar.IsOn(entryDay))
                {
                    admissionAudit.Reject(candidate, "market_regime_off_on_entry_day");
                    continue;
                }

                var completedTrades = execution.CompletedTrades;
                var equity = portfolio.StartingCapital + completedTrades.Sum(trade => trade.NetProfit);
                var activePositions = execution.ActiveExecutions;

                if (portfolio.PreventOverlappingTickerPositions &&
                    activePositions.Any(position => position.Ticker.Equals(candidate.Ticker, StringComparison.OrdinalIgnoreCase)))
                {
                    admissionAudit.Reject(candidate, "overlapping_ticker_position");
                    continue;
                }

                if (activePositions.Count >= portfolio.MaxConcurrentPositions)
                {
                    admissionAudit.Reject(
                        candidate,
                        "max_concurrent_positions_reached",
                        $"Actual: {activePositions.Count}, Allowed: {portfolio.MaxConcurrentPositions}");
                    continue;
                }

                var slotPositionValue = equity / portfolio.MaxConcurrentPositions;
                var reservedPositionValue = activePositions.Sum(position => position.ReservedNotional);
                var availablePositionValue = Math.Max(0m, equity - reservedPositionValue);
                var requestedNotional = Math.Min(slotPositionValue, availablePositionValue);
                var side = candidate.Direction.Equals("short", StringComparison.OrdinalIgnoreCase)
                    ? PlannedOrderSide.Short
                    : PlannedOrderSide.Long;
                var planningRequest = new OrderPlanningRequest(
                    candidate.Ticker,
                    side,
                    equity,
                    candidate.EntryPrice,
                    PlannedStopRequest.FromResolvedPrice(
                        candidate.InitialStopKind.Equals("atr", StringComparison.OrdinalIgnoreCase)
                            ? PlannedStopKind.Atr
                            : PlannedStopKind.Structural,
                        candidate.StopLossPrice),
                    new OrderPlanningRiskLimits(
                        portfolio.AccountRiskBudgetPct,
                        portfolio.MaxPositionNotionalPct),
                    new EstimatedOrderExecutionCosts(
                        SlippageBps: 0m,
                        FixedEntryFee: portfolio.FixedBuyFee,
                        FixedExitFee: portfolio.FixedSellFee),
                    requestedNotional);
                var orderPlan = new SharedOrderRiskPlanner().Plan(planningRequest);
                if (!orderPlan.IsAccepted || orderPlan.Order is null)
                {
                    admissionAudit.Reject(
                        candidate,
                        $"order_plan_{ToSnakeCase(orderPlan.Rejection?.Code.ToString() ?? "unknown")}",
                        orderPlan.Rejection?.Reason);
                    continue;
                }

                if (!execution.TrySchedule(
                        new ChronologicalExecutionPlan(candidate, strategy, orderPlan.Order.Quantity),
                        out var executionRejection))
                {
                    admissionAudit.Reject(candidate, executionRejection ?? "execution_schedule_rejected");
                    continue;
                }
            }

        }

        execution.Complete();
        foreach (var failure in execution.Failures)
        {
            admissionAudit.Reject(
                failure.Ticker,
                failure.StrategyName,
                failure.EntryTimestamp,
                failure.Reason,
                $"Candidate: {failure.CandidateId}, Open signed quantity: {failure.OpenSignedQuantity}");
        }

        return new PortfolioBuildOutcome(
            execution.CompletedTrades
                .OrderBy(trade => trade.EntryTimestamp)
                .ThenBy(trade => trade.Ticker, StringComparer.OrdinalIgnoreCase)
                .ThenBy(trade => trade.StrategyName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            admissionAudit,
            execution.Failures);
    }

    private static IReadOnlyCollection<OhlcvBar> BuildSyntheticExecutionBars(
        IReadOnlyCollection<BacktestCandidateTrade> candidates,
        Func<BacktestCandidateTrade, StrategyDefinition> resolveStrategy)
    {
        const decimal unconstrainedSyntheticVolume = 1_000_000_000m;
        return candidates
            .SelectMany(candidate =>
            {
                var timeframe = resolveStrategy(candidate).Execution.Timeframe;
                var entryVolume = candidate.EntryLiquidityEvidenceVolume > 0m
                    ? candidate.EntryLiquidityEvidenceVolume
                    : unconstrainedSyntheticVolume;
                return new[]
                {
                    new OhlcvBar(
                        candidate.Ticker,
                        candidate.EntryTimestamp.ToUniversalTime(),
                        timeframe,
                        candidate.EntryPrice,
                        candidate.EntryPrice,
                        candidate.EntryPrice,
                        candidate.EntryPrice,
                        entryVolume),
                    new OhlcvBar(
                        candidate.Ticker,
                        candidate.ExitTimestamp.ToUniversalTime(),
                        timeframe,
                        candidate.ExitPrice,
                        candidate.ExitPrice,
                        candidate.ExitPrice,
                        candidate.ExitPrice,
                        unconstrainedSyntheticVolume)
                };
            })
            .ToArray();
    }

    private static string ToSnakeCase(string value)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var result = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (Char.IsUpper(character) && index > 0)
            {
                result.Append('_');
            }

            result.Append(Char.ToLowerInvariant(character));
        }

        return result.ToString();
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

    private static DateTime ToExchangeTime(DateTimeOffset timestamp, string timezoneId)
    {
        var zone = ResolveTimezone(timezoneId);
        return TimeZoneInfo.ConvertTime(timestamp, zone).DateTime;
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
}

internal sealed record PortfolioBuildOutcome(
    IReadOnlyList<BacktestTrade> Trades,
    PortfolioAdmissionAudit AdmissionAudit,
    IReadOnlyList<BacktestExecutionFailure> ExecutionFailures);

internal sealed class PortfolioAdmissionAudit
{
    private readonly Dictionary<string, int> _rejectionCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _rejectionExamples = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> RejectionCounts => _rejectionCounts;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> RejectionExamples =>
        _rejectionExamples.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value,
            StringComparer.OrdinalIgnoreCase);

    public void Reject(BacktestCandidateTrade candidate, string reason, string? detail = null)
        => Reject(candidate.Ticker, candidate.StrategyName, candidate.EntryTimestamp, reason, detail);

    public void Reject(
        string ticker,
        string strategyName,
        DateTimeOffset entryTimestamp,
        string reason,
        string? detail = null)
    {
        var normalizedReason = $"portfolio_{reason.Trim().ToLowerInvariant()}";
        _rejectionCounts[normalizedReason] = _rejectionCounts.TryGetValue(normalizedReason, out var count)
            ? count + 1
            : 1;

        if (!_rejectionExamples.TryGetValue(normalizedReason, out var examples))
        {
            examples = [];
            _rejectionExamples[normalizedReason] = examples;
        }

        if (examples.Count >= 5)
        {
            return;
        }

        var explanation =
            $"{normalizedReason} (Ticker: {ticker}, Strategy: {strategyName}, " +
            $"Entry: {entryTimestamp:O}" +
            (String.IsNullOrWhiteSpace(detail) ? ")" : $", Detail: {detail})");
        if (!examples.Contains(explanation, StringComparer.OrdinalIgnoreCase))
        {
            examples.Add(explanation);
        }
    }
}

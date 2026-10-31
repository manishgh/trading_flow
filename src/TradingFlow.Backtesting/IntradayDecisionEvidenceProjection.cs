using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Backtesting;

/// <summary>Projects a bounded recent sample from retained intraday hypotheses and actual execution observations.</summary>
public static class IntradayDecisionEvidenceProjection
{
    public const int MaximumDisplayDecisions = 50;

    /// <summary>Copies source levels/times and last observed protection without simulating orders or changing economic state.</summary>
    public static BacktestPortfolioState Attach(BacktestPortfolioState state,
        IReadOnlyList<BacktestCandidateTrade> candidates, IntradayPortfolioReplayOutcome replay)
    {
        var events = replay.Events.ToLookup(item => item.CandidateId, StringComparer.Ordinal);
        var fills = state.Fills.Where(fill => fill.IsEntry).ToLookup(fill => fill.CandidateId, StringComparer.Ordinal);
        var trades = replay.Trades.ToDictionary(trade => trade.CandidateId, StringComparer.Ordinal);
        var details = new List<BacktestIntradayDecisionDetail>();
        foreach (var candidate in candidates.Where(candidate => candidate.IntradayExecution is not null && candidate.ZoneExecution is null)
            .OrderByDescending(candidate => candidate.EntryTimestamp).ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .Take(MaximumDisplayDecisions))
        {
            var intent = candidate.IntradayExecution!;
            var observed = events[candidate.CandidateId].OrderBy(item => item.TimestampUtc).ToArray();
            var latest = observed.LastOrDefault();
            var entries = fills[candidate.CandidateId].ToArray();
            var quantity = entries.Sum(fill => fill.Quantity);
            var closed = trades.GetValueOrDefault(candidate.CandidateId);
            var terminalClock = closed?.ExitTimestamp ?? state.CutoffUtc;
            var initialization = observed.LastOrDefault(item => item.State == "protection_initialized" && item.TimestampUtc <= terminalClock);
            // Opening fills at the decision timestamp precede a newly decided protection change.
            var adjustment = observed.LastOrDefault(item => item.State == "protection_updated" &&
                item.EffectiveAfterUtc is { } effective && effective < terminalClock);
            var protection = adjustment ?? initialization;
            var target = protection?.TargetPrice ?? (quantity == 0 ? candidate.TakeProfitPrice : 0m);
            details.Add(new(candidate.CandidateId, candidate.StrategyId, candidate.Ticker, candidate.Direction,
                intent.EntryPattern, intent.PreviousSessionHigh ?? throw new InvalidDataException("Intraday breakout evidence requires a previous-session high."),
                intent.PreviousSessionLow ?? throw new InvalidDataException("Intraday breakout evidence requires a previous-session low."),
                intent.BreakoutTimestampUtc, intent.ConfirmationTimestampUtc, intent.ActiveAtUtc, intent.ExpiresAtUtc,
                intent.SessionDeadlineUtc, intent.LimitPrice, candidate.StopLossPrice,
                quantity == 0 ? null : entries.Sum(fill => fill.Quantity * fill.Price) / quantity,
                protection?.StopPrice ?? candidate.StopLossPrice, target > 0 ? target : null,
                closed is not null ? "closed" : latest?.State ?? "not_observed",
                closed?.ExitReason ?? latest?.Reason ?? "No replay event was published for this hypothesis.",
                latest?.TimestampUtc, adjustment?.EffectiveAfterUtc,
                intent.EntryOrderMode == IntradayEntryOrderMode.NextBarMarket ? "next_bar_market"
                    : intent.EntryOrderMode == IntradayEntryOrderMode.StopMarket ? "stop_market" : "next_bar_limit",
                intent.EntryOrderMode == IntradayEntryOrderMode.StopMarket ? intent.LimitPrice : intent.BreakoutTriggerPrice,
                intent.OpeningRangeHigh, intent.OpeningRangeLow, intent.OpeningRangeOpen, intent.OpeningRangeClose,
                intent.OpeningRangeCompletedAtUtc, intent.OpeningRelativeVolume, intent.OpeningVolumeBaselineSampleCount,
                intent.MaxFilledParentTradesPerSession, intent.RequireRangeReturnBeforeReentry));
        }
        return state with { IntradayDecisions = details, IntradayDecisionCount = candidates.Count(candidate => candidate.IntradayExecution is not null && candidate.ZoneExecution is null) };
    }
}

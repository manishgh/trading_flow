using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Application.Jobs;

/// <summary>Copies the server accountant's terminal facts into the shared client contract without recalculating P&amp;L.</summary>
public static class RunPortfolioStateProjection
{
    // Publish terminal cash even when marks are unavailable; preserve null valuations and the original reason.
    public static RunPortfolioStateResponse? Project(BacktestPortfolioState? state)
    {
        if (state is null) return null;
        var close = state.Sessions.LastOrDefault();
        return new(state.ValuationBasis, state.CutoffUtc, state.AdmittedOrderCount, state.FilledCandidateCount,
            state.ClosedTradeCount, state.Fills.Count, close?.Cash, close?.RealizedNetProfit, close?.Fees,
            close?.PositionValue, close?.UnrealizedProfit, close?.Equity, close?.GrossExposurePct,
            state.UnavailableReason ?? close?.UnavailableReason ?? (close is null ? "No source-session valuation observation was published." : null),
            state.OpenPositions.Select(position => new RunOpenPosition(position.CandidateId, position.StrategyId,
                position.Ticker, position.SignedQuantity, position.EntryBasis, position.MarkPrice,
                position.MarkTimestampUtc, position.UnrealizedProfit)).ToArray(),
            state.PendingOrders.Select(order => new RunPendingOrder(order.CandidateId, order.StrategyId, order.Ticker,
                order.OrderId, order.Side, order.OrderType, order.RemainingQuantity, order.Status,
                order.ReservedNotional, order.KnownAtUtc)).ToArray(),
            state.PendingExits.Select(exit => new RunPendingExit(exit.CandidateId, exit.StrategyId,
                exit.Ticker, exit.KnownAtUtc, exit.Reason)).ToArray(),
            state.IntradayDecisions.Select(ProjectIntradayDecision).ToArray(), state.IntradayDecisionCount);
    }

    // Preserve each source clock, structural reference and observed status verbatim for both clients.
    private static RunIntradayDecisionDetail ProjectIntradayDecision(BacktestIntradayDecisionDetail item) =>
        new(item.CandidateId, item.StrategyId, item.Ticker, item.Direction, item.EntryMethod,
            item.PreviousSessionHigh, item.PreviousSessionLow, item.BreakoutTimestampUtc, item.ConfirmationTimestampUtc,
            item.ActiveAtUtc, item.ExpiresAtUtc, item.SessionDeadlineUtc, item.LimitPrice, item.FrozenStopPrice,
            item.ActualEntryPrice, item.CurrentStopPrice, item.TargetPrice, item.Status, item.ResolutionReason,
            item.LastObservedAtUtc, item.ProtectionEffectiveAfterUtc, item.EntryOrderMode, item.TriggerPrice,
            item.OpeningRangeHigh, item.OpeningRangeLow, item.OpeningRangeOpen, item.OpeningRangeClose,
            item.OpeningRangeCompletedAtUtc, item.OpeningRelativeVolume, item.OpeningVolumeBaselineSampleCount,
            item.MaxFilledParentTradesPerSession, item.RequireRangeReturnBeforeReentry);

    // Keep observed bars, first eligibility and still-warming statuses exactly as established by the runner.
    public static IReadOnlyList<RunHistoryEligibility> ProjectEligibility(IReadOnlyList<BacktestHistoryEligibility> eligibility) =>
        eligibility.Select(item => new RunHistoryEligibility(item.Ticker, item.StrategyId, item.RequiredBars,
            item.AvailableAtStart, item.AvailableAtCutoff, item.FirstEligibleDecisionUtc, item.Status)).ToArray();
}

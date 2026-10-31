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

    // Copy immutable zone evidence without applying strategy rules or inferring absent fills.
    public static IReadOnlyList<RunZoneDecisionDetail> ProjectZones(IReadOnlyList<BacktestZoneDecisionDetail>? details) =>
        (details ?? []).Select(item => new RunZoneDecisionDetail(item.CandidateId, item.StrategyId, item.Ticker, item.Direction,
            item.Pattern, item.ZoneId, item.ZoneVersion, item.ZoneRole, item.ZoneBottom, item.ZoneTop, item.PivotCount,
            item.PivotBarStartUtc, item.PivotCompletedAtUtc, item.PivotConfirmedAtUtc, item.ZoneKnownAtUtc, item.FlippedAtUtc,
            item.ConfirmationBarStartUtc, item.ConfirmationCompletedAtUtc, item.ConfirmationClose, item.Vwap, item.SlotRelativeVolume,
            item.SlotMedianVolume, item.SlotSampleCount, item.FrozenStopPrice, item.SessionDeadlineUtc, item.Status,
            item.ResolutionReason, item.ActualEntryPrice, item.ActualExitPrice, item.ZonePolicyId, item.EntryCutoffUtc,
            item.FixedTargetPrice, item.Atr, item.TargetZoneId, item.TargetZoneBottom, item.TargetZoneTop,
            ProjectVolumeSpread(item.VolumeSpread), item.AlternativeDecisions?.Select(alternative => new RunZoneAlternativeDecisionDetail(
                alternative.AlternativeIndex, alternative.Direction, alternative.ZoneId, alternative.PlannedQuantity,
                alternative.EstimatedNetRiskDollars, alternative.EstimatedNetRewardDollars, alternative.RewardRisk,
                alternative.Selected, alternative.Reason, ProjectTargetPlan(alternative.TargetPlan))).ToArray(),
            ProjectTargetPlan(item.TargetPlan), item.LongBounceVolumeMode, item.StopBasis, item.PreviousLow,
            item.ConfirmationLow, item.RawStopPrice, item.TickSize, ProjectRetest(item.Retest),
            item.TargetZoneVersion, item.TargetZoneKnownAtUtc)).ToArray();

    // Copy the pre-flip resistance and active start-snapshot version separately from the frozen source on the main row.
    private static RunZoneRetestDetail? ProjectRetest(TradingFlow.Domain.Strategies.IntradayZoneRetestEvidence? item) =>
        item is null ? null : new(item.ArmTransition.Before.Version, item.ArmTransition.Before.Bottom,
            item.ArmTransition.Before.Top, item.ArmTransition.Before.KnownAtUtc, item.ArmTransition.FlippedAtUtc,
            item.ArmTransition.Atr, item.ArmTransition.CanArmRetest, item.ActiveSourceAtConfirmationStart.Version,
            item.ActiveSourceAtConfirmationStart.Bottom, item.ActiveSourceAtConfirmationStart.Top,
            item.ActiveSourceAtConfirmationStart.KnownAtUtc, item.ConfirmationSnapshotAsOfUtc);

    // Keep sizing budget and the separate full-cost stop denominator explicit.
    private static RunZoneTargetPlanningDetail? ProjectTargetPlan(TradingFlow.Domain.Strategies.IntradayZoneTargetPlanningEvidence? item) =>
        item is null ? null : new(item.TargetOrigin, item.FallbackNetRiskMultiple, item.PlannedQuantity,
            item.PlannerLossDollars, item.PlannerRiskBudgetDollars, item.EstimatedEntryFillPrice, item.EstimatedStopFillPrice,
            item.EstimatedEntryFees, item.EstimatedStopExitFees, item.EstimatedTargetFees, item.EstimatedNetStopRisk,
            item.EstimatedNetReward, item.EstimatedNetRewardRisk, item.AdverseCostBps, item.ResolvedTargetPrice);

    // Preserve null admission values rather than inventing quantities or ratios for rejected or untested alternatives.
    private static RunZoneVolumeSpreadDetail? ProjectVolumeSpread(BacktestZoneVolumeSpreadDetail? item) => item is null ? null
        : new(item.BidPrice, item.AskPrice, item.FullSpreadBps, item.MaximumSpreadBps, item.MinimumRewardRisk,
            item.OrderedAlternativeIndex, item.PlannedQuantity, item.EstimatedRoundTripTicketFees,
            item.EstimatedNetRiskDollars, item.EstimatedNetRewardDollars, item.SelectedRewardRisk);

    // Keep observed bars, first eligibility and still-warming statuses exactly as established by the runner.
    public static IReadOnlyList<RunHistoryEligibility> ProjectEligibility(IReadOnlyList<BacktestHistoryEligibility> eligibility) =>
        eligibility.Select(item => new RunHistoryEligibility(item.Ticker, item.StrategyId, item.RequiredBars,
            item.AvailableAtStart, item.AvailableAtCutoff, item.FirstEligibleDecisionUtc, item.Status)).ToArray();
}

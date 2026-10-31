using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Strategies;

namespace TradingFlow.Backtesting;

public static partial class IntradayPortfolioExecution
{
    private sealed partial class Book
    {
        /// <summary>Admit the validated fifteen-minute zone recipe while preserving every legacy five-minute setup requirement.</summary>
        private static bool ValidStrategyClock(StrategyDefinition strategy)
        {
            if (strategy.EntryRules.IntradayZone is not { IsPythonFixedTargetComparison: true } rules)
                return strategy.Timeframe == "5m";
            if (strategy.Timeframe != "15m") return false;
            try
            {
                rules.Validate(strategy);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>Keep the inclusive zone entry cutoff distinct from the later same-session liquidation opening.</summary>
        private bool ValidZoneOrderClock(BacktestCandidateTrade candidate, DateTimeOffset clock,
            DateTimeOffset deadline)
        {
            var e = candidate.IntradayExecution!;
            var signal = candidate.ZoneExecution;
            var rules = strategies[candidate.StrategyId].EntryRules.IntradayZone;
            if (rules?.IsPythonFixedTargetComparison != true) return false;
            var cutoffMinutes = rules.PythonComparison?.EntryCutoffMinutesBeforeClose ?? rules.PythonVolumeSpreadComparison!.EntryCutoffMinutesBeforeClose;
            var session = request.Sessions[Date(clock)];
            return signal is not null && e.EntryOrderMode == IntradayEntryOrderMode.NextBarMarket &&
                e.ActiveAtUtc == clock && candidate.EntryTimestamp == clock && signal.ActiveAtUtc == clock &&
                e.SessionDeadlineUtc == deadline && signal.SessionDeadlineUtc == deadline &&
                e.EntryCutoffUtc is { } cutoff && cutoff == deadline.AddMinutes(5 - cutoffMinutes) &&
                signal.EntryCutoffUtc == cutoff && e.ExpiresAtUtc == cutoff && clock <= cutoff &&
                e.TickSize > 0 && signal.TickSize == e.TickSize &&
                signal.ConfirmationBar.Timestamp.AddMinutes(5) == clock &&
                e.ConfirmationTimestampUtc == clock && signal.Evidence.ConfirmationCompletedAtUtc == clock &&
                e.BreakoutTimestampUtc == signal.ConfirmationBar.Timestamp &&
                signal.Evidence.SessionOpenUtc == At(session.TradeDate, session.RegularOpen) &&
                signal.Evidence.SessionCloseUtc == At(session.TradeDate, session.RegularClose) &&
                signal.Evidence.PreviousBar?.Timestamp >= signal.Evidence.SessionOpenUtc &&
                e.PreviousSessionHigh is null && e.PreviousSessionLow is null &&
                e.OpeningRangeHigh is null && e.OpeningRangeLow is null && e.OpeningRangeCompletedAtUtc is null &&
                e.MaxFilledParentTradesPerSession == rules.MaxFilledParentTradesPerSession &&
                signal.MaxFilledParentTradesPerSession == rules.MaxFilledParentTradesPerSession &&
                !e.RequireRangeReturnBeforeReentry;
        }

        /// <summary>Cross-check completed same-source candles and shared stop geometry before any executable opening is read.</summary>
        private bool ValidCompletedZoneSignal(BacktestCandidateTrade candidate, DateTimeOffset clock)
        {
            var signal = candidate.ZoneExecution!;
            var e = candidate.IntradayExecution!;
            var source = signal.ConfirmationBar;
            var zone = signal.Evidence;
            var rules = strategies[candidate.StrategyId].EntryRules.IntradayZone!;
            var minimumPivots = rules.PythonComparison?.MinimumPivotCount ?? rules.PythonVolumeSpreadComparison!.MinimumPivotCount;
            var stopAtrMultiple = rules.PythonComparison?.StopAtrMultiple ?? rules.PythonVolumeSpreadComparison!.StopAtrMultiple;
            var retestLong = candidate.Direction == "long" && rules.PythonVolumeSpreadComparison?.UsesBreakoutRetestEntry == true;
            if (!bars.TryGetValue((candidate.Ticker.ToUpperInvariant(), source.Timestamp), out var observed) ||
                !ValidBar(observed) || observed.KnownAtUtc > clock || observed != source ||
                signal.Direction != candidate.Direction || signal.Pattern != e.EntryPattern ||
                signal.Pattern != (candidate.Direction == "long" ? retestLong ? "breakout_retest" : "python_support_bounce" : "python_zone_breakdown") ||
                signal.Evidence.PolicyId != candidate.ExecutionReplay?.PolicyId ||
                signal.EntryReferencePrice != observed.Close || candidate.EntryPrice != observed.Close || e.LimitPrice != observed.Close ||
                zone.Zone.Bottom <= 0 || zone.Zone.Top < zone.Zone.Bottom || zone.Zone.PivotCount < minimumPivots || zone.Zone.KnownAtUtc > source.Timestamp ||
                zone.PreviousBar is not { } previous || !ValidBar(previous) || previous.Timeframe != "5m" ||
                !previous.Ticker.Equals(candidate.Ticker, StringComparison.OrdinalIgnoreCase) ||
                previous.DataFeed != source.DataFeed || previous.AdjustmentPolicy != source.AdjustmentPolicy ||
                previous.Timestamp.AddMinutes(5) != source.Timestamp || previous.KnownAtUtc > source.Timestamp ||
                !bars.TryGetValue((candidate.Ticker.ToUpperInvariant(), previous.Timestamp), out var observedPrevious) || observedPrevious != previous ||
                zone.Atr is not > 0 || zone.RawStopPrice is not { } rawStop ||
                !rules.IsPythonFixedTargetComparison)
                return false;
            if (retestLong)
            {
                if (zone.Retest is not { } retest || zone.SessionOpenUtc is not { } sessionOpen || zone.SessionCloseUtc is not { } sessionClose ||
                    zone.FlippedAtUtc != retest.ArmTransition.FlippedAtUtc || zone.TargetZone?.ZoneId == zone.Zone.ZoneId ||
                    zone.TargetZone?.KnownAtUtc > retest.ConfirmationSnapshotAsOfUtc ||
                    !IntradayZoneRetestEvidenceValidator.MatchesArm(retest, zone.Zone, source, sessionOpen, sessionClose, rules) ||
                    !IntradayZoneRetestEvidenceValidator.Qualifies(zone.Zone, source)) return false;
            }
            else if (zone.Retest is not null) return false;
            var isLong = candidate.Direction == "long";
            var longStopBasis = rules.PythonVolumeSpreadComparison?.LongStopBasis;
            var expectedBasis = IntradayZoneStopGeometry.Basis(isLong, longStopBasis);
            if (zone.StopBasis != expectedBasis && (longStopBasis is not null || zone.StopBasis is not null)) return false;
            var (expectedRawStop, stop) = IntradayZoneStopGeometry.Calculate(isLong, zone.Zone, previous, source,
                zone.Atr.GetValueOrDefault(), e.TickSize, stopAtrMultiple, longStopBasis);
            var reference = ExecutionReplayPolicy.IsPythonZoneVolumeSpread(candidate.ExecutionReplay?.PolicyId) && signal.Evidence.VolumeSpread is { } spread
                ? isLong ? spread.AskPrice : spread.BidPrice : observed.Close;
            return stop > 0 && signal.InitialStopPrice == stop && candidate.StopLossPrice == stop &&
                (isLong ? stop < reference : stop > reference) && rawStop == expectedRawStop &&
                ValidFrozenTarget(candidate, rules, reference, minimumPivots);
        }

        /// <summary>Require genuine known-zone geometry, or an explicitly unresolved risk target under the approved new target package.</summary>
        private static bool ValidFrozenTarget(BacktestCandidateTrade candidate, IntradayZoneRules rules, decimal reference, int minimumPivots)
        {
            var signal = candidate.ZoneExecution!;
            var e = signal.Evidence;
            var fullCost = rules.PythonVolumeSpreadComparison?.UsesFullCostTargets == true;
            if (fullCost && (e.TargetPlan is not { } plan || plan.FallbackNetRiskMultiple != rules.PythonVolumeSpreadComparison!.FallbackNetRiskMultiple ||
                plan.TargetOrigin != (e.TargetZone is null ? "risk_derived" : "known_zone"))) return false;
            if (!fullCost && e.TargetPlan is not null) return false;
            if (e.TargetZone is null)
                return fullCost && e.RawTargetPrice is null && signal.FixedTargetPrice is null && candidate.TakeProfitPrice == 0m;
            var targetZone = e.TargetZone;
            if (targetZone.PivotCount < minimumPivots || targetZone.Bottom <= 0 || targetZone.Top < targetZone.Bottom ||
                targetZone.KnownAtUtc > signal.ConfirmationBar.Timestamp || e.RawTargetPrice is not { } raw ||
                signal.FixedTargetPrice is not { } fixedTarget) return false;
            var isLong = candidate.Direction == "long";
            var rounded = (isLong ? decimal.Floor(raw / signal.TickSize) : decimal.Ceiling(raw / signal.TickSize)) * signal.TickSize;
            return rounded > 0 && fixedTarget == rounded && candidate.TakeProfitPrice == rounded &&
                raw == (isLong ? targetZone.Bottom : targetZone.Top) && (isLong ? rounded > reference : rounded < reference);
        }

        /// <summary>Retain fixed zone protection across partial fills and reduce actual gap exposure that invalidates either side.</summary>
        private void InitializeFixedZoneProtection(Intent intent, DateTimeOffset at)
        {
            intent.InitialTarget = intent.Candidate.TakeProfitPrice;
            var stopCrossed = intent.Long ? intent.LastEntryFillPrice <= intent.Stop : intent.LastEntryFillPrice >= intent.Stop;
            var targetNotFavorable = intent.Long ? intent.LastEntryFillPrice >= intent.Target : intent.LastEntryFillPrice <= intent.Target;
            var notionalBreached = intent.EntryNotional > intent.MaximumNotional + 0.000001m;
            var reason = stopCrossed ? "gap_fill_through_structural_stop"
                : targetNotFavorable ? "actual_fill_zone_target_not_favorable"
                : notionalBreached ? "actual_fill_notional_limit_breach" : null;
            if (reason is not null)
            {
                intent.StopTriggered = true;
                intent.StopTriggeredAt = at;
                intent.ExitReason = reason;
                CancelRemainder(intent, at, reason);
                Emit(intent, at, "risk_reduction_pending", reason, intent.Open);
            }
            Emit(intent, at, "protection_initialized", "fixed_zone_stop_and_target", intent.Entered, intent.Entry);
        }
    }
}

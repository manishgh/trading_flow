using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Backtesting;

public static partial class IntradayPortfolioExecution
{
    private sealed partial class Book
    {
        /// <summary>Apply one half-spread on each adverse planner side, matching the simulator's midpoint-based price model.</summary>
        private static decimal PlanningAdverseCostBps(BacktestCandidateTrade candidate, StrategyDefinition strategy) =>
            strategy.Execution.SlippageBps + SyntheticSpreadBps(candidate, strategy) / 2m;

        /// <summary>Only this explicit comparison supplies synthetic spread; previous execution policies retain zero spread.</summary>
        private static decimal SyntheticSpreadBps(BacktestCandidateTrade candidate, StrategyDefinition strategy) =>
            ExecutionReplayPolicy.IsPythonZoneVolumeSpread(candidate.ExecutionReplay?.PolicyId)
                ? strategy.EntryRules.IntradayZone!.PythonVolumeSpreadComparison!.SyntheticSpreadBps : 0m;

        /// <summary>Keep intermediate alternative sizing failures separate from the group's single admission disposition.</summary>
        private void RejectPlan(BacktestCandidateTrade candidate, string reason, bool recordDisposition)
        {
            if (recordDisposition) Reject(candidate, reason);
            else events.Add(new(candidate.CandidateId, candidate.Ticker, candidate.EntryTimestamp,
                "alternative_sizing_rejected", reason));
        }

        /// <summary>Validate all frozen alternatives before sizing, including their source clocks, quote assumptions and literal order.</summary>
        private bool ValidVolumeSpreadGroup(BacktestCandidateTrade candidate, DateTimeOffset clock)
        {
            if (candidate.ZoneAlternatives is not { Count: > 0 } alternatives || candidate.ZoneExecution != alternatives[0] ||
                !ValidCompletedZoneSignal(candidate, clock)) return false;
            var previousBottom = decimal.MinValue;
            for (var index = 0; index < alternatives.Count; index++)
            {
                var signal = alternatives[index];
                var alternative = AlternativeCandidate(candidate, signal);
                if (signal.Direction is not ("long" or "short") || signal.ConfirmationBar != alternatives[0].ConfirmationBar ||
                    signal.Evidence.PreviousBar != alternatives[0].Evidence.PreviousBar ||
                    signal.Evidence.Zone.Bottom < previousBottom || signal.Evidence.VolumeSpread?.OrderedAlternativeIndex != index ||
                    !ValidZoneOrderClock(alternative, clock, candidate.IntradayExecution!.SessionDeadlineUtc) ||
                    !ValidCompletedZoneSignal(alternative, clock) || !ValidVolumeSpreadTerms(alternative)) return false;
                previousBottom = signal.Evidence.Zone.Bottom;
            }
            return true;
        }

        /// <summary>Bind the synthetic quote, completed volume and asymmetric pattern evidence to the frozen recipe without adding VWAP gates.</summary>
        private bool ValidVolumeSpreadTerms(BacktestCandidateTrade candidate)
        {
            var signal = candidate.ZoneExecution!;
            var e = signal.Evidence;
            var rules = strategies[candidate.StrategyId].EntryRules.IntradayZone!.PythonVolumeSpreadComparison!;
            if (e.VolumeSpread is not { } spread || e.PreviousBar is not { } previous ||
                spread.FullSpreadBps != rules.SyntheticSpreadBps || spread.MaximumSpreadBps != rules.MaximumSpreadBps ||
                spread.MinimumRewardRisk != rules.MinimumRewardRisk || spread.FullSpreadBps > spread.MaximumSpreadBps ||
                e.LongBounceVolumeMode != rules.LongBounceVolumeMode ||
                spread.BidPrice != signal.ConfirmationBar.Close * (1m - spread.FullSpreadBps / 20000m) ||
                spread.AskPrice != signal.ConfirmationBar.Close * (1m + spread.FullSpreadBps / 20000m) ||
                spread.BidPrice <= 0 || spread.AskPrice < spread.BidPrice ||
                e.SlotMedianVolume is not > 0 || e.SlotRelativeVolume is not { } relativeVolume ||
                (relativeVolume < rules.MinimumRelativeVolume && (signal.Direction != "long" || !rules.LongVolumeIsDiagnostic)) ||
                relativeVolume != signal.ConfirmationBar.Volume / e.SlotMedianVolume ||
                e.SlotRelativeVolumeSampleCount != MarketEvidenceProfile.DefaultLookbackSessions ||
                e.MarketEvidenceProfileVersion != MarketEvidenceProfile.ProductionVersion) return false;
            var bar = signal.ConfirmationBar;
            return signal.Direction == "long"
                ? e.Zone.Role == IntradayZoneRole.Support && (rules.UsesBreakoutRetestEntry
                    ? TradingFlow.Engine.Strategies.IntradayZoneRetestEvidenceValidator.Qualifies(e.Zone, bar)
                    : previous.Low <= e.Zone.Top + rules.LongTouchDollarTolerance && bar.Close > e.Zone.Top && bar.Close > bar.Open)
                : previous.Close >= e.Zone.Bottom && bar.Close < e.Zone.Bottom && bar.Close < bar.Open;
        }

        /// <summary>Size frozen alternatives against known capital; zero disables ratio filtering but retains positive risk and audited fee calculations.</summary>
        private Intent? PlanVolumeSpreadGroup(BacktestCandidateTrade candidate)
        {
            foreach (var signal in candidate.ZoneAlternatives!)
            {
                var alternative = AlternativeCandidate(candidate, signal);
                var planned = PlanIntent(alternative, recordDisposition: false);
                if (planned is null)
                {
                    RecordAlternative(alternative, null, false, "shared_risk_plan_rejected");
                    continue;
                }
                if (planned.Strategy.EntryRules.IntradayZone!.PythonVolumeSpreadComparison!.UsesFullCostTargets)
                {
                    var resolved = ResolveFullCostTarget(planned);
                    if (resolved is not null) return resolved;
                    continue;
                }
                var evidence = EstimateVolumeSpreadRatio(planned);
                var filterEnabled = evidence.MinimumRewardRisk > 0m;
                var accepted = evidence.EstimatedNetRiskDollars > 0 && (!filterEnabled || evidence.SelectedRewardRisk >= evidence.MinimumRewardRisk);
                var selectedReason = filterEnabled ? "selected_after_ticket_fee_reward_risk" : "selected_with_minimum_reward_risk_filter_disabled";
                RecordAlternative(alternative, evidence, accepted, accepted ? selectedReason : "estimated_reward_risk_below_minimum");
                if (!accepted) continue;
                var enrichedSignal = signal with { Evidence = signal.Evidence with { VolumeSpread = evidence } };
                var selected = AlternativeCandidate(candidate, enrichedSignal);
                return new(selected, planned.Strategy, planned.Requested, planned.MaximumNotional, planned.ReservationPrice,
                    planned.PlannerLossDollars, planned.PlannerRiskBudgetDollars);
            }
            var parameters = strategies[candidate.StrategyId].EntryRules.IntradayZone!.PythonVolumeSpreadComparison!;
            Reject(candidate, parameters.UsesFullCostTargets ? "no_ordered_alternative_passed_sizing_and_target_costs"
                : parameters.MinimumRewardRisk == 0m ? "no_ordered_alternative_passed_sizing_and_positive_risk"
                : "no_ordered_alternative_passed_sizing_and_reward_risk");
            return null;
        }

        /// <summary>Calculate the supplied ticket-fee ratio from capped shares and synthetic ask/bid, excluding costs the file omits.</summary>
        private PythonZoneVolumeSpreadEvidence EstimateVolumeSpreadRatio(Intent intent)
        {
            var e = intent.Candidate.ZoneExecution!.Evidence.VolumeSpread!;
            var entryQuote = intent.Long ? e.AskPrice : e.BidPrice;
            var fees = request.Portfolio.FixedBuyFee + request.Portfolio.FixedSellFee;
            var risk = (intent.Long ? entryQuote - intent.Stop : intent.Stop - entryQuote) * intent.Requested + fees;
            var reward = (intent.Long ? intent.Candidate.TakeProfitPrice - entryQuote : entryQuote - intent.Candidate.TakeProfitPrice) * intent.Requested - fees;
            return e with { PlannedQuantity = intent.Requested, EstimatedRoundTripTicketFees = fees,
                EstimatedNetRiskDollars = risk, EstimatedNetRewardDollars = reward, SelectedRewardRisk = risk > 0 ? reward / risk : null };
        }

        /// <summary>Publish each attempted alternative without creating a second order or mutating portfolio capital.</summary>
        private void RecordAlternative(BacktestCandidateTrade alternative, PythonZoneVolumeSpreadEvidence? evidence, bool selected, string reason)
        {
            var signal = alternative.ZoneExecution!;
            alternativeDecisions.Add(new(alternative.CandidateId, signal.Evidence.VolumeSpread!.OrderedAlternativeIndex,
                alternative.Direction, signal.Evidence.Zone.ZoneId, evidence?.PlannedQuantity,
                evidence?.EstimatedNetRiskDollars, evidence?.EstimatedNetRewardDollars, evidence?.SelectedRewardRisk, selected, reason));
        }

        /// <summary>Retain group identity and all frozen alternatives while substituting the alternative actually being evaluated or selected.</summary>
        private static BacktestCandidateTrade AlternativeCandidate(BacktestCandidateTrade candidate, IntradayZoneSignal signal) =>
            candidate with { Direction = signal.Direction, EntryPrice = signal.EntryReferencePrice, StopLossPrice = signal.InitialStopPrice,
                TakeProfitPrice = signal.FixedTargetPrice ?? 0m, StopDistance = Math.Abs(signal.EntryReferencePrice - signal.InitialStopPrice),
                ZoneExecution = signal, IntradayExecution = candidate.IntradayExecution! with {
                    LimitPrice = signal.EntryReferencePrice, TickSize = signal.TickSize, EntryPattern = signal.Pattern } };
    }
}

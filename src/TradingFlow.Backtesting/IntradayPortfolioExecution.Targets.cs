using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Risk;

namespace TradingFlow.Backtesting;

public static partial class IntradayPortfolioExecution
{
    private sealed partial class Book
    {
        /// <summary>Resolve the approved target package at the already-capped quantity, then freeze all estimates before matching an opening.</summary>
        private Intent? ResolveFullCostTarget(Intent planned)
        {
            var signal = planned.Candidate.ZoneExecution!;
            var p = request.Portfolio;
            var rules = planned.Strategy.EntryRules.IntradayZone!.PythonVolumeSpreadComparison!;
            var adverseBps = PlanningAdverseCostBps(planned.Candidate, planned.Strategy) + p.ImpactBpsAtMaxParticipation;
            var result = FixedTargetCostPlanner.Plan(new(planned.Long, planned.Requested, signal.EntryReferencePrice,
                signal.InitialStopPrice, signal.TickSize, adverseBps, p.FixedBuyFee, p.FixedSellFee,
                p.SecFeeRate, p.FinraTafPerShare, p.FinraTafCap, rules.FallbackNetRiskMultiple!.Value, signal.FixedTargetPrice));
            var targetPlan = new IntradayZoneTargetPlanningEvidence(signal.Evidence.TargetZone is null ? "risk_derived" : "known_zone",
                rules.FallbackNetRiskMultiple, planned.Requested, planned.PlannerLossDollars, planned.PlannerRiskBudgetDollars,
                result.EntryFillPrice, result.StopFillPrice, result.EntryFees, result.StopExitFees, result.TargetExitFees,
                result.NetStopRisk, result.NetReward, result.NetRewardRisk, adverseBps, result.TargetPrice);
            alternativeDecisions.Add(new(planned.Candidate.CandidateId, signal.Evidence.VolumeSpread!.OrderedAlternativeIndex,
                planned.Candidate.Direction, signal.Evidence.Zone.ZoneId, planned.Requested, result.NetStopRisk, result.NetReward,
                result.NetRewardRisk, result.Accepted, result.Reason, targetPlan));
            if (!result.Accepted || result.TargetPrice is not { } target) return null;
            var resolvedSignal = signal with { FixedTargetPrice = target, Evidence = signal.Evidence with { TargetPlan = targetPlan } };
            var resolved = CopyPlannedIntent(planned, resolvedSignal);
            var ticketAudit = EstimateVolumeSpreadRatio(resolved);
            return CopyPlannedIntent(planned, resolvedSignal with { Evidence = resolvedSignal.Evidence with { VolumeSpread = ticketAudit } });
        }

        /// <summary>Copy an unfilled plan after target resolution without recalculating quantity or changing any live execution state.</summary>
        private static Intent CopyPlannedIntent(Intent source, IntradayZoneSignal signal) =>
            new(AlternativeCandidate(source.Candidate, signal), source.Strategy, source.Requested, source.MaximumNotional,
                source.ReservationPrice, source.PlannerLossDollars, source.PlannerRiskBudgetDollars);
    }
}

namespace TradingFlow.Engine.Risk;

/// <summary>Quantity is supplied by the unchanged risk planner; this contract only resolves a fixed target and estimates its costs.</summary>
public sealed record FixedTargetCostRequest(bool IsLong, int Quantity, decimal Midpoint, decimal StopPrice, decimal TickSize,
    decimal AdverseCostBps, decimal FixedBuyFee, decimal FixedSellFee, decimal SecFeeRate, decimal FinraTafPerShare,
    decimal FinraTafCap, decimal FallbackNetRiskMultiple, decimal? KnownZoneTarget = null);

public sealed record FixedTargetCostResult(bool Accepted, string Reason, decimal? TargetPrice, decimal EntryFillPrice,
    decimal StopFillPrice, decimal EntryFees, decimal StopExitFees, decimal? TargetExitFees,
    decimal NetStopRisk, decimal? NetReward, decimal? NetRewardRisk);

/// <summary>Resolve a known-zone cost floor or nearest legal net-risk target without reading any future market evidence or resizing shares.</summary>
public static class FixedTargetCostPlanner
{
    /// <summary>Estimate prospective adverse entry/stop fills, then preserve an existing zone target or solve only a genuinely absent target.</summary>
    public static FixedTargetCostResult Plan(FixedTargetCostRequest request)
    {
        if (request.Quantity <= 0 || request.Midpoint <= 0 || request.StopPrice <= 0 || request.TickSize <= 0 ||
            request.AdverseCostBps is < 0 or >= 10000 || request.FallbackNetRiskMultiple <= 0 ||
            request.FixedBuyFee < 0 || request.FixedSellFee < 0 || request.SecFeeRate < 0 || request.FinraTafPerShare < 0 || request.FinraTafCap < 0)
            return new(false, "target_cost_inputs_invalid", null, 0, 0, 0, 0, null, 0, null, null);
        var entrySide = request.IsLong ? SimulatedOrderSide.Buy : SimulatedOrderSide.Sell;
        var exitSide = request.IsLong ? SimulatedOrderSide.Sell : SimulatedOrderSide.Buy;
        var entry = DeterministicExecutionSimulator.EstimateAdverseFillPrice(request.Midpoint, entrySide, request.AdverseCostBps);
        var stop = DeterministicExecutionSimulator.EstimateAdverseFillPrice(request.StopPrice, exitSide, request.AdverseCostBps);
        var entryFees = Fees(request, entry, entrySide);
        var stopFees = Fees(request, stop, exitSide);
        var loss = request.Quantity * (request.IsLong ? entry - stop : stop - entry) + entryFees + stopFees;
        var basis = new FixedTargetCostResult(false, "estimated_stop_risk_not_positive", null, entry, stop, entryFees, stopFees, null, loss, null, null);
        if (entry <= 0 || stop <= 0 || loss <= 0) return basis;
        try
        {
            if (request.KnownZoneTarget is { } known)
            {
                var estimate = AtTarget(request, basis, known);
                return estimate with { Accepted = estimate.NetReward > 0,
                    Reason = estimate.NetReward > 0 ? "known_zone_target_covers_costs" : "target_does_not_cover_costs" };
            }
            var target = SolveRiskTarget(request, basis);
            return target is { } price ? AtTarget(request, basis, price) with { Accepted = true, Reason = "risk_derived_target_resolved" }
                : basis with { Reason = "risk_target_no_finite_positive_legal_price" };
        }
        catch (OverflowException)
        {
            return basis with { Reason = "risk_target_no_finite_positive_legal_price" };
        }
    }

    /// <summary>Use the shared fee schedule on the appropriate transaction side, including capped TAF on full planned shares.</summary>
    private static decimal Fees(FixedTargetCostRequest r, decimal price, SimulatedOrderSide side) =>
        DeterministicExecutionSimulator.EstimateExecutionFees(price, r.Quantity, side,
            r.FixedBuyFee, r.FixedSellFee, r.SecFeeRate, r.FinraTafPerShare, r.FinraTafCap);

    /// <summary>Assume a target limit fills exactly at its legal order price, preserving the simulator's no-worse-than-limit constraint.</summary>
    private static FixedTargetCostResult AtTarget(FixedTargetCostRequest r, FixedTargetCostResult basis, decimal price)
    {
        if (price <= 0 || price % r.TickSize != 0) return basis with { Reason = "target_price_not_positive_legal_tick" };
        var exitFees = Fees(r, price, r.IsLong ? SimulatedOrderSide.Sell : SimulatedOrderSide.Buy);
        var reward = r.Quantity * (r.IsLong ? price - basis.EntryFillPrice : basis.EntryFillPrice - price) - basis.EntryFees - exitFees;
        return basis with { TargetPrice = price, TargetExitFees = exitFees, NetReward = reward, NetRewardRisk = reward / basis.NetStopRisk };
    }

    /// <summary>Binary-search legal ticks for the nearest favourable price reaching the declared full-cost multiple; short targets cannot cross zero.</summary>
    private static decimal? SolveRiskTarget(FixedTargetCostRequest r, FixedTargetCostResult basis)
    {
        var goal = r.FallbackNetRiskMultiple * basis.NetStopRisk;
        if (r.IsLong && r.SecFeeRate >= 1m) return null;
        var entryTicks = decimal.Floor(basis.EntryFillPrice / r.TickSize);
        if (r.IsLong)
        {
            var low = entryTicks;
            var high = Math.Max(low + 1, low * 2);
            for (var attempts = 0; !MeetsGoal(r, basis, high, goal); attempts++)
            {
                if (attempts >= 96) return null;
                high *= 2;
            }
            while (high - low > 1)
            {
                var middle = decimal.Floor(low + (high - low) / 2);
                if (MeetsGoal(r, basis, middle, goal)) high = middle;
                else low = middle;
            }
            return high * r.TickSize;
        }
        if (entryTicks < 1 || !MeetsGoal(r, basis, 1, goal)) return null;
        var lower = 1m;
        var upper = entryTicks + 1;
        while (upper - lower > 1)
        {
            var middle = decimal.Floor(lower + (upper - lower) / 2);
            if (MeetsGoal(r, basis, middle, goal)) lower = middle;
            else upper = middle;
        }
        return lower * r.TickSize;
    }

    /// <summary>Evaluate one legal prospective price without changing quantity, order state or any target estimate already published.</summary>
    private static bool MeetsGoal(FixedTargetCostRequest r, FixedTargetCostResult basis, decimal ticks, decimal goal) =>
        AtTarget(r, basis, ticks * r.TickSize).NetReward >= goal;
}

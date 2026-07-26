using System.Globalization;

namespace TradingFlow.Engine.Risk;

/// <summary>
/// Builds a side-neutral order plan. The strategy owns the initial stop; this planner
/// preserves that stop and sizes quantity to the account-risk and position-notional caps.
/// </summary>
public sealed class SharedOrderRiskPlanner
{
    private const decimal BasisPointsDivisor = 10_000m;

    public OrderPlanningResult Plan(OrderPlanningRequest request)
    {
        var validationFailure = ValidateRequest(request);
        if (validationFailure is not null)
        {
            return validationFailure;
        }

        var slippageRate = request.ExecutionCosts.SlippageBps / BasisPointsDivisor;
        var entryPrice = request.Side == PlannedOrderSide.Long
            ? request.ReferenceEntryPrice * (1m + slippageRate)
            : request.ReferenceEntryPrice * (1m - slippageRate);

        if (entryPrice <= 0m)
        {
            return Reject(
                OrderPlanRejectionCode.InvalidReferenceEntryPrice,
                "Adverse entry slippage produces a non-positive estimated entry price.");
        }

        var stopResult = ResolveStop(request, entryPrice, slippageRate);
        if (stopResult.Rejection is not null)
        {
            return stopResult.Rejection;
        }

        var stopTriggerPrice = stopResult.StopTriggerPrice;
        var estimatedStopFillPrice = stopResult.EstimatedStopFillPrice;
        var priceLossPerShare = request.Side == PlannedOrderSide.Long
            ? entryPrice - estimatedStopFillPrice
            : estimatedStopFillPrice - entryPrice;

        if (priceLossPerShare <= 0m)
        {
            return Reject(
                OrderPlanRejectionCode.InvalidStop,
                "The requested stop does not create positive adverse price risk.");
        }

        var accountRiskBudget = request.AccountEquity * (request.RiskLimits.AccountRiskBudgetPct / 100m);
        var fixedFees = request.ExecutionCosts.TotalFixedFees;
        if (fixedFees >= accountRiskBudget)
        {
            return Reject(
                OrderPlanRejectionCode.AccountRiskBudgetConsumedByCosts,
                $"Estimated fixed fees of {FormatCurrency(fixedFees)} consume the account risk budget of " +
                $"{FormatCurrency(accountRiskBudget)}.");
        }

        var positionNotionalLimit =
            request.AccountEquity * (request.RiskLimits.MaxPositionNotionalPct / 100m);
        if (request.RequestedNotional is not null)
        {
            positionNotionalLimit = Math.Min(positionNotionalLimit, request.RequestedNotional.Value);
        }

        var quantityByNotional = FloorToQuantity(positionNotionalLimit / entryPrice);
        var quantityByAccountRisk = FloorToQuantity((accountRiskBudget - fixedFees) / priceLossPerShare);
        var quantity = Math.Min(quantityByNotional, quantityByAccountRisk);

        if (quantity <= 0)
        {
            return Reject(
                OrderPlanRejectionCode.PositionNotionalTooSmall,
                "The account-risk and position-notional limits do not permit one share.");
        }

        var deployedNotional = quantity * entryPrice;
        var plannedLoss = quantity * priceLossPerShare + fixedFees;

        if (plannedLoss > accountRiskBudget)
        {
            return Reject(
                OrderPlanRejectionCode.PlannedLossExceedsAccountRiskBudget,
                $"Estimated planned loss of {FormatCurrency(plannedLoss)} exceeds the account risk budget of " +
                $"{FormatCurrency(accountRiskBudget)}.");
        }

        return OrderPlanningResult.Accept(
            new PlannedOrder(
                request.Symbol.Trim().ToUpperInvariant(),
                request.Side,
                quantity,
                entryPrice,
                stopTriggerPrice,
                estimatedStopFillPrice,
                deployedNotional,
                priceLossPerShare,
                fixedFees,
                plannedLoss,
                accountRiskBudget));
    }

    private static OrderPlanningResult? ValidateRequest(OrderPlanningRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            return Reject(OrderPlanRejectionCode.InvalidSymbol, "A symbol is required.");
        }

        if (request.AccountEquity <= 0m)
        {
            return Reject(
                OrderPlanRejectionCode.InvalidAccountEquity,
                "Account equity must be greater than zero.");
        }

        if (request.ReferenceEntryPrice <= 0m)
        {
            return Reject(
                OrderPlanRejectionCode.InvalidReferenceEntryPrice,
                "Reference entry price must be greater than zero.");
        }

        if (!IsValidPercentage(request.RiskLimits.AccountRiskBudgetPct) ||
            !IsValidPercentage(request.RiskLimits.MaxPositionNotionalPct))
        {
            return Reject(
                OrderPlanRejectionCode.InvalidRiskLimits,
                "Risk percentages must each be greater than zero and no greater than 100.");
        }

        if (request.ExecutionCosts.SlippageBps < 0m ||
            request.ExecutionCosts.SlippageBps >= BasisPointsDivisor ||
            request.ExecutionCosts.FixedEntryFee < 0m ||
            request.ExecutionCosts.FixedExitFee < 0m)
        {
            return Reject(
                OrderPlanRejectionCode.InvalidExecutionCosts,
                "Slippage must be between 0 and 10,000 basis points, and fixed fees cannot be negative.");
        }

        if (request.RequestedNotional is <= 0m)
        {
            return Reject(
                OrderPlanRejectionCode.InvalidRequestedNotional,
                "Requested notional must be greater than zero when supplied.");
        }

        return null;
    }

    private static StopResolution ResolveStop(
        OrderPlanningRequest request,
        decimal entryPrice,
        decimal slippageRate)
    {
        decimal stopTriggerPrice;
        if (request.Stop.ResolvedStopPrice is > 0m)
        {
            stopTriggerPrice = request.Stop.ResolvedStopPrice.Value;
        }
        else
        {
            switch (request.Stop.Kind)
            {
                case PlannedStopKind.Atr:
                    if (request.Stop.Atr is not > 0m || request.Stop.AtrMultiple is not > 0m)
                    {
                        return StopResolution.Rejected(
                            Reject(
                                OrderPlanRejectionCode.InvalidStop,
                                "An ATR stop requires positive ATR and ATR-multiple values."));
                    }

                    var stopDistance = request.Stop.Atr.Value * request.Stop.AtrMultiple.Value;
                    stopTriggerPrice = request.Side == PlannedOrderSide.Long
                        ? entryPrice - stopDistance
                        : entryPrice + stopDistance;
                    break;

                case PlannedStopKind.Structural:
                    if (request.Stop.StructuralStopPrice is not > 0m)
                    {
                        return StopResolution.Rejected(
                            Reject(
                                OrderPlanRejectionCode.InvalidStop,
                                "A structural stop requires a positive stop price."));
                    }

                    stopTriggerPrice = request.Stop.StructuralStopPrice.Value;
                    break;

                default:
                    return StopResolution.Rejected(
                        Reject(OrderPlanRejectionCode.InvalidStop, "The stop kind is unsupported."));
            }
        }

        if (stopTriggerPrice <= 0m)
        {
            return StopResolution.Rejected(
                Reject(
                    OrderPlanRejectionCode.InvalidStop,
                    "The requested stop produces a non-positive stop trigger price."));
        }

        var hasCorrectSide = request.Side == PlannedOrderSide.Long
            ? stopTriggerPrice < entryPrice
            : stopTriggerPrice > entryPrice;
        if (!hasCorrectSide)
        {
            return StopResolution.Rejected(
                Reject(
                    OrderPlanRejectionCode.InvalidStop,
                    request.Side == PlannedOrderSide.Long
                        ? "A long stop must be below the estimated entry price."
                        : "A short stop must be above the estimated entry price."));
        }

        var estimatedStopFillPrice = request.Side == PlannedOrderSide.Long
            ? stopTriggerPrice * (1m - slippageRate)
            : stopTriggerPrice * (1m + slippageRate);

        return estimatedStopFillPrice > 0m
            ? StopResolution.Resolved(stopTriggerPrice, estimatedStopFillPrice)
            : StopResolution.Rejected(
                Reject(
                    OrderPlanRejectionCode.InvalidStop,
                    "Adverse stop slippage produces a non-positive estimated stop fill price."));
    }

    private static bool IsValidPercentage(decimal value) => value > 0m && value <= 100m;

    private static int FloorToQuantity(decimal value) =>
        value <= 0m ? 0 : (int)Math.Min(decimal.Floor(value), int.MaxValue);

    private static string Format(decimal value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string FormatCurrency(decimal value) =>
        value.ToString("C2", CultureInfo.InvariantCulture);

    private static OrderPlanningResult Reject(OrderPlanRejectionCode code, string reason) =>
        OrderPlanningResult.Reject(code, reason);

    private sealed record StopResolution(
        decimal StopTriggerPrice,
        decimal EstimatedStopFillPrice,
        OrderPlanningResult? Rejection)
    {
        public static StopResolution Resolved(decimal triggerPrice, decimal estimatedFillPrice) =>
            new(triggerPrice, estimatedFillPrice, null);

        public static StopResolution Rejected(OrderPlanningResult rejection) =>
            new(0m, 0m, rejection);
    }
}

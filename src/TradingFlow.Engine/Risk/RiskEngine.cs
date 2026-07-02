using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Risk;

public sealed class RiskEngine
{
    public FinalizedOrder? CreateLongBracketOrder(
        StrategyDefinition strategy,
        TradeSignal signal,
        decimal accountEquity,
        decimal riskPerTradePct)
    {
        if (signal.CurrentAtr <= 0 || accountEquity <= 0 || riskPerTradePct <= 0)
        {
            return null;
        }

        var stopDistance = strategy.ExitRules.StopAtrMultiple * signal.CurrentAtr;
        if (stopDistance <= 0)
        {
            return null;
        }

        var riskBudget = accountEquity * (riskPerTradePct / 100m);
        var shareQuantity = (int)Math.Floor(riskBudget / stopDistance);
        if (shareQuantity <= 0)
        {
            return null;
        }

        var limitPrice = ApplyLongSlippage(signal.CurrentPrice, strategy.Execution.SlippageBps);
        var stopLossPrice = limitPrice - stopDistance;
        var takeProfitPrice = limitPrice + (stopDistance * strategy.ExitRules.TargetRMultiple);

        return new FinalizedOrder(
            signal.Ticker,
            strategy.StrategyName,
            shareQuantity,
            Decimal.Round(limitPrice, 4),
            Decimal.Round(stopLossPrice, 4),
            Decimal.Round(takeProfitPrice, 4),
            signal.Timestamp);
    }

    public FinalizedOrder? CreateLongBracketOrderWithPositionRisk(
        StrategyDefinition strategy,
        TradeSignal signal,
        decimal accountEquity,
        decimal riskPerTradePct,
        decimal maxPositionValuePct)
    {
        if (signal.CurrentAtr <= 0 || accountEquity <= 0 || riskPerTradePct <= 0 || maxPositionValuePct <= 0)
        {
            return null;
        }

        var limitPrice = ApplyLongSlippage(signal.CurrentPrice, strategy.Execution.SlippageBps);
        if (limitPrice <= 0)
        {
            return null;
        }

        var atrStopDistance = strategy.ExitRules.StopAtrMultiple * signal.CurrentAtr;
        var positionRiskStopDistance = limitPrice * (riskPerTradePct / 100m);
        var stopDistance = Math.Min(atrStopDistance, positionRiskStopDistance);
        if (stopDistance <= 0)
        {
            return null;
        }

        var riskBudget = accountEquity * (riskPerTradePct / 100m);
        var riskSizedQuantity = (int)Math.Floor(riskBudget / stopDistance);
        var maxPositionValue = accountEquity * (maxPositionValuePct / 100m);
        var capitalSizedQuantity = (int)Math.Floor(maxPositionValue / limitPrice);
        var shareQuantity = Math.Min(riskSizedQuantity, capitalSizedQuantity);
        if (shareQuantity <= 0)
        {
            return null;
        }

        var stopLossPrice = limitPrice - stopDistance;
        var takeProfitPrice = limitPrice + (stopDistance * strategy.ExitRules.TargetRMultiple);

        return new FinalizedOrder(
            signal.Ticker,
            strategy.StrategyName,
            shareQuantity,
            Decimal.Round(limitPrice, 4),
            Decimal.Round(stopLossPrice, 4),
            Decimal.Round(takeProfitPrice, 4),
            signal.Timestamp);
    }

    private static decimal ApplyLongSlippage(decimal price, decimal slippageBps)
    {
        return price * (1m + (slippageBps / 10_000m));
    }
}

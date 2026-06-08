using System;

namespace TradingFlow.Engine.Risk;

public static class DynamicSlippageModel
{
    public static decimal CalculateSlippageBps(decimal price, decimal relativeVolume, decimal baseSlippageBps, decimal? tradeAmount = null)
    {
        // Start with the base slippage (e.g. 8 bps)
        var bps = baseSlippageBps;

        // Penalty for low price (penny stocks have wider spreads percentage-wise)
        if (price < 2.0m)
            bps *= 3.0m;
        else if (price < 5.0m)
            bps *= 2.0m;
        else if (price < 10.0m)
            bps *= 1.5m;

        // Penalty for low relative volume (illiquid order books)
        if (relativeVolume < 0.5m)
            bps *= 2.5m;
        else if (relativeVolume < 0.8m)
            bps *= 1.5m;
        else if (relativeVolume > 2.0m)
            bps *= 0.8m; // Discount for highly liquid/active periods

        // Penalty for large trade amounts
        if (tradeAmount.HasValue)
        {
            if (tradeAmount.Value > 100_000m)
                bps *= 1.5m; // Large institutional size
            else if (tradeAmount.Value > 50_000m)
                bps *= 1.2m; // Medium size
        }

        // Cap slippage at 100 bps (1%) to prevent absurd fills
        return Math.Min(bps, 100.0m);
    }

    public static decimal ApplyLongSlippage(decimal price, decimal relativeVolume, decimal baseSlippageBps, decimal? tradeAmount = null)
    {
        var dynamicBps = CalculateSlippageBps(price, relativeVolume, baseSlippageBps, tradeAmount);
        return price * (1m + (dynamicBps / 10_000m));
    }
}

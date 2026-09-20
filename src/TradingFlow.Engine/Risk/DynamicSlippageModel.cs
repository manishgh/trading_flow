using System;

namespace TradingFlow.Engine.Risk;

public static class DynamicSlippageModel
{
    public static decimal CalculateSlippageBps(decimal price, decimal relativeVolume, decimal baseSlippageBps, decimal? tradeAmount = null)
    {
        return DeterministicExecutionSimulator.CalculateLegacyDynamicSlippageBps(
            price,
            relativeVolume,
            baseSlippageBps,
            tradeAmount);
    }

    public static decimal ApplyLongSlippage(decimal price, decimal relativeVolume, decimal baseSlippageBps, decimal? tradeAmount = null)
    {
        var dynamicBps = CalculateSlippageBps(price, relativeVolume, baseSlippageBps, tradeAmount);
        return price * (1m + (dynamicBps / 10_000m));
    }

    public static decimal ApplyLongExitSlippage(decimal price, decimal relativeVolume, decimal baseSlippageBps, decimal? tradeAmount = null)
    {
        var dynamicBps = CalculateSlippageBps(price, relativeVolume, baseSlippageBps, tradeAmount);
        return price * (1m - (dynamicBps / 10_000m));
    }

    public static decimal ApplyShortEntrySlippage(decimal price, decimal relativeVolume, decimal baseSlippageBps, decimal? tradeAmount = null)
    {
        var dynamicBps = CalculateSlippageBps(price, relativeVolume, baseSlippageBps, tradeAmount);
        return price * (1m - (dynamicBps / 10_000m));
    }

    public static decimal ApplyShortExitSlippage(decimal price, decimal relativeVolume, decimal baseSlippageBps, decimal? tradeAmount = null)
    {
        var dynamicBps = CalculateSlippageBps(price, relativeVolume, baseSlippageBps, tradeAmount);
        return price * (1m + (dynamicBps / 10_000m));
    }
}

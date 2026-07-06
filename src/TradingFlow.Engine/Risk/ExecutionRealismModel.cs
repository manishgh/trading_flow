namespace TradingFlow.Engine.Risk;

/// <summary>
/// Execution realism helpers so a backtest fill resembles what the shared strategy brain
/// would actually get from a real broker. Two effects that flat-fee/unbounded-size backtests
/// miss: you cannot fill more than a small share of a bar's real volume, and US equity sells
/// carry regulatory pass-through costs (SEC fee + FINRA TAF). See
/// docs/edge-recovery-master-plan.md phase 0.3.
/// </summary>
public static class ExecutionRealismModel
{
    /// <summary>
    /// Caps a requested share quantity to a fraction of the entry bar's traded volume.
    /// A value of 0 (or missing volume) disables the cap, preserving legacy behavior.
    /// </summary>
    public static int CapQuantityByParticipation(int requestedQuantity, decimal entryBarVolume, decimal maxParticipationPct)
    {
        if (maxParticipationPct <= 0m || entryBarVolume <= 0m)
        {
            return requestedQuantity;
        }

        var cap = (int)Math.Floor(entryBarVolume * (maxParticipationPct / 100m));
        return Math.Min(requestedQuantity, Math.Max(cap, 0));
    }

    /// <summary>
    /// Approximate US equity regulatory sell-side costs. Applies only to the sell leg
    /// (exit for longs, entry for shorts). All rates default to 0 = disabled.
    /// </summary>
    public static decimal RegulatoryFees(
        decimal sellProceeds,
        int shares,
        decimal secFeeRate,
        decimal finraTafPerShare,
        decimal finraTafCap)
    {
        var fees = 0m;

        if (secFeeRate > 0m && sellProceeds > 0m)
        {
            fees += sellProceeds * secFeeRate;
        }

        if (finraTafPerShare > 0m && shares > 0)
        {
            var taf = shares * finraTafPerShare;
            if (finraTafCap > 0m)
            {
                taf = Math.Min(taf, finraTafCap);
            }

            fees += taf;
        }

        return fees;
    }
}

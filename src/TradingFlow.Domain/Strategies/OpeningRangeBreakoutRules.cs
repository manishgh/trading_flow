namespace TradingFlow.Domain.Strategies;

/// <summary>Order matching behavior; zero retains historical pending-limit replay.</summary>
public enum IntradayEntryOrderMode { NextBarLimit = 0, StopMarket = 1, NextBarMarket = 2 }

/// <summary>Approved fifteen-minute opening range with a declared filled-parent session limit.</summary>
public sealed record OpeningRangeBreakoutRules(
    string PolicyId = "opening-range-breakout-v1",
    int MaxFilledParentTradesPerSession = 2)
{
    public const string SetupType = "opening_range_breakout";
    public const string ExecutionTimingPolicy = "completed-bar-pending-stop-market-session-deadline-v1";
    public const string OutwardOrderRoundingPolicyId = "opening-range-breakout-outward-rounding-v1";

    public const string CompletedBreakoutPolicyId = "opening-range-breakout-completed-candle-v1";
    public const string NextBarMarketTimingPolicy = "completed-bar-next-open-market-session-deadline-v1";

    // Freeze the reviewed range and shared exit baseline; the declared positive parent limit is retained in each intent.
    public void Validate(StrategyDefinition strategy)
    {
        if (PolicyId is not ("opening-range-breakout-v1" or OutwardOrderRoundingPolicyId or CompletedBreakoutPolicyId) || MaxFilledParentTradesPerSession <= 0 ||
            strategy.EntryRules.PreviousSessionBreakout is not null ||
            strategy.Direction != "long_short" || !strategy.EntryRules.EnableShort ||
            strategy.EntryRules.ShortSetupType != SetupType)
            throw new InvalidOperationException("Opening-range breakout requires its exclusive long/short research policy and a positive filled-parent session limit.");
        FixedIntradayBreakoutBaseline.Validate(strategy, SetupType, PolicyId == CompletedBreakoutPolicyId ? "breakout_candle" : "opposite_opening_range");
    }
}

namespace TradingFlow.Domain.Strategies;

/// <summary>Order matching behavior; zero retains historical pending-limit replay.</summary>
public enum IntradayEntryOrderMode { NextBarLimit = 0, StopMarket = 1, NextBarMarket = 2 }

/// <summary>
/// Opening-range breakout policies. The 2026-10 research policies use a fifteen-minute range; the published
/// recipe (Zarattini, Barbon and Aziz, SSRN 4729284) declares its range length, opening-volume baseline and
/// ATR-fraction stop explicitly. Recipe parameters are omitted from canonical JSON when unset, so the earlier
/// policies keep their identity hashes.
/// </summary>
public sealed record OpeningRangeBreakoutRules(
    string PolicyId = "opening-range-breakout-v1",
    int MaxFilledParentTradesPerSession = 2,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? RangeMinutes = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? VolumeLookbackSessions = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? MinRelativeVolume = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? AtrSessions = null,
    // Published recipe only: re-measure the protective stop from the actual average entry fill instead of the trigger.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool StopFromFill = false,
    // Published recipe only: keep the entry order live until 16:00 and exit at the session's official close (daily close)
    // instead of the opening of the last five-minute bar.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool ExitAtSessionClose = false)
{
    public const string SetupType = "opening_range_breakout";
    public const string ExecutionTimingPolicy = "completed-bar-pending-stop-market-session-deadline-v1";
    public const string OutwardOrderRoundingPolicyId = "opening-range-breakout-outward-rounding-v1";

    public const string CompletedBreakoutPolicyId = "opening-range-breakout-completed-candle-v1";
    public const string NextBarMarketTimingPolicy = "completed-bar-next-open-market-session-deadline-v1";

    /// <summary>Published recipe: declared range, mean opening-volume baseline, stop order at the range, ATR-fraction stop, no target.</summary>
    public const string PublishedRecipePolicyId = "opening-range-breakout-recipe-v1";

    /// <summary>Stop mode of the published recipe: exit_rules.stop_atr_multiple times the prior-session daily ATR from the entry trigger.</summary>
    public const string DailyAtrFractionStopMode = "daily_atr_fraction";

    /// <summary>True for the published recipe policy.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPublishedRecipe => PolicyId == PublishedRecipePolicyId;

    /// <summary>Number of five-minute bars in the opening range.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int RangeBars => (RangeMinutes ?? 15) / 5;

    // Freeze the reviewed range and shared exit baseline; the declared positive parent limit is retained in each intent.
    public void Validate(StrategyDefinition strategy)
    {
        if (PolicyId is not ("opening-range-breakout-v1" or OutwardOrderRoundingPolicyId or CompletedBreakoutPolicyId or PublishedRecipePolicyId) ||
            MaxFilledParentTradesPerSession <= 0 ||
            strategy.Direction != "long_short" || !strategy.EntryRules.EnableShort ||
            strategy.EntryRules.ShortSetupType != SetupType)
            throw new InvalidOperationException("Opening-range breakout requires its exclusive long/short research policy and a positive filled-parent session limit.");
        if (!IsPublishedRecipe && (StopFromFill || ExitAtSessionClose))
            throw new InvalidOperationException("stop_from_fill and exit_at_session_close belong only to the published recipe policy.");
        var recipeParameters = new object?[] { RangeMinutes, VolumeLookbackSessions, MinRelativeVolume, AtrSessions };
        if (IsPublishedRecipe
                ? RangeMinutes is not (> 0 and <= 60) || RangeMinutes % 5 != 0 || VolumeLookbackSessions is not (> 0 and <= 20) ||
                  MinRelativeVolume is not > 0m || AtrSessions is not (> 0 and < 20)
                : recipeParameters.Any(value => value is not null))
            throw new InvalidOperationException(IsPublishedRecipe
                ? "The published recipe needs range_minutes (multiple of 5, up to 60), volume_lookback_sessions (1-20), a positive min_relative_volume and atr_sessions (1-19)."
                : "Range, volume and ATR parameters belong only to the published recipe policy.");
        FixedIntradayBreakoutBaseline.Validate(strategy, SetupType,
            IsPublishedRecipe ? DailyAtrFractionStopMode : PolicyId == CompletedBreakoutPolicyId ? "breakout_candle" : "opposite_opening_range",
            IsPublishedRecipe);
    }
}

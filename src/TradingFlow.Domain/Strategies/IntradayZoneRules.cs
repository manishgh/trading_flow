namespace TradingFlow.Domain.Strategies;

/// <summary>Frozen, approved close-pivot zone recipe; execution owns the shared filled-parent limit.</summary>
public sealed record IntradayZoneRules(string PolicyId = "intraday-close-pivot-zones-v1", int MaxFilledParentTradesPerSession = 2,
    int PivotStrength = 3, int PriorSessionCount = 5, decimal MergeAtrMultiple = 0.35m, decimal FlipAtrMultiple = 0.2m,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] PythonZoneComparisonRules? PythonComparison = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] PythonZoneVolumeSpreadComparisonRules? PythonVolumeSpreadComparison = null)
{
    public const string PythonComparisonPolicyId = "python-zone-bounce-breakdown-v1";
    public const string PythonComparisonTimingPolicy = "completed-bar-next-open-zone-fixed-target-session-deadline-v1";
    [System.Text.Json.Serialization.JsonIgnore] public bool IsPythonComparison => PolicyId == PythonComparisonPolicyId;
    public const string PythonVolumeSpreadComparisonPolicyId = "python-zone-volume-spread-v1";
    [System.Text.Json.Serialization.JsonIgnore] public bool IsPythonVolumeSpreadComparison => PolicyId == PythonVolumeSpreadComparisonPolicyId;
    [System.Text.Json.Serialization.JsonIgnore] public bool IsPythonFixedTargetComparison => IsPythonComparison || IsPythonVolumeSpreadComparison;
    public const string SetupType = "intraday_zone";
    public const string ExecutionTimingPolicy = "completed-bar-sip-market-session-deadline-v1";

    // Validate reusable arithmetic and memory bounds; the approved immutable recipe freezes the actual selected values.
    public void ValidateParameters()
    {
        if ((PolicyId != "intraday-close-pivot-zones-v1" && !IsPythonFixedTargetComparison) || MaxFilledParentTradesPerSession <= 0 || PivotStrength <= 0 ||
            PivotStrength > (int.MaxValue - 1) / 2 || PriorSessionCount <= 0 || MergeAtrMultiple < 0m || FlipAtrMultiple < 0m)
            throw new InvalidOperationException("Intraday zones require the versioned policy, positive counts and nonnegative ATR multiples.");
        if (IsPythonComparison != (PythonComparison is not null))
            throw new InvalidOperationException("Only the Python comparison policy requires explicit comparison parameters.");
        PythonComparison?.Validate();
        if (IsPythonVolumeSpreadComparison != (PythonVolumeSpreadComparison is not null))
            throw new InvalidOperationException("Only the volume/spread comparison policy requires explicit volume/spread parameters.");
        PythonVolumeSpreadComparison?.Validate();
    }

    // Reject variants outside the approved long/short, candle-stop and shared 3R research baseline.
    public void Validate(StrategyDefinition strategy)
    {
        ValidateParameters();
        if (strategy.EntryRules.PreviousSessionBreakout is not null || strategy.EntryRules.OpeningRangeBreakout is not null ||
            strategy.Direction != "long_short" || !strategy.EntryRules.EnableShort || strategy.EntryRules.ShortSetupType != SetupType)
            throw new InvalidOperationException("Intraday zones require their exclusive long/short policy.");
        if (IsPythonFixedTargetComparison)
        {
            if (strategy.ExitRules.InitialStopMode != "zone_atr" || strategy.ExitRules.ProfitTargetMode != "next_zone" ||
                strategy.ExitRules.TargetRMultiple != 0m || strategy.ExitRules.EnableAtrTrailingStop ||
                strategy.ExitRules.ContinuationProtection is not null)
                throw new InvalidOperationException("Python comparison requires fixed zone/ATR stop and next-zone target without R targets or continuation.");
            ValidatePythonBaseline(strategy);
        }
        else FixedIntradayBreakoutBaseline.Validate(strategy, SetupType, "breakout_candle", "15m");
    }
    // This comparison has its own exit contract and never weakens the existing 3R baseline validation.
    private static void ValidatePythonBaseline(StrategyDefinition strategy)
    {
        var exits = strategy.ExitRules;
        if (strategy.EntryRules.SetupType != SetupType || strategy.Timeframe != "15m" || strategy.Execution.Timeframe != "5m" ||
            strategy.Session.IsContinuousMarket || strategy.Session.UseExtendedHours || strategy.Session.ExchangeTimezone != "America/New_York" ||
            strategy.Session.QuietMinutesAfterOpen != 0 || strategy.Session.CloseBufferMinutes != 5 || strategy.Session.FridayCloseBufferMinutes != 5 ||
            strategy.Confluence.Enabled || strategy.Regime?.IsActive == true ||
            FixedIntradayBreakoutBaseline.HasCompetingEntryFilters(strategy.EntryRules) || strategy.EntryRules.RequirePositiveNews ||
            strategy.EntryRules.HistoricalNewsDiagnostic is not null || strategy.EntryRules.TechnicalFactor is not null ||
            strategy.EntryRules.DrawdownQualification is not null || strategy.Execution.EffectiveConfirmation.Enabled ||
            exits.ExitOnCloseAboveSma5 || exits.ExitOnCloseBelowEma20 || exits.ExitOnCloseBelowVwap || exits.ExitOnMacdHistogramNegative ||
            exits.ExitOnLogPriceFade || exits.EnableConfirmedVwapExit || exits.ExitOnSma10NearSma20 || exits.ExitOnSma10CrossBelowSma20 ||
            exits.ExitShortOnSma10CrossAboveSma20 || exits.ExitOnEma10CrossBelowEma20 || exits.ExitOnRsi2Above is not null || exits.MaxHoldBars is not null)
            throw new InvalidOperationException("Python comparison requires its exclusive regular-session fifteen/five-minute baseline without extra filters or exits.");
    }
}

/// <summary>Independent immutable comparison; zero minimum reward/risk disables that filter, while ticket fees remain portfolio-owned.</summary>
public sealed record PythonZoneVolumeSpreadComparisonRules(int MinimumPivotCount, int MinimumZoneCount,
    decimal LongTouchDollarTolerance, decimal MinimumRelativeVolume, decimal StopAtrMultiple,
    int EntryCutoffMinutesBeforeClose, decimal SyntheticSpreadBps, decimal MaximumSpreadBps, decimal MinimumRewardRisk,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? LongBounceVolumeMode = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? TargetPolicy = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] decimal? FallbackNetRiskMultiple = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? LongStopBasis = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? LongEntryMode = null)
{
    public const string DiagnosticLongVolume = "diagnostic_only";
    public const string ZoneOrFullCostRiskTarget = "zone_or_full_cost_risk";
    public const string TwoCandleLowStop = "two_candle_low";
    public const string BreakoutRetestEntry = "breakout_retest";
    [System.Text.Json.Serialization.JsonIgnore] public bool LongVolumeIsDiagnostic => LongBounceVolumeMode == DiagnosticLongVolume;
    [System.Text.Json.Serialization.JsonIgnore] public bool UsesFullCostTargets => TargetPolicy == ZoneOrFullCostRiskTarget;
    [System.Text.Json.Serialization.JsonIgnore] public bool UsesBreakoutRetestEntry => LongEntryMode == BreakoutRetestEntry;

    // Reject invalid arithmetic and clocks; zero reward/risk disables filtering and negative thresholds are invalid.
    public void Validate()
    {
        if (MinimumPivotCount <= 0 || MinimumZoneCount < 2 || LongTouchDollarTolerance < 0m ||
            MinimumRelativeVolume < 0m || StopAtrMultiple < 0m || EntryCutoffMinutesBeforeClose <= 5 ||
            SyntheticSpreadBps < 0m || SyntheticSpreadBps >= 20000m || MaximumSpreadBps < 0m || MinimumRewardRisk < 0m ||
            LongBounceVolumeMode is not (null or DiagnosticLongVolume) || TargetPolicy is not (null or ZoneOrFullCostRiskTarget) ||
            LongStopBasis is not (null or TwoCandleLowStop) ||
            LongEntryMode is not (null or BreakoutRetestEntry) ||
            (UsesBreakoutRetestEntry && (!LongVolumeIsDiagnostic || !UsesFullCostTargets || LongStopBasis is not null)) ||
            (UsesFullCostTargets ? FallbackNetRiskMultiple is not > 0m || MinimumRewardRisk != 0m : FallbackNetRiskMultiple is not null))
            throw new InvalidOperationException("Volume/spread comparison requires explicit counts, entry buffer and valid spread, price and reward/risk thresholds.");
    }
}

/// <summary>Frozen comparison inputs; pivot count approximates Python touches, not price visits.</summary>
public sealed record PythonZoneComparisonRules(int MinimumPivotCount, int MinimumZoneCount, decimal LongTouchDollarTolerance,
    decimal LongVwapAllowanceAtrMultiple, decimal MaximumVwapDistanceAtrMultiple, decimal MinimumShortRelativeVolume,
    decimal StopAtrMultiple, int EntryCutoffMinutesBeforeClose)
{
    // Validate arithmetic bounds; the immutable research recipe freezes actual selected values.
    public void Validate()
    {
        if (MinimumPivotCount <= 0 || MinimumZoneCount < 2 || LongTouchDollarTolerance < 0m ||
            LongVwapAllowanceAtrMultiple < 0m || MaximumVwapDistanceAtrMultiple <= 0m ||
            MinimumShortRelativeVolume < 0m || StopAtrMultiple < 0m || EntryCutoffMinutesBeforeClose <= 5)
            throw new InvalidOperationException("Python comparison requires explicit counts, entry buffer and valid price/ATR thresholds.");
    }
}

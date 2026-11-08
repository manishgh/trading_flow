namespace TradingFlow.Domain.Strategies;

// Fixed session/filter/exit constraints for the approved opening-range breakout baseline; the strategy keeps its own signal and stop geometry.
internal static class FixedIntradayBreakoutBaseline
{
    // Reject clocks and extra filters that would change the approved intraday baseline. The published recipe
    // (Zarattini, Barbon and Aziz) has no target and no continuation protection: it holds until its stop or the close.
    internal static void Validate(StrategyDefinition strategy, string setupType, string initialStopMode, bool publishedRecipe = false)
    {
        var exits = strategy.ExitRules;
        var exitPolicyMatches = publishedRecipe
            ? !exits.HasProfitTarget && exits.ContinuationProtection is null && !exits.EnableAtrTrailingStop && exits.StopAtrMultiple is > 0m and < 1m
            : exits.TargetRMultiple == 3m && exits.ContinuationProtection == new ContinuationProtectionRules();
        if (!exitPolicyMatches)
            throw new InvalidOperationException(publishedRecipe
                ? "The published opening-range recipe requires profit_target_mode: none, no trailing stop or continuation protection, and a stop fraction of the daily ATR between 0 and 1."
                : "Intraday breakout requires its 3R target and default continuation protection.");
        const string primaryTimeframe = "5m";
        if (strategy.EntryRules.SetupType != setupType ||
            strategy.Timeframe != primaryTimeframe || strategy.Execution.Timeframe != "5m" ||
            strategy.Session.IsContinuousMarket || strategy.Session.UseExtendedHours ||
            strategy.Session.ExchangeTimezone != "America/New_York" ||
            strategy.Session.QuietMinutesAfterOpen != 0 ||
            strategy.Session.CloseBufferMinutes != 5 || strategy.Session.FridayCloseBufferMinutes != 5 ||
            strategy.Confluence.Enabled || strategy.Regime?.IsActive == true || HasCompetingEntryFilters(strategy.EntryRules) ||
            strategy.EntryRules.RequirePositiveNews || strategy.EntryRules.HistoricalNewsDiagnostic is not null ||
            strategy.EntryRules.TechnicalFactor is not null || strategy.EntryRules.DrawdownQualification is not null ||
            strategy.Execution.EffectiveConfirmation.Enabled || strategy.ExitRules.InitialStopMode != initialStopMode ||
            strategy.ExitRules.ExitOnCloseAboveSma5 || strategy.ExitRules.ExitOnCloseBelowEma20 ||
            strategy.ExitRules.ExitOnCloseBelowVwap || strategy.ExitRules.ExitOnMacdHistogramNegative ||
            strategy.ExitRules.ExitOnLogPriceFade || strategy.ExitRules.EnableConfirmedVwapExit ||
            strategy.ExitRules.ExitOnSma10NearSma20 || strategy.ExitRules.ExitOnSma10CrossBelowSma20 ||
            strategy.ExitRules.ExitShortOnSma10CrossAboveSma20 || strategy.ExitRules.ExitOnEma10CrossBelowEma20 ||
            strategy.ExitRules.ExitOnRsi2Above is not null || strategy.ExitRules.MaxHoldBars is not null)
            throw new InvalidOperationException("Intraday breakout requires its fixed regular-session five-minute baseline and declared stop.");
        exits.ContinuationProtection?.Validate(exits);
    }

    // The fixed baseline cannot silently accept an additional technical, volume, news or indicator gate.
    internal static bool HasCompetingEntryFilters(EntryRules rules) =>
        rules.MinVolumeSpike != 0m || rules.VolumeConfirmationMode != "none" ||
        rules.MinEntryRsi != 0m || rules.MaxEntryRsi != 100m || rules.TrendFilter != "none" || rules.MacdFilter != "none" ||
        rules.RequirePriceAboveBollingerMiddle || rules.RequireMacdHistogramPositive || rules.RequirePriceAboveVwap ||
        rules.RequirePriceAboveEma10 || rules.RequirePriceAboveEma20 || rules.RequirePriceAboveEma50 ||
        rules.RequireEma10AboveEma20 || rules.RequireEma20AboveEma50 ||
        rules.RequirePriceAboveSma10 || rules.RequirePriceAboveSma20 || rules.RequirePriceAboveSma50 ||
        rules.RequirePriceAboveSma150 || rules.RequirePriceAboveSma200 ||
        rules.RequireSma10AboveSma20 || rules.RequireSma20AboveSma50 || rules.RequireSma50AboveSma150 || rules.RequireSma150AboveSma200 ||
        rules.RequirePriceAboveSma50Daily || rules.RequirePriceAboveSma200Daily || rules.RequirePriceAboveEma5_65m ||
        rules.RequirePriceAboveAnchoredVwap || rules.RequirePriceBelowAnchoredVwapForShort || rules.RequirePriceBelowShortAnchoredVwap ||
        rules.RequirePriceBelowVwapForShort || rules.RequireMacdBearishForShort || rules.RequireLogPriceRising || rules.RequireLogVolumeRising ||
        rules.RequireAdxRising || rules.RequireObvRising || rules.MinAdx is not null || rules.MinNewsSentiment is not null ||
        rules.VetoNewsSentimentBelow is not null || rules.VetoFreshNewsHours is not null || rules.MinCatalystPriceMovePct is not null ||
        rules.MaxCatalystPriceMovePct is not null || rules.MaxShortNewsSentiment is not null || rules.MinShortCatalystDropPct is not null ||
        rules.MaxShortEntryRsi is not null || rules.MinShortEntryRsi is not null || rules.EnableEntryBarConfirmation;
}

public static class StrategyHorizonResolver
{
    // Derive the day horizon from the strategy rather than from a caller's swing default.
    public static string Resolve(StrategyDefinition strategy) =>
        strategy.EntryRules.OpeningRangeBreakout is not null ? "day" : "swing";
}

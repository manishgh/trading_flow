using TradingFlow.Contracts.Operator;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Services;

/// <summary>Resolves ordinary saved-profile selections into one explicit strategy-compatible research execution policy.</summary>
public static class BacktestExecutionProfileResolver
{
    public static IReadOnlyList<BacktestExecutionReplayChoice> Choices { get; } = [
        new("", "Selected strategy / profile", "auto"),
        new(ExecutionReplayPolicy.DailyRthFiveMinuteV1, "Daily setup · five-minute diagnostic replay", "swing"),
        new(ExecutionReplayPolicy.DailyRthSipPortfolioV1, "Daily setup · SIP portfolio replay", "swing"),
        new(ExecutionReplayPolicy.IntradayRthFiveMinuteV1, "Day setup · five-minute limit diagnostic replay", "day"),
        new(ExecutionReplayPolicy.IntradayRthFiveMinuteStopMarketV1, "Opening range · five-minute stop-market diagnostic replay", "day"),
        new(ExecutionReplayPolicy.IntradayRthFiveMinuteQuoteMarketV1, "Intraday zones � historical SIP quotes/trades", "day"),
        new(ExecutionReplayPolicy.PythonZoneBounceBreakdownV1, "Python zone comparison - five-minute candle diagnostic replay", "day"),
        new(ExecutionReplayPolicy.PythonZoneVolumeSpreadV1, "Python zone volume/spread comparison - five-minute candle diagnostic replay", "day"),
        new(ExecutionReplayPolicy.IntradayRthFiveMinuteNextBarMarketV1, "Completed breakout: next-opening market / candle stop diagnostic replay", "day")
    ];

    /// <summary>Rejects mixed horizons and incompatible assumptions before any generated files or market-data request exists.</summary>
    public static BacktestRunConfig Resolve(BacktestRunConfig profile, IReadOnlyCollection<StrategyDefinition> strategies,
        string? requestedPolicy = null)
    {
        if (strategies.Count == 0) throw new InvalidOperationException("Select at least one strategy.");
        var horizons = strategies.Select(StrategyHorizonResolver.Resolve).Distinct(StringComparer.Ordinal).ToArray();
        if (horizons.Length != 1)
            throw new InvalidOperationException("Use separate saved plans for day and swing strategies so each plan has its own source horizon and execution assumptions.");
        var day = horizons[0] == "day";
        var zoneStrategy = strategies.Any(strategy => strategy.EntryRules.IntradayZone is not null);
        if (zoneStrategy && (strategies.Count != 1 || strategies.Any(strategy => strategy.EntryRules.IntradayZone is null)))
            throw new InvalidOperationException("Use a separate saved plan and portfolio for intraday zones.");
        var openingRange = strategies.Any(strategy => strategy.EntryRules.OpeningRangeBreakout is not null);
        if (openingRange && strategies.Any(strategy => strategy.EntryRules.OpeningRangeBreakout is null))
            throw new InvalidOperationException("Use separate saved plans for opening-range stop orders and previous-session limit orders.");
        var entryModes = strategies.Select(strategy => strategy.EntryRules.OpeningRangeBreakout is { } rules
            ? rules.PolicyId == OpeningRangeBreakoutRules.CompletedBreakoutPolicyId ? IntradayEntryOrderMode.NextBarMarket : IntradayEntryOrderMode.StopMarket
            : IntradayEntryOrderMode.NextBarLimit).Distinct().ToArray();
        if (day && entryModes.Length != 1)
            throw new InvalidOperationException("Use separate saved plans for different intraday entry modes.");
        var dayPolicy = zoneStrategy
            ? ZonePolicy(strategies.Single().EntryRules.IntradayZone!)
            : ExecutionReplayPolicy.ForEntryMode(entryModes[0]);
        var explicitPolicy = String.IsNullOrWhiteSpace(requestedPolicy) ? null : requestedPolicy;
        var policy = explicitPolicy ?? (day ? dayPolicy : profile.Engine.ExecutionReplayPolicyId);
        if (policy is not null && !Choices.Any(choice => choice.PolicyId == policy))
            throw new InvalidOperationException($"Unsupported execution replay policy: {policy}.");
        if (day && policy != dayPolicy ||
            !day && policy is not (null or ExecutionReplayPolicy.DailyRthFiveMinuteV1 or ExecutionReplayPolicy.DailyRthSipPortfolioV1))
            throw new InvalidOperationException("The selected execution replay policy does not support the strategy horizon.");
        var engine = profile.Engine with { ExecutionReplayPolicyId = policy };
        if (!day) return profile with { Engine = engine };
        if (!ExecutionReplayPolicy.IsEventPortfolio(policy) && (engine.ExecutionSourceManifestPath is not null || engine.ExecutionSourceManifestSha256 is not null))
            throw new InvalidOperationException("A SIP execution-source manifest cannot be reused for five-minute day diagnostic execution.");
        if (engine.PrecommittedLiquidationSourceUtc is not null)
            throw new InvalidOperationException("Day research uses its strategy's regular-session deadline; choose a profile without a terminal liquidation override.");
        return profile with
        {
            Engine = engine with { IndicatorWarmupBars = 0, AllowLateWarmup = false },
            TimeWindow = profile.TimeWindow with { WarmupLookbackDays = 0 },
            Intervals = ["5m"],
            DerivedTimeframes = new("5m"),
            Validation = profile.Validation with { Benchmark = profile.Validation.Benchmark with { Enabled = false } },
            News = profile.News with { Enabled = strategies.Any(StrategyCapabilityInspector.UsesNews) }
        };
    }
    // Resolve the immutable recipe policy before normal plan freezing; candle comparisons require no historical quotes.
    private static string ZonePolicy(IntradayZoneRules rules) => rules.IsPythonVolumeSpreadComparison
        ? ExecutionReplayPolicy.PythonZoneVolumeSpreadV1 : rules.IsPythonComparison
            ? ExecutionReplayPolicy.PythonZoneBounceBreakdownV1 : ExecutionReplayPolicy.IntradayRthFiveMinuteQuoteMarketV1;
}

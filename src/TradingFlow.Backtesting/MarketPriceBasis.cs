using TradingFlow.Data.Csv;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Market;

namespace TradingFlow.Backtesting;

internal sealed record MarketPriceBasis(string Policy, bool HasRawDaily, bool HasRawSubDaily,
    bool HasUnknown, bool HasDividendAdjustment)
{
    // Capture provenance before completion/coverage wrappers, including from the provider serving pinned prepared bars.
    internal static MarketDataProvenance? ReadProvenance(IMarketDataProvider source)
    {
        if (source is not IMarketDataProvenanceProvider provenance) return null;
        try { return provenance.MarketDataProvenance; }
        catch (InvalidOperationException) when (source is CachedMarketDataProvider)
        {
            // The cache also wraps providers whose underlying source has no provenance descriptor.
            return null;
        }
    }

    // Explicit bar labels describe observed inputs; a source descriptor fills otherwise unspecified labels.
    internal static MarketPriceBasis FromBars(IEnumerable<OhlcvBar> bars, MarketDataProvenance? provenance)
    {
        var policies = new HashSet<string>(StringComparer.Ordinal);
        var rawDaily = false;
        var rawSubDaily = false;
        var providerPolicy = Normalize(provenance?.AdjustmentPolicy);
        foreach (var bar in bars)
        {
            var policy = Normalize(bar.AdjustmentPolicy);
            if (policy == "unknown") policy = providerPolicy;
            policies.Add(policy);
            if (policy != "raw") continue;
            if (TimeframeParser.IsDailyOrHigher(bar.Timeframe)) rawDaily = true;
            else rawSubDaily = true;
        }
        if (policies.Count == 0) policies.Add("unknown");
        var value = policies.Count == 1 ? policies.Single() : $"mixed ({String.Join(", ", policies.Order(StringComparer.Ordinal))})";
        return new(value, rawDaily, rawSubDaily, policies.Contains("unknown"),
            policies.Any(policy => policy is "all" or "total_return" || policy.Contains("dividend", StringComparison.Ordinal)));
    }

    // Keep adapter adjustment labels while normalizing placeholders into one explicit unknown value.
    private static string Normalize(string? policy) => policy?.Trim().ToLowerInvariant() switch
    {
        null or "" or "unspecified" or "unknown" => "unknown",
        "none" or "unadjusted" => "raw",
        var value => value
    };
}

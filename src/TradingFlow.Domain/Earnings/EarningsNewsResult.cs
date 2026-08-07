namespace TradingFlow.Domain.Earnings;

/// <summary>
/// Provider-reported earnings values recovered from a structured result headline. This lives in
/// the domain so both the analyzer and the calendar repository can consume the same parse.
/// </summary>
public sealed record EarningsNewsResult(
    decimal? EpsEstimate,
    decimal? EpsActual,
    decimal? EpsSurprisePercent,
    decimal? RevenueEstimateMillions,
    decimal? RevenueActualMillions,
    decimal? RevenueSurprisePercent)
{
    public static EarningsNewsResult Empty { get; } = new(null, null, null, null, null, null);

    public bool HasResult => EpsActual.HasValue || RevenueActualMillions.HasValue;

    public static decimal? SurprisePercent(decimal? actual, decimal? estimate) =>
        actual.HasValue && estimate.HasValue && estimate.Value != 0m
            ? (actual.Value - estimate.Value) / Math.Abs(estimate.Value) * 100m
            : null;
}

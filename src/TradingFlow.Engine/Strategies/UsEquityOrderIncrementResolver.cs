namespace TradingFlow.Engine.Strategies;

public sealed record UsEquityOrderIncrementEvidence(decimal TickSize, string PolicyId, string SourceUrl);

/// <summary>US NMS broker order increments after the historical tick pilot and before the deferred Rule 612 amendments.</summary>
public static class UsEquityOrderIncrementResolver
{
    public const string PolicyId = "us-nms-post-tick-pilot-pre-rule612-amendment-v1";
    public const string SourceUrl = "https://docs.alpaca.markets/us/docs/orders-at-alpaca";
    public const string EffectiveBoundarySourceUrl = "https://www.sec.gov/newsroom/speeches-statements/tm-dera-statement-expiration-tick-size-pilot";
    public const string EndBoundarySourceUrl = "https://public-inspection.federalregister.gov/2026-11997.pdf";

    // Resolve the documented order increment, never infer a valid instrument increment from observed OHLC precision.
    public static UsEquityOrderIncrementEvidence? Resolve(DateOnly sessionDate, decimal minimumOrderPrice, decimal maximumOrderPrice)
    {
        // Pilot securities could require nickel increments before 1 October 2018; the new half-cent rule starts in November 2027.
        if (sessionDate < new DateOnly(2018, 10, 1) || sessionDate >= new DateOnly(2027, 11, 1) ||
            minimumOrderPrice <= 0m || maximumOrderPrice < minimumOrderPrice ||
            minimumOrderPrice < 1m && maximumOrderPrice >= 1m)
            return null;
        return new(minimumOrderPrice >= 1m ? 0.01m : 0.0001m, PolicyId, SourceUrl);
    }
}

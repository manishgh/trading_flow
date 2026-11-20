namespace TradingFlow.Domain.Market;

/// <summary>
/// Read-only option contract snapshot from an OPRA chain used strictly to compute equity volatility and skew context.
/// TradingFlow does not route or simulate option contract orders.
/// </summary>
public sealed record OptionContractQuote(
    string ContractSymbol,
    string UnderlyingSymbol,
    DateOnly ExpirationDate,
    bool IsCall,
    decimal Strike,
    decimal Bid,
    decimal Ask,
    decimal? ImpliedVolatility,
    decimal? Delta,
    decimal? Gamma,
    decimal? Theta,
    decimal? Vega,
    decimal DailyVolume);

/// <summary>
/// Deterministic trader-facing options context labels summarizing implied move, 25-delta put-call skew, and IV term structure.
/// </summary>
public static class OptionsSentimentRegime
{
    public const string Neutral = "neutral";
    public const string CallSkewSqueezeRisk = "call_skew_squeeze_risk";
    public const string PutSkewHedging = "put_skew_hedging";
    public const string TermBackwardationStress = "term_backwardation_stress";
    public const string NoLiquidOptions = "no_liquid_options";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// Read-only options sentiment and implied-volatility summary for a US equity symbol computed from filtered liquid OPRA contracts.
/// </summary>
public sealed record OptionsSentimentSnapshot(
    string Symbol,
    DateTimeOffset ObservedAtUtc,
    decimal? SpotPrice,
    DateOnly? FrontExpiry,
    int? TradingDaysToFrontExpiry,
    decimal? FrontImpliedMovePct,
    decimal? AtmImpliedVolPct,
    decimal? Skew25Delta,
    decimal? TermStructureSpread,
    decimal CallVolume,
    decimal PutVolume,
    decimal? CallPutVolumeRatio,
    decimal? ImpliedToRealizedAdrRatio,
    int LiquidContractCount,
    string RegimeLabel,
    string SummaryNote)
{
    /// <summary>
    /// Creates an explicit unavailable snapshot when credentials are missing or the OPRA provider query failed.
    /// </summary>
    public static OptionsSentimentSnapshot Unavailable(string symbol, DateTimeOffset observedAtUtc, string summaryNote) =>
        new(
            Symbol: symbol.Trim().ToUpperInvariant(),
            ObservedAtUtc: observedAtUtc.ToUniversalTime(),
            SpotPrice: null,
            FrontExpiry: null,
            TradingDaysToFrontExpiry: null,
            FrontImpliedMovePct: null,
            AtmImpliedVolPct: null,
            Skew25Delta: null,
            TermStructureSpread: null,
            CallVolume: 0m,
            PutVolume: 0m,
            CallPutVolumeRatio: null,
            ImpliedToRealizedAdrRatio: null,
            LiquidContractCount: 0,
            RegimeLabel: OptionsSentimentRegime.Unavailable,
            SummaryNote: summaryNote);
}

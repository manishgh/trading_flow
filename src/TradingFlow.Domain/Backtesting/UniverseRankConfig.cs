namespace TradingFlow.Domain.Backtesting;

/// <summary>
/// Weights for one horizon. They sum to 1.0 so a score is directly readable as a
/// fraction, and so changing one weight forces the operator to say what it is
/// taken from.
/// </summary>
/// <param name="ModelEdge">Direction-adjusted model probability, from the predictor.</param>
/// <param name="MarketStructure">Swing trend quality from the indicator engine.</param>
/// <param name="Catalyst">Catalyst weight, from news, earnings and the screener.</param>
/// <param name="TechnicalState">Eligibility verdict, from the signal generator and evaluator.</param>
/// <param name="Liquidity">Spread-derived tradability, from the quote stream.</param>
public sealed record UniverseRankWeights(
    decimal ModelEdge,
    decimal MarketStructure,
    decimal Catalyst,
    decimal TechnicalState,
    decimal Liquidity)
{
    public decimal Total => ModelEdge + MarketStructure + Catalyst + TechnicalState + Liquidity;
}

/// <summary>
/// How the desk orders a resolved candidate universe.
///
/// The desk selects a universe source, not a single symbol, and everything
/// downstream ranks that whole set. Per-symbol prediction is one input to this,
/// not the ranking itself: a model veto subtracts <see cref="VetoPenalty"/>
/// rather than removing the candidate, so the disagreement stays visible and
/// auditable instead of silently shrinking the universe.
///
/// Defaults live here only so a profile that omits the block still ranks. A
/// profile that states the block overrides it, and the version string travels
/// with every persisted ranking run so a stored score can be re-derived.
/// </summary>
/// <param name="WeightsVersion">Identifies the weight set a persisted run used.</param>
/// <param name="Swing">Weights applied when the desk horizon is swing.</param>
/// <param name="CatalystWeights">
/// Catalyst kind to weight, keyed by kind: ER, NEWS, SEC, SCRN, NONE.
/// </param>
/// <param name="VetoPenalty">
/// Flat subtraction applied when the model opposes the technical verdict.
/// </param>
/// <param name="LiquiditySpreadCeilingBps">
/// Spread in basis points at which the liquidity factor reaches zero. Inside
/// spread over this is untradable for the purpose of ranking.
/// </param>
public sealed record UniverseRankConfig(
    string WeightsVersion,
    UniverseRankWeights Swing,
    IReadOnlyDictionary<string, decimal> CatalystWeights,
    decimal VetoPenalty,
    decimal LiquiditySpreadCeilingBps)
{
    public const string EarningsCatalyst = "ER";
    public const string NewsCatalyst = "NEWS";
    public const string FilingCatalyst = "SEC";
    public const string ScreenerCatalyst = "SCRN";
    public const string NoCatalyst = "NONE";

    public static IReadOnlyDictionary<string, decimal> DefaultCatalystWeights { get; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            [EarningsCatalyst] = 1.00m,
            [NewsCatalyst] = 0.85m,
            [FilingCatalyst] = 0.50m,
            [ScreenerCatalyst] = 0.35m,
            [NoCatalyst] = 0.08m
        };

    public static UniverseRankConfig Default { get; } = new(
        "rank.v1",
        new UniverseRankWeights(0.34m, 0.28m, 0.16m, 0.14m, 0.08m),
        DefaultCatalystWeights,
        0.12m,
        8m);

    public UniverseRankWeights WeightsFor(string horizon)
    {
        if (!horizon.Equals("swing", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(nameof(horizon), "TradingFlow supports the swing horizon only.");
        }

        return Swing;
    }

    /// <summary>Weight for a catalyst kind, falling back to the no-catalyst weight.</summary>
    public decimal CatalystWeightFor(string kind) =>
        CatalystWeights.TryGetValue(kind, out var weight)
            ? weight
            : CatalystWeights.TryGetValue(NoCatalyst, out var none) ? none : 0m;
}

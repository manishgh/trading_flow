namespace TradingFlow.Domain.Market;

/// <summary>Which price a daily screen compares with its minimum.</summary>
public enum DailyScreenPriceBasis
{
    /// <summary>Close of the last completed session.</summary>
    PriorClose,

    /// <summary>Official open of the session being decided, known once it opens.</summary>
    SessionOpen
}

/// <summary>
/// Frozen daily eligibility rules shared by the research screen command, run settings and the per-day backtest
/// universe. A null threshold means that rule is not applied.
/// </summary>
public sealed record DailyScreenRules(
    decimal MinPrice,
    DailyScreenPriceBasis PriceBasis = DailyScreenPriceBasis.PriorClose,
    int AverageLookbackSessions = 20,
    decimal? MinAverageShareVolume = null,
    decimal? MinAverageDollarVolume = null,
    int? AtrPeriod = null,
    decimal? MinAtr = null,
    int MinHistorySessions = 1)
{
    /// <summary>Wilder ATR weights the earliest of 100 sessions by about 0.2%, so older history cannot change a decision.</summary>
    public const int AtrStabilizationSessions = 100;

    /// <summary>Fails on rules that could never be evaluated, such as an ATR minimum without an ATR period.</summary>
    public void Validate()
    {
        if (MinPrice < 0m || AverageLookbackSessions < 1 || MinHistorySessions < 1 ||
            MinAtr is not null && AtrPeriod is not { } period || AtrPeriod is < 1 ||
            MinAverageShareVolume is < 0m || MinAverageDollarVolume is < 0m || MinAtr is < 0m)
            throw new ArgumentException("Daily screen rules need non-negative prices, positive look-backs, and an ATR period for an ATR minimum.");
    }

    /// <summary>
    /// Completed sessions before a decision day that the rules read: the averaging window, the history minimum and a stable
    /// ATR. Wilder's starting estimate keeps (1 - 1/period)^n of its weight after n sessions; seven periods leave under 0.2%.
    /// </summary>
    public int PriorSessionsNeeded =>
        Math.Max(Math.Max(AverageLookbackSessions, MinHistorySessions),
            AtrPeriod is { } period ? Math.Max(AtrStabilizationSessions, checked(period * 7)) : 0);

    /// <summary>
    /// Fails when the price compared is the decision day's open but the strategy's orders fill at that same open: a
    /// market-on-open order cannot wait for its own opening price. Intraday entries after the open may use it.
    /// </summary>
    public void RequireKnownBeforeEntry(bool entriesFillAtSessionOpen)
    {
        if (PriceBasis == DailyScreenPriceBasis.SessionOpen && entriesFillAtSessionOpen)
            throw new ArgumentException("price_basis session_open is known only after the open; use prior_close for strategies that fill at the open.");
    }

    /// <summary>Reads the settings-file spelling of a price basis; unknown spellings fail instead of defaulting.</summary>
    public static DailyScreenPriceBasis ParsePriceBasis(string value) => value.Trim().ToLowerInvariant() switch
    {
        "prior_close" => DailyScreenPriceBasis.PriorClose,
        "session_open" => DailyScreenPriceBasis.SessionOpen,
        var other => throw new ArgumentException($"Unknown price basis '{other}'. Use prior_close or session_open.")
    };

    /// <summary>The settings-file spelling of a price basis, the inverse of <see cref="ParsePriceBasis"/>.</summary>
    public static string PriceBasisName(DailyScreenPriceBasis basis) =>
        basis == DailyScreenPriceBasis.SessionOpen ? "session_open" : "prior_close";
}

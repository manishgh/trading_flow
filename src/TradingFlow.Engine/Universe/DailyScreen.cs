using Skender.Stock.Indicators;
using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Universe;

/// <summary>Which price a daily screen compares with its minimum.</summary>
public enum DailyScreenPriceBasis
{
    /// <summary>Close of the last completed session.</summary>
    PriorClose,

    /// <summary>Official open of the session being decided, known once it opens.</summary>
    SessionOpen
}

/// <summary>Frozen daily eligibility rules. A null threshold means that rule is not applied.</summary>
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
    public void Validate()
    {
        if (MinPrice < 0m || AverageLookbackSessions < 1 || MinHistorySessions < 1 ||
            MinAtr is not null && AtrPeriod is not { } period || AtrPeriod is < 1)
            throw new ArgumentException("Daily screen rules need non-negative prices, positive look-backs, and an ATR period for an ATR minimum.");
    }
}

/// <summary>The measured values behind one screen decision, kept so every inclusion can be audited.</summary>
public sealed record DailyScreenValues(
    string Ticker,
    decimal Price,
    decimal AverageShareVolume,
    decimal AverageDollarVolume,
    decimal? Atr,
    int HistorySessions);

public sealed record DailyScreenResult(bool Eligible, DailyScreenValues? Values, string? Rejection);

/// <summary>
/// One shared daily eligibility rule set. Inputs are completed sessions before the decision date,
/// already on the decision date's split basis, plus the session open when the rules use it.
/// </summary>
public static class DailyScreen
{
    public static DailyScreenResult Evaluate(string ticker, IReadOnlyList<OhlcvBar> priorBars, decimal? sessionOpen, DailyScreenRules rules)
    {
        ArgumentNullException.ThrowIfNull(priorBars);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();
        if (priorBars.Count == 0) return Reject($"{ticker}: no_prior_daily_data");
        if (priorBars.Count < rules.MinHistorySessions)
            return Reject($"{ticker}: history {priorBars.Count} < min {rules.MinHistorySessions}");

        var window = priorBars.Count <= rules.AverageLookbackSessions
            ? priorBars : priorBars.Skip(priorBars.Count - rules.AverageLookbackSessions).ToArray();
        var averageShareVolume = window.Average(bar => bar.Volume);
        var averageDollarVolume = window.Average(bar => bar.Close * bar.Volume);
        decimal? price = rules.PriceBasis == DailyScreenPriceBasis.PriorClose ? priorBars[^1].Close : sessionOpen;
        if (price is null) return Reject($"{ticker}: session_open_unavailable");
        if (price < rules.MinPrice) return Reject($"{ticker}: price {price:F2} < min {rules.MinPrice:F2}");
        if (rules.MinAverageDollarVolume is { } minDollars && averageDollarVolume < minDollars)
            return Reject($"{ticker}: adv {averageDollarVolume:F0} < min {minDollars:F0}");
        if (rules.MinAverageShareVolume is { } minShares && averageShareVolume < minShares)
            return Reject($"{ticker}: average_volume {averageShareVolume:F0} < min {minShares:F0}");
        decimal? atr = rules.AtrPeriod is { } period ? Atr(priorBars, period) : null;
        if (rules.MinAtr is { } minAtr && (atr is null || atr <= minAtr))
            return Reject($"{ticker}: atr {(atr?.ToString("F2") ?? "n/a")} <= min {minAtr:F2}");
        return new(true, new(ticker, price.Value, averageShareVolume, averageDollarVolume, atr, priorBars.Count), null);
    }

    // Wilder ATR from the standard indicator library over every supplied completed session.
    private static decimal? Atr(IReadOnlyList<OhlcvBar> bars, int period)
    {
        if (bars.Count <= period) return null;
        var quotes = bars.Select(bar => new Quote
        {
            Date = bar.Timestamp.UtcDateTime, Open = bar.Open, High = bar.High, Low = bar.Low, Close = bar.Close, Volume = bar.Volume
        });
        return quotes.GetAtr(period).LastOrDefault()?.Atr is { } value ? (decimal)value : null;
    }

    private static DailyScreenResult Reject(string reason) => new(false, null, reason);
}

/// <summary>A split or reverse split: one old share becomes <see cref="Ratio"/> new shares from <see cref="ExDate"/>.</summary>
public sealed record ShareSplit(DateOnly ExDate, decimal Ratio);

/// <summary>Puts earlier sessions on the share basis in force on the decision date, using only splits already effective then.</summary>
public static class SplitBasis
{
    public static IReadOnlyList<OhlcvBar> AsOf(IReadOnlyList<OhlcvBar> bars, IReadOnlyList<ShareSplit> splits, DateOnly decisionDate)
    {
        var effective = splits.Where(split => split.ExDate <= decisionDate && split.Ratio > 0m && split.Ratio != 1m).ToArray();
        if (effective.Length == 0) return bars;
        return bars.Select(bar =>
        {
            var date = DateOnly.FromDateTime(bar.Timestamp.UtcDateTime);
            var ratio = effective.Where(split => split.ExDate > date).Aggregate(1m, (total, split) => total * split.Ratio);
            return ratio == 1m ? bar : bar with
            {
                Open = bar.Open / ratio, High = bar.High / ratio, Low = bar.Low / ratio, Close = bar.Close / ratio,
                Volume = bar.Volume * ratio
            };
        }).ToArray();
    }
}

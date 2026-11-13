using Skender.Stock.Indicators;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Engine.Strategies;

/// <summary>Generic factors on the completed prefix. Benchmark failures are required-data errors, not zero-trade evidence.</summary>
public static class TechnicalFactorCalculator
{
    public static TechnicalFactorEvidence Analyze(TechnicalFactorRules rules,
        IReadOnlyList<OhlcvBar> bars, IReadOnlyList<IndicatorSnapshot> snapshots, int index,
        IReadOnlyList<OhlcvBar>? reference, DateTimeOffset asOfUtc,
        IReadOnlyList<DateOnly>? expectedReferenceSessions = null)
    {
        rules.Validate();
        if (index < 0 || index >= bars.Count || index >= snapshots.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var current = bars[index];
        TechnicalFactorEvidence Result(decimal? value, decimal? context = null, string? reason = null) =>
            new(rules.Metric, rules.LookbackBars, current.Timestamp, value, context, reason);
        switch (rules.Metric)
        {
            case TechnicalFactorRules.AtrDistance:
                return snapshots[index].Sma20 is { } sma && snapshots[index].Atr is > 0
                    ? Result((current.Close - sma) / snapshots[index].Atr!.Value)
                    : Result(null, reason: "sma20_or_positive_atr14_unavailable");
            case TechnicalFactorRules.Roc:
                return index >= rules.LookbackBars && bars[index - rules.LookbackBars].Close > 0
                    ? Result(100 * (current.Close / bars[index - rules.LookbackBars].Close - 1))
                    : Result(null, reason: "roc_history_unavailable");
            case TechnicalFactorRules.Ibs:
                return current.High > current.Low ? Result((current.Close - current.Low) / (current.High - current.Low))
                    : Result(null, reason: "zero_range_bar");
            case TechnicalFactorRules.Connors:
                if (index < 259) return Result(null, reason: "connors_convergence_history_unavailable");
                var quotes = bars.Take(index + 1).Select(bar => new Quote { Date = bar.Timestamp.UtcDateTime,
                    Open = bar.Open, High = bar.High, Low = bar.Low, Close = bar.Close, Volume = bar.Volume });
                var crsi = quotes.GetConnorsRsi(3, 2, 100).Last();
                return new(rules.Metric, 100, current.Timestamp, Finite(crsi.ConnorsRsi),
                    UnavailableReason: Finite(crsi.ConnorsRsi) is null ? "connors_value_unavailable" : null,
                    PriceRsi: Finite(crsi.Rsi), StreakRsi: Finite(crsi.RsiStreak), PercentRank: Finite(crsi.PercentRank));
            case TechnicalFactorRules.Squeeze:
                if (index < rules.LookbackBars + 1 || snapshots[index - 1].BollingerUpper is not > 0)
                    return Result(null, reason: "squeeze_history_unavailable");
                var priorWidth = Width(snapshots[index - 1]);
                var widths = snapshots.Skip(index - rules.LookbackBars - 1).Take(rules.LookbackBars).Select(Width).ToArray();
                if (priorWidth is null || widths.Any(width => width is null)) return Result(null, reason: "squeeze_width_unavailable");
                // Prior width is compared to N earlier widths; ties are not counted as below.
                return Result(100 * (current.Close / snapshots[index - 1].BollingerUpper!.Value - 1),
                    100m * widths.Count(width => width < priorWidth) / rules.LookbackBars);
            case TechnicalFactorRules.RelativeRoc:
                return Relative(rules, bars, index, reference, asOfUtc, expectedReferenceSessions);
            case TechnicalFactorRules.SmaDistance:
                return SmaDistance(rules, bars, index) is { } distance
                    ? Result(distance) : Result(null, reason: "sma_history_unavailable");
            default: throw new InvalidOperationException("Unsupported technical factor.");
        }
    }

    // Percent distance of bar[index]'s close from the simple average of the LookbackBars closes ending at index; null when
    // fewer completed closes exist or the average is not positive.
    private static decimal? SmaDistance(TechnicalFactorRules rules, IReadOnlyList<OhlcvBar> bars, int index)
    {
        if (index + 1 < rules.LookbackBars) return null;
        var sum = 0m;
        for (var i = index - rules.LookbackBars + 1; i <= index; i++) sum += bars[i].Close;
        var average = sum / rules.LookbackBars;
        return average > 0m ? 100m * (bars[index].Close / average - 1m) : null;
    }

    private static decimal? Finite(double? value) => value is { } number && Double.IsFinite(number) ? (decimal)number : null;
    private static decimal? Width(IndicatorSnapshot snapshot) => snapshot.BollingerMiddle is > 0 &&
        snapshot.BollingerUpper is { } upper && snapshot.BollingerLower is { } lower
        ? 100 * (upper - lower) / snapshot.BollingerMiddle.Value : null;

    private static TechnicalFactorEvidence Relative(TechnicalFactorRules rules, IReadOnlyList<OhlcvBar> bars,
        int index, IReadOnlyList<OhlcvBar>? reference, DateTimeOffset asOfUtc, IReadOnlyList<DateOnly>? sessions)
    {
        if (index < rules.LookbackBars || reference is null || sessions is null || sessions.Count != rules.LookbackBars + 1)
            throw new InvalidDataException("Required relative-strength benchmark/calendar evidence is unavailable.");
        var stock = bars.Skip(index - rules.LookbackBars).Take(rules.LookbackBars + 1).ToArray();
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        DateOnly Session(OhlcvBar bar) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bar.Timestamp, zone).DateTime);
        if (!stock.Select(Session).SequenceEqual(sessions)) throw new InvalidDataException("Stock relative-strength span omits an expected completed session.");
        var available = reference.Where(bar => bar.Timestamp.AddDays(1) <= asOfUtc && (bar.KnownAtUtc is null || bar.KnownAtUtc <= asOfUtc))
            .OrderBy(bar => bar.Timestamp).ToArray();
        if (available.Length == 0 || available[^1].Timestamp != stock[^1].Timestamp ||
            available.Select(bar => bar.Timestamp).Distinct().Count() != available.Length)
            throw new InvalidDataException("Required benchmark is stale or has ambiguous versions.");
        var matched = stock.Select(bar => available.SingleOrDefault(item => item.Timestamp == bar.Timestamp)).ToArray();
        if (matched.Any(bar => bar is null) || matched.Any(bar => bar!.Ticker != rules.BenchmarkSymbol ||
            bar.Timeframe != "1d" || bar.DataFeed != stock[^1].DataFeed || bar.AdjustmentPolicy != stock[^1].AdjustmentPolicy || bar.Close <= 0))
            throw new InvalidDataException("Required benchmark sessions or matching provenance are missing.");
        if (stock[0].Close <= 0) throw new InvalidDataException("Stock relative-strength endpoint is invalid.");
        var value = 100 * (stock[^1].Close / stock[0].Close - matched[^1]!.Close / matched[0]!.Close);
        return new(rules.Metric, rules.LookbackBars, stock[^1].Timestamp, value,
            BenchmarkSymbol: rules.BenchmarkSymbol, ReferenceInputs: matched.Select(bar => bar!).ToArray());
    }

    public static IReadOnlyList<DateOnly> ExpectedCompletedSessions(IReadOnlyDictionary<DateOnly, MarketSessionSchedule> calendar,
        DateTimeOffset asOfUtc, int count)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(asOfUtc, zone).DateTime);
        DateTimeOffset Receipt(DateOnly date)
        {
            var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime().AddDays(1);
        }
        var dates = calendar.Where(pair => pair.Value.IsTradingDay && Receipt(pair.Key) <= asOfUtc)
            .Select(pair => pair.Key).Order().TakeLast(count).ToArray();
        if (dates.Length != count || Enumerable.Range(0, today.DayNumber - dates[0].DayNumber + 1)
            .Any(offset => !calendar.ContainsKey(dates[0].AddDays(offset))))
            throw new InvalidDataException("Authoritative benchmark calendar coverage is incomplete.");
        return dates;
    }
}

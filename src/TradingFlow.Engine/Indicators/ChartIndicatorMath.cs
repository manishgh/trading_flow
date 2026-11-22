using System.Globalization;
using Skender.Stock.Indicators;
using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Indicators;

public sealed record ChartFormula(
    string Id, string Family, int Period, string Parameters, int SeedObservations, int WarmObservations, bool Recursive, string[] Columns)
{
    public const string PackageVersion = "skender-2.7.1-chart-v2";
}

public readonly record struct ChartIndicatorClock(DateTimeOffset AvailableAtUtc, DateTimeOffset AnchorUtc, bool Reconstructed);
public sealed record ChartCalculatedIndicator(double?[][] Columns, ChartIndicatorClock?[] Clocks);

/// <summary>Computes only the selected observational formula on one completed, same-basis segment.</summary>
public static class ChartIndicatorMath
{
    private static readonly string[] Value = ["value"];

    // Period bounds a chart accepts.
    private static readonly Dictionary<string, (int Minimum, int Maximum)> Periods = new(StringComparer.Ordinal)
    {
        ["sma"] = (2, 250), ["ema"] = (2, 250), ["bb"] = (5, 100),
        ["rsi"] = (2, 50), ["atr"] = (2, 50), ["atrpct"] = (2, 50), ["adx"] = (2, 40)
    };

    private static readonly Dictionary<string, ChartFormula> Fixed = new(StringComparer.Ordinal)
    {
        ["macd"] = new("macd", "macd", 0, "12,26,9", 34, 285, true, ["macd", "signal", "histogram"]),
        ["obv"] = new("obv", "obv", 0, "close change; zero origin", 1, 1, true, Value)
    };

    /// <summary>Reads an ID such as "ema21" into its formula; null when the family is unknown or the period is out of bounds.</summary>
    public static ChartFormula? Find(string id)
    {
        if (Fixed.TryGetValue(id, out var formula)) return formula;
        var family = id.TrimEnd("0123456789".ToCharArray());
        var digits = id[family.Length..];
        if (!Periods.TryGetValue(family, out var bounds) || digits.Length is 0 or > 3 || digits[0] == '0' ||
            !Int32.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var period) ||
            period < bounds.Minimum || period > bounds.Maximum) return null;
        return Build(id, family, period);
    }

    // States the first package output and the later candle from which the chart shows the value (see RunIn and WilderRunIn).
    private static ChartFormula Build(string id, string family, int period) => family switch
    {
        "sma" => new(id, family, period, $"{period}", period, period, false, Value),
        "ema" => new(id, family, period, $"{period}", period, period + RunIn(period), true, Value),
        "bb" => new(id, family, period, $"{period},2", period, period, false, ["lower", "middle", "upper"]),
        "adx" => new(id, family, period, $"{period}", 2 * period, 2 * period + 2 * WilderRunIn(period), true, ["adx", "pdi", "mdi"]),
        _ => new(id, family, period, $"{period}", period + 1, period + 1 + WilderRunIn(period), true, Value)
    };

    // Candles after which less than 1% of an EMA still comes from its starting average: (1 - 2/(n+1))^k < 0.01. At most 250, and
    // at most 470 candles in total, so the longest averages still fit the 500 daily candles a chart loads. Averages above 107
    // periods therefore start with more of it: about 8% at EMA 200 and 17% at EMA 250.
    private static int RunIn(int period) => Math.Min(Math.Min(250, 470 - period), (int)Math.Ceiling(2.3 * (period + 1)));

    // The same 1% for Wilder smoothing, which keeps (1 - 1/n) of the old value per candle.
    private static int WilderRunIn(int period) => (int)Math.Ceiling(4.6 * period);

    // Calculate invariant source points, including the full warm-up receipt and each independently anchored segment.
    public static ChartCalculatedIndicator Project(ChartFormula formula, IReadOnlyList<ChartCompletedBar> bars,
        ChartBarProjector calendar, DateOnly firstCoveredDate, bool daily, DateTimeOffset visibleFrom,
        CancellationToken cancellationToken, int intervalMinutes = 1, bool startsAtListing = false)
    {
        var values = formula.Columns.Select(_ => new double?[bars.Count]).ToArray();
        var clocks = new ChartIndicatorClock?[bars.Count];
        var publishStart = 0;
        while (publishStart < bars.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var publishEnd = PublishEnd(formula, bars, publishStart, daily);
            if (bars[publishEnd - 1].EndUtc >= visibleFrom)
            {
                var inputStart = InputStart(formula, bars, publishStart, firstCoveredDate, calendar, daily, intervalMinutes, startsAtListing);
                if (inputStart is { } start)
                    PublishSegment(formula, bars, start, publishStart, publishEnd, values, clocks);
            }
            publishStart = publishEnd;
        }
        return new ChartCalculatedIndicator(values, clocks);
    }

    // Unknown coverage and unproved basis changes always break a segment. Recursive formulas also break at a fixed block.
    private static int PublishEnd(ChartFormula formula, IReadOnlyList<ChartCompletedBar> bars, int start, bool daily)
    {
        var end = start + 1;
        var anchor = ChartBarProjector.IndicatorBlock(bars[start].SessionDate, daily);
        while (end < bars.Count && bars[end].Continuity == bars[start].Continuity &&
            (!formula.Recursive || ChartBarProjector.IndicatorBlock(bars[end].SessionDate, daily) == anchor))
        {
            end++;
        }
        return end;
    }

    // A left-clipped recursive seed is unavailable; an observed coverage break inside the bounded source can restart it.
    // With proof that the source has no earlier candle (startsAtListing), a seed before the first candle starts at that candle.
    private static int? InputStart(ChartFormula formula, IReadOnlyList<ChartCompletedBar> bars, int publishStart,
        DateOnly firstCoveredDate, ChartBarProjector calendar, bool daily, int intervalMinutes, bool startsAtListing)
    {
        if (!formula.Recursive) return publishStart;
        var current = bars[publishStart];
        // OBV has no run-in; it accumulates from a fixed 365 daily or 12 intraday sessions before the block.
        var reach = formula.Family != "obv" ? formula.WarmObservations : daily ? 360 : 12 * 390;
        var seed = calendar.IndicatorSeed(current.SessionDate, daily, reach, intervalMinutes);
        if (seed is null) return null;
        var start = publishStart;
        while (start > 0 && bars[start - 1].Continuity == current.Continuity && bars[start - 1].SessionDate >= seed)
            start--;
        var verifiedSeed = firstCoveredDate <= seed && bars[start].SessionDate == seed;
        var coveredRestart = start > 0 && bars[start - 1].SessionDate >= seed &&
            bars[start - 1].Continuity != current.Continuity;
        var firstCandle = startsAtListing && start == 0 && firstCoveredDate <= seed && bars[0].SessionDate > seed;
        return verifiedSeed || coveredRestart || firstCandle ? start : null;
    }

    // Track dependencies with a descending receipt deque for fixed windows and a prefix maximum for recursive series.
    private static void PublishSegment(ChartFormula formula, IReadOnlyList<ChartCompletedBar> bars, int start,
        int publishStart, int end, double?[][] values, ChartIndicatorClock?[] clocks)
    {
        if (end - start < formula.WarmObservations) return;
        var source = Calculate(formula, bars, start, end - start);
        var receipts = new int[formula.Recursive ? 1 : formula.SeedObservations + 1];
        var head = 0;
        var tail = 0;
        var latest = bars[start].AvailableAtUtc;
        var reconstructed = 0;
        for (var index = start; index < end; index++)
        {
            if (bars[index].Bar.KnownAtUtc is null) reconstructed++;
            if (formula.Recursive)
            {
                if (bars[index].AvailableAtUtc > latest) latest = bars[index].AvailableAtUtc;
            }
            else
            {
                var expired = index - formula.SeedObservations;
                if (expired >= start && bars[expired].Bar.KnownAtUtc is null) reconstructed--;
                if (head < tail && receipts[head % receipts.Length] <= expired) head++;
                while (head < tail && bars[receipts[(tail - 1) % receipts.Length]].AvailableAtUtc <= bars[index].AvailableAtUtc)
                    tail--;
                receipts[tail++ % receipts.Length] = index;
                latest = bars[receipts[head % receipts.Length]].AvailableAtUtc;
            }
            if (index < publishStart) continue;
            var hasValue = false;
            for (var column = 0; column < source.Length; column++)
            {
                values[column][index] = source[column][index - start];
                hasValue |= values[column][index].HasValue;
            }
            if (hasValue)
                clocks[index] = new ChartIndicatorClock(latest, bars[start].Bar.Timestamp, reconstructed > 0);
        }
    }

    // Compute causal aligned values. Null output represents warm-up or an undefined package result.
    public static double?[][] Calculate(ChartFormula formula, IReadOnlyList<ChartCompletedBar> bars,
        int start, int length)
    {
        var values = formula.Columns.Select(_ => new double?[length]).ToArray();
        if (length < formula.WarmObservations) return values;
        var quotes = new Quote[length];
        for (var index = 0; index < length; index++)
        {
            var bar = bars[start + index].Bar;
            quotes[index] = new Quote
            {
                Date = bar.Timestamp.UtcDateTime, Open = bar.Open, High = bar.High, Low = bar.Low,
                Close = bar.Close, Volume = bar.Volume
            };
        }
        switch (formula.Family)
        {
            case "sma":
                Copy(quotes.GetSma(formula.Period), formula, values, static (row, _, _) => row.Sma);
                break;
            case "ema":
                Copy(quotes.GetEma(formula.Period), formula, values, static (row, _, _) => row.Ema);
                break;
            case "rsi":
                Copy(quotes.GetRsi(formula.Period), formula, values, static (row, _, _) => row.Rsi);
                break;
            case "macd":
                Copy(quotes.GetMacd(12, 26, 9), formula, values,
                    static (row, _, column) => column == 0 ? row.Macd : column == 1 ? row.Signal : row.Histogram);
                break;
            case "adx":
                Copy(quotes.GetAdx(formula.Period), formula, values,
                    static (row, _, column) => column == 0 ? row.Adx : column == 1 ? row.Pdi : row.Mdi);
                break;
            case "atr":
                Copy(quotes.GetAtr(formula.Period), formula, values, static (row, _, _) => row.Atr);
                break;
            case "atrpct":
                Copy(quotes.GetAtr(formula.Period), formula, values, (row, index, _) =>
                    quotes[index].Close > 0 ? 100 * row.Atr / (double)quotes[index].Close : null);
                break;
            case "bb":
                Copy(quotes.GetBollingerBands(formula.Period, 2), formula, values,
                    static (row, _, column) => column == 0 ? row.LowerBand : column == 1 ? row.Sma : row.UpperBand);
                break;
            case "obv":
                Copy(quotes.GetObv(), formula, values, static (row, _, _) => row.Obv);
                break;
            default:
                throw new ArgumentException("Unsupported chart formula.", nameof(formula));
        }
        return values;
    }

    // Retain only finite package outputs beyond the display convergence threshold.
    private static void Copy<T>(IEnumerable<T> rows, ChartFormula formula, double?[][] values,
        Func<T, int, int, double?> select)
    {
        var index = 0;
        foreach (var row in rows)
        {
            if (index >= formula.WarmObservations - 1)
            {
                for (var column = 0; column < values.Length; column++)
                    if (select(row, index, column) is { } value && Double.IsFinite(value))
                        values[column][index] = value;
            }
            index++;
        }
    }

    // Reuse the production cumulative same-time median. Partial cohorts and zero denominators are undefined.
    public static decimal? CumulativeRelativeVolume(decimal current, IReadOnlyCollection<decimal> earlierSessions) =>
        earlierSessions.Count == MarketEvidenceProfile.DefaultLookbackSessions
            ? RelativeVolume.Ratio(current, RelativeVolume.Baseline(earlierSessions, VolumeBaselineAverage.Median))
            : null;
}

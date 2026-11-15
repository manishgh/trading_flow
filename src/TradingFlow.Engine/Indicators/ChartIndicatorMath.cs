using Skender.Stock.Indicators;
using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Indicators;

public sealed record ChartFormula(
    string Id, string Parameters, int SeedObservations, int WarmObservations, bool Recursive, string[] Columns)
{
    public const string PackageVersion = "skender-2.7.1-chart-v1";
}

public readonly record struct ChartIndicatorClock(DateTimeOffset AvailableAtUtc, DateTimeOffset AnchorUtc, bool Reconstructed);
public sealed record ChartCalculatedIndicator(double?[][] Columns, ChartIndicatorClock?[] Clocks);

/// <summary>Computes only the selected observational formula on one completed, same-basis segment.</summary>
public static class ChartIndicatorMath
{
    private static readonly string[] Value = ["value"];

    // Separate the first package output from the conservative threshold at which charts expose it.
    private static readonly Dictionary<string, ChartFormula> Formulas = new(StringComparer.Ordinal)
    {
        ["sma20"] = new("sma20", "20", 20, 20, false, Value),
        ["sma50"] = new("sma50", "50", 50, 50, false, Value),
        ["sma200"] = new("sma200", "200", 200, 200, false, Value),
        ["ema9"] = new("ema9", "9", 9, 259, true, Value),
        ["ema20"] = new("ema20", "20", 20, 270, true, Value),
        ["ema50"] = new("ema50", "50", 50, 300, true, Value),
        ["ema200"] = new("ema200", "200", 200, 450, true, Value),
        ["rsi14"] = new("rsi14", "14", 15, 140, true, Value),
        ["macd"] = new("macd", "12,26,9", 34, 285, true, ["macd", "signal", "histogram"]),
        ["adx14"] = new("adx14", "14", 28, 264, true, ["adx", "pdi", "mdi"]),
        ["atr14"] = new("atr14", "14", 15, 264, true, Value),
        ["atrpct14"] = new("atrpct14", "14", 15, 264, true, Value),
        ["bb20"] = new("bb20", "20,2", 20, 20, false, ["lower", "middle", "upper"]),
        ["obv"] = new("obv", "close change; zero origin", 1, 1, true, Value)
    };

    // A finite formula catalog keeps parameter selection out of strategy configuration.
    public static ChartFormula? Find(string id) => Formulas.GetValueOrDefault(id);

    // Calculate invariant source points, including the full warm-up receipt and each independently anchored segment.
    public static ChartCalculatedIndicator Project(ChartFormula formula, IReadOnlyList<ChartCompletedBar> bars,
        ChartBarProjector calendar, DateOnly firstCoveredDate, bool daily, DateTimeOffset visibleFrom,
        CancellationToken cancellationToken)
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
                var inputStart = InputStart(formula, bars, publishStart, firstCoveredDate, calendar, daily);
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
    private static int? InputStart(ChartFormula formula, IReadOnlyList<ChartCompletedBar> bars, int publishStart,
        DateOnly firstCoveredDate, ChartBarProjector calendar, bool daily)
    {
        if (!formula.Recursive) return publishStart;
        var current = bars[publishStart];
        var seed = calendar.IndicatorSeed(current.SessionDate, daily, formula.WarmObservations);
        if (seed is null) return null;
        var start = publishStart;
        while (start > 0 && bars[start - 1].Continuity == current.Continuity && bars[start - 1].SessionDate >= seed)
            start--;
        var verifiedSeed = firstCoveredDate <= seed && bars[start].SessionDate == seed;
        var coveredRestart = start > 0 && bars[start - 1].SessionDate >= seed &&
            bars[start - 1].Continuity != current.Continuity;
        return verifiedSeed || coveredRestart ? start : null;
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
        switch (formula.Id)
        {
            case "sma20" or "sma50" or "sma200":
                Copy(quotes.GetSma(formula.SeedObservations), formula, values, static (row, _, _) => row.Sma);
                break;
            case "ema9" or "ema20" or "ema50" or "ema200":
                Copy(quotes.GetEma(formula.SeedObservations), formula, values, static (row, _, _) => row.Ema);
                break;
            case "rsi14":
                Copy(quotes.GetRsi(14), formula, values, static (row, _, _) => row.Rsi);
                break;
            case "macd":
                Copy(quotes.GetMacd(12, 26, 9), formula, values,
                    static (row, _, column) => column == 0 ? row.Macd : column == 1 ? row.Signal : row.Histogram);
                break;
            case "adx14":
                Copy(quotes.GetAdx(14), formula, values,
                    static (row, _, column) => column == 0 ? row.Adx : column == 1 ? row.Pdi : row.Mdi);
                break;
            case "atr14":
                Copy(quotes.GetAtr(14), formula, values, static (row, _, _) => row.Atr);
                break;
            case "atrpct14":
                Copy(quotes.GetAtr(14), formula, values, (row, index, _) =>
                    quotes[index].Close > 0 ? 100 * row.Atr / (double)quotes[index].Close : null);
                break;
            case "bb20":
                Copy(quotes.GetBollingerBands(20, 2), formula, values,
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

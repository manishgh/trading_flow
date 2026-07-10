using TradingFlow.Domain.Market;

namespace TradingFlow.Backtesting.Research;

/// <summary>
/// Inverted event study for the mean-reversion archetype (docs/strategy-design-doctrine.md §4/§6C,
/// "down today -> up tomorrow"). For each cached daily series it finds "stretch" events (>=N consecutive
/// down closes, RSI(2) oversold, or a close below the lower Bollinger band) and measures 1-5 day forward
/// returns, split by whether the stock is above or below its 200-day SMA. It answers the evidence-gate
/// question before any Archetype-C engine code is written: does buying weakness pay in an uptrend
/// (Connors/Chan) and fail below the 200dma? Triggers use only data up to the event day; the forward
/// returns are the measured outcome, not a tradeable lookahead.
/// </summary>
public sealed class ReversionResearchAnalyzer
{
    public ReversionResearchReport Analyze(
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> dailyBarsByTicker,
        ReversionResearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dailyBarsByTicker);
        options ??= new ReversionResearchOptions();
        var horizons = options.Horizons;

        var observations = new List<Observation>();
        var tickers = new List<string>();
        var totalBarsEvaluated = 0;

        foreach (var entry in dailyBarsByTicker.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var bars = entry.Value.OrderBy(bar => bar.Timestamp).ToArray();
            if (bars.Length < options.SmaTrendPeriod + 2)
            {
                continue;
            }

            tickers.Add(entry.Key.ToUpperInvariant());
            var closes = Array.ConvertAll(bars, bar => bar.Close);
            var rsi = ComputeRsi(closes, options.RsiPeriod);

            for (var i = options.SmaTrendPeriod; i < bars.Length - 1; i++)
            {
                totalBarsEvaluated++;

                var isConsecutiveDown = ConsecutiveDownCloses(closes, i) >= options.ConsecutiveDownDays;
                var isRsiOversold = rsi[i] is { } value && value < options.RsiOversoldThreshold;
                var (_, lowerBand) = Bollinger(closes, i, options.BollingerPeriod, options.BollingerStdDevMultiple);
                var isBelowLowerBollinger = lowerBand is { } band && closes[i] < band;

                // Record EVERY evaluated bar (stretch or not) so the report can compare stretch-day forward
                // returns against the unconditional baseline drift — otherwise a bull-market sample makes
                // "buy any dip" look good simply because the market rose.
                var sma = Sma(closes, i, options.SmaTrendPeriod);
                var aboveTrend = sma is { } trend && closes[i] > trend;

                var forward = new Dictionary<int, decimal>();
                foreach (var horizon in horizons)
                {
                    if (i + horizon < bars.Length)
                    {
                        forward[horizon] = PercentChange(closes[i], closes[i + horizon]);
                    }
                }

                observations.Add(new Observation(isConsecutiveDown, isRsiOversold, isBelowLowerBollinger, aboveTrend, forward));
            }
        }

        var triggers = new (string Name, Func<Observation, bool> Match)[]
        {
            ("baseline_all_bars", _ => true),
            ("any_stretch", IsStretch),
            ("consecutive_down", o => o.ConsecutiveDown),
            ("rsi_oversold", o => o.RsiOversold),
            ("below_lower_bollinger", o => o.BelowLowerBollinger),
        };
        var regimes = new (string Name, Func<Observation, bool> Match)[]
        {
            ("all", _ => true),
            ("above_trend", o => o.AboveTrend),
            ("below_trend", o => !o.AboveTrend),
        };

        var cohorts = new List<ReversionCohort>();
        foreach (var (triggerName, triggerMatch) in triggers)
        {
            foreach (var (regimeName, regimeMatch) in regimes)
            {
                var matched = observations.Where(o => triggerMatch(o) && regimeMatch(o)).ToArray();
                if (matched.Length == 0)
                {
                    continue;
                }

                var stats = horizons.Select(horizon => BuildHorizon(horizon, matched)).ToArray();
                cohorts.Add(new ReversionCohort(triggerName, regimeName, matched.Length, stats));
            }
        }

        return new ReversionResearchReport(
            tickers,
            totalBarsEvaluated,
            observations.Count(IsStretch),
            options,
            cohorts);
    }

    private static bool IsStretch(Observation observation) =>
        observation.ConsecutiveDown || observation.RsiOversold || observation.BelowLowerBollinger;

    private static ReversionHorizonStat BuildHorizon(int horizon, IReadOnlyList<Observation> matched)
    {
        var returns = matched
            .Where(o => o.Forward.ContainsKey(horizon))
            .Select(o => o.Forward[horizon])
            .OrderBy(value => value)
            .ToArray();
        if (returns.Length == 0)
        {
            return new ReversionHorizonStat(horizon, 0, 0m, 0m, 0m);
        }

        var mean = returns.Average();
        var median = returns.Length % 2 == 1
            ? returns[returns.Length / 2]
            : (returns[(returns.Length / 2) - 1] + returns[returns.Length / 2]) / 2m;
        var winRate = (decimal)returns.Count(value => value > 0m) / returns.Length * 100m;
        return new ReversionHorizonStat(horizon, returns.Length, Round(mean), Round(median), Round(winRate));
    }

    private static int ConsecutiveDownCloses(decimal[] closes, int index)
    {
        var count = 0;
        for (var j = index; j >= 1 && closes[j] < closes[j - 1]; j--)
        {
            count++;
        }

        return count;
    }

    private static decimal?[] ComputeRsi(decimal[] closes, int period)
    {
        var rsi = new decimal?[closes.Length];
        if (closes.Length <= period)
        {
            return rsi;
        }

        decimal avgGain = 0m;
        decimal avgLoss = 0m;
        for (var i = 1; i <= period; i++)
        {
            var change = closes[i] - closes[i - 1];
            if (change >= 0m)
            {
                avgGain += change;
            }
            else
            {
                avgLoss -= change;
            }
        }

        avgGain /= period;
        avgLoss /= period;
        rsi[period] = RsiValue(avgGain, avgLoss);

        for (var i = period + 1; i < closes.Length; i++)
        {
            var change = closes[i] - closes[i - 1];
            var gain = change > 0m ? change : 0m;
            var loss = change < 0m ? -change : 0m;
            avgGain = ((avgGain * (period - 1)) + gain) / period;
            avgLoss = ((avgLoss * (period - 1)) + loss) / period;
            rsi[i] = RsiValue(avgGain, avgLoss);
        }

        return rsi;
    }

    private static decimal RsiValue(decimal avgGain, decimal avgLoss)
    {
        return avgLoss == 0m ? 100m : 100m - (100m / (1m + (avgGain / avgLoss)));
    }

    private static decimal? Sma(decimal[] closes, int index, int period)
    {
        if (index + 1 < period)
        {
            return null;
        }

        decimal sum = 0m;
        for (var j = index - period + 1; j <= index; j++)
        {
            sum += closes[j];
        }

        return sum / period;
    }

    private static (decimal? Middle, decimal? Lower) Bollinger(decimal[] closes, int index, int period, decimal stdDevMultiple)
    {
        var middle = Sma(closes, index, period);
        if (middle is not { } mid)
        {
            return (null, null);
        }

        decimal sumSquares = 0m;
        for (var j = index - period + 1; j <= index; j++)
        {
            var deviation = closes[j] - mid;
            sumSquares += deviation * deviation;
        }

        var stdDev = (decimal)Math.Sqrt((double)(sumSquares / period));
        return (mid, mid - (stdDevMultiple * stdDev));
    }

    private static decimal PercentChange(decimal start, decimal end)
    {
        return start == 0m ? 0m : ((end - start) / start) * 100m;
    }

    private static decimal Round(decimal value) => Decimal.Round(value, 4);

    private sealed record Observation(
        bool ConsecutiveDown,
        bool RsiOversold,
        bool BelowLowerBollinger,
        bool AboveTrend,
        IReadOnlyDictionary<int, decimal> Forward);
}

public sealed record ReversionResearchOptions(
    decimal RsiOversoldThreshold = 10m,
    int RsiPeriod = 2,
    int ConsecutiveDownDays = 3,
    int SmaTrendPeriod = 200,
    int BollingerPeriod = 20,
    decimal BollingerStdDevMultiple = 2.0m,
    IReadOnlyList<int>? ForwardHorizons = null)
{
    public IReadOnlyList<int> Horizons =>
        ForwardHorizons is { Count: > 0 } ? ForwardHorizons : new[] { 1, 2, 3, 5 };
}

public sealed record ReversionHorizonStat(
    int Days,
    int Count,
    decimal MeanReturnPct,
    decimal MedianReturnPct,
    decimal WinRatePct);

public sealed record ReversionCohort(
    string Trigger,
    string Regime,
    int Events,
    IReadOnlyList<ReversionHorizonStat> Horizons);

public sealed record ReversionResearchReport(
    IReadOnlyList<string> Tickers,
    int TotalBarsEvaluated,
    int TotalStretchEvents,
    ReversionResearchOptions Options,
    IReadOnlyList<ReversionCohort> Cohorts);

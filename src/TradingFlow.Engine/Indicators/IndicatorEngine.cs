using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Indicators;

public sealed class IndicatorEngine
{
    private const int RsiPeriod = 14;
    private const int AtrPeriod = 14;
    private const int BollingerPeriod = 20;
    private const int RelativeVolumeLookbackSessions = 20;
    private const int RelativeVolumeMinimumComparableBars = 5;
    private static readonly TimeZoneInfo ExchangeTimeZone = ResolveExchangeTimeZone();

    public IReadOnlyList<IndicatorSnapshot> Compute(IReadOnlyList<OhlcvBar> inputBars)
    {
        var bars = inputBars.OrderBy(x => x.Timestamp).ToArray();
        if (bars.Length == 0)
        {
            return Array.Empty<IndicatorSnapshot>();
        }

        var closes = bars.Select(x => x.Close).ToArray();
        var volumes = bars.Select(x => x.Volume).ToArray();
        var sma10 = ComputeSma(closes, 10);
        var sma20 = ComputeSma(closes, 20);
        var sma50 = ComputeSma(closes, 50);
        var ema20 = ComputeEma(closes, 20);
        var ema50 = ComputeEma(closes, 50);
        var ema200 = ComputeEma(closes, 200);
        var ema12 = ComputeEma(closes, 12);
        var ema26 = ComputeEma(closes, 26);
        var macdLine = ComputeMacdLine(ema12, ema26);
        var macdSignal = ComputeNullableEma(macdLine, 9);
        var rsi = ComputeRsi(closes);
        var atr = ComputeAtr(bars);
        var bollinger = ComputeBollinger(closes);
        var relativeVolume = ComputeRelativeVolume(bars);
        var vwap = ComputeVwap(bars);

        var snapshots = new List<IndicatorSnapshot>(bars.Length);
        for (var i = 0; i < bars.Length; i++)
        {
            snapshots.Add(new IndicatorSnapshot(
                bars[i].Ticker,
                bars[i].Timestamp,
                bars[i].Timeframe,
                bars[i].Close,
                bars[i].Volume,
                vwap[i],
                rsi[i],
                atr[i],
                ema20[i],
                ema50[i],
                ema200[i],
                bollinger.Middle[i],
                bollinger.Upper[i],
                bollinger.Lower[i],
                relativeVolume[i],
                macdLine[i],
                macdSignal[i],
                macdLine[i] is null || macdSignal[i] is null ? null : macdLine[i] - macdSignal[i],
                Sma10: sma10[i],
                Sma20: sma20[i],
                Sma50: sma50[i]));
        }

        return snapshots;
    }

    private static decimal?[] ComputeSma(IReadOnlyList<decimal> values, int period)
    {
        var output = new decimal?[values.Count];
        if (values.Count < period)
        {
            return output;
        }

        var runningSum = 0m;
        for (var i = 0; i < values.Count; i++)
        {
            runningSum += values[i];
            if (i >= period)
            {
                runningSum -= values[i - period];
            }

            if (i >= period - 1)
            {
                output[i] = runningSum / period;
            }
        }

        return output;
    }

    private static decimal?[] ComputeEma(IReadOnlyList<decimal> values, int period)
    {
        var output = new decimal?[values.Count];
        if (values.Count < period)
        {
            return output;
        }

        var seed = values.Take(period).Average();
        output[period - 1] = seed;
        var multiplier = 2m / (period + 1);

        for (var i = period; i < values.Count; i++)
        {
            output[i] = ((values[i] - output[i - 1]!.Value) * multiplier) + output[i - 1]!.Value;
        }

        return output;
    }

    private static decimal?[] ComputeMacdLine(IReadOnlyList<decimal?> ema12, IReadOnlyList<decimal?> ema26)
    {
        var output = new decimal?[ema12.Count];
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = ema12[i] is null || ema26[i] is null ? null : ema12[i] - ema26[i];
        }

        return output;
    }

    private static decimal?[] ComputeNullableEma(IReadOnlyList<decimal?> values, int period)
    {
        var output = new decimal?[values.Count];
        var available = new List<(int Index, decimal Value)>();
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is { } value)
            {
                available.Add((i, value));
            }
        }

        if (available.Count < period)
        {
            return output;
        }

        var seed = available.Take(period).Average(x => x.Value);
        var seedIndex = available[period - 1].Index;
        output[seedIndex] = seed;
        var previous = seed;
        var multiplier = 2m / (period + 1);

        foreach (var point in available.Skip(period))
        {
            previous = ((point.Value - previous) * multiplier) + previous;
            output[point.Index] = previous;
        }

        return output;
    }

    private static decimal?[] ComputeRsi(IReadOnlyList<decimal> closes)
    {
        var output = new decimal?[closes.Count];
        if (closes.Count <= RsiPeriod)
        {
            return output;
        }

        var gain = 0m;
        var loss = 0m;
        for (var i = 1; i <= RsiPeriod; i++)
        {
            var delta = closes[i] - closes[i - 1];
            if (delta >= 0)
            {
                gain += delta;
            }
            else
            {
                loss -= delta;
            }
        }

        var averageGain = gain / RsiPeriod;
        var averageLoss = loss / RsiPeriod;
        output[RsiPeriod] = CalculateRsi(averageGain, averageLoss);

        for (var i = RsiPeriod + 1; i < closes.Count; i++)
        {
            var delta = closes[i] - closes[i - 1];
            var currentGain = Math.Max(delta, 0);
            var currentLoss = Math.Max(-delta, 0);
            averageGain = ((averageGain * (RsiPeriod - 1)) + currentGain) / RsiPeriod;
            averageLoss = ((averageLoss * (RsiPeriod - 1)) + currentLoss) / RsiPeriod;
            output[i] = CalculateRsi(averageGain, averageLoss);
        }

        return output;
    }

    private static decimal CalculateRsi(decimal averageGain, decimal averageLoss)
    {
        if (averageLoss == 0)
        {
            return 100;
        }

        var relativeStrength = averageGain / averageLoss;
        return 100 - (100 / (1 + relativeStrength));
    }

    private static decimal?[] ComputeAtr(IReadOnlyList<OhlcvBar> bars)
    {
        var output = new decimal?[bars.Count];
        if (bars.Count <= AtrPeriod)
        {
            return output;
        }

        var trueRanges = new decimal[bars.Count];
        trueRanges[0] = bars[0].High - bars[0].Low;
        for (var i = 1; i < bars.Count; i++)
        {
            var highLow = bars[i].High - bars[i].Low;
            var highClose = Math.Abs(bars[i].High - bars[i - 1].Close);
            var lowClose = Math.Abs(bars[i].Low - bars[i - 1].Close);
            trueRanges[i] = Math.Max(highLow, Math.Max(highClose, lowClose));
        }

        var atr = trueRanges.Skip(1).Take(AtrPeriod).Average();
        output[AtrPeriod] = atr;

        for (var i = AtrPeriod + 1; i < bars.Count; i++)
        {
            atr = ((atr * (AtrPeriod - 1)) + trueRanges[i]) / AtrPeriod;
            output[i] = atr;
        }

        return output;
    }

    private static (decimal?[] Middle, decimal?[] Upper, decimal?[] Lower) ComputeBollinger(IReadOnlyList<decimal> closes)
    {
        var middle = new decimal?[closes.Count];
        var upper = new decimal?[closes.Count];
        var lower = new decimal?[closes.Count];

        for (var i = BollingerPeriod - 1; i < closes.Count; i++)
        {
            var window = closes.Skip(i - BollingerPeriod + 1).Take(BollingerPeriod).ToArray();
            var average = window.Average();
            var variance = window.Select(x => Math.Pow((double)(x - average), 2)).Average();
            var standardDeviation = (decimal)Math.Sqrt(variance);
            middle[i] = average;
            upper[i] = average + (2 * standardDeviation);
            lower[i] = average - (2 * standardDeviation);
        }

        return (middle, upper, lower);
    }

    private static decimal?[] ComputeRelativeVolume(IReadOnlyList<OhlcvBar> bars)
    {
        var output = new decimal?[bars.Count];
        var volumesBySlot = new Dictionary<TimeSpan, List<decimal>>();

        for (var i = 0; i < bars.Count; i++)
        {
            var slot = TimeZoneInfo.ConvertTime(bars[i].Timestamp, ExchangeTimeZone).TimeOfDay;
            if (!volumesBySlot.TryGetValue(slot, out var comparableVolumes))
            {
                comparableVolumes = new List<decimal>();
                volumesBySlot[slot] = comparableVolumes;
            }

            if (comparableVolumes.Count >= RelativeVolumeMinimumComparableBars)
            {
                var lookback = comparableVolumes
                    .Skip(Math.Max(0, comparableVolumes.Count - RelativeVolumeLookbackSessions))
                    .ToArray();
                var average = lookback.Average();
                output[i] = average <= 0 ? null : bars[i].Volume / average;
            }

            comparableVolumes.Add(bars[i].Volume);
        }

        return output;
    }

    private static decimal?[] ComputeVwap(IReadOnlyList<OhlcvBar> bars)
    {
        var output = new decimal?[bars.Count];
        DateOnly? activeDate = null;
        decimal cumulativePriceVolume = 0;
        decimal cumulativeVolume = 0;

        for (var i = 0; i < bars.Count; i++)
        {
            var barDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bars[i].Timestamp, ExchangeTimeZone).DateTime);
            if (activeDate != barDate)
            {
                activeDate = barDate;
                cumulativePriceVolume = 0;
                cumulativeVolume = 0;
            }

            var typicalPrice = (bars[i].High + bars[i].Low + bars[i].Close) / 3m;
            cumulativePriceVolume += typicalPrice * bars[i].Volume;
            cumulativeVolume += bars[i].Volume;
            output[i] = cumulativeVolume <= 0 ? null : cumulativePriceVolume / cumulativeVolume;
        }

        return output;
    }

    private static TimeZoneInfo ResolveExchangeTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }
}

using TradingFlow.Domain.Market;
using Skender.Stock.Indicators;

namespace TradingFlow.Engine.Indicators;

public sealed class IndicatorEngine
{
    private const int RsiPeriod = 14;
    private const int AtrPeriod = 14;
    private const int BollingerPeriod = 20;
    private const int RelativeVolumeLookbackSessions = 63;
    private const int RelativeVolumeMinimumComparableBars = 5;
    private static readonly TimeZoneInfo ExchangeTimeZone = ResolveExchangeTimeZone();

    public IReadOnlyList<IndicatorSnapshot> Compute(IReadOnlyList<OhlcvBar> inputBars)
    {
        var bars = inputBars.OrderBy(x => x.Timestamp).ToArray();
        if (bars.Length == 0)
        {
            return Array.Empty<IndicatorSnapshot>();
        }

        var standardIndicators = ComputeStandardIndicators(bars);
        var cumulativeVolumeBaseline = ComputeCumulativeVolumeBaseline(bars);
        var slotVolumeBaseline = ComputeSlotVolumeBaseline(bars);
        var sessionVolumeBaseline = ComputeSessionVolumeBaseline(bars);
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
                standardIndicators.Rsi[i],
                standardIndicators.Atr[i],
                standardIndicators.Ema20[i],
                standardIndicators.Ema50[i],
                standardIndicators.Ema200[i],
                standardIndicators.BollingerMiddle[i],
                standardIndicators.BollingerUpper[i],
                standardIndicators.BollingerLower[i],
                cumulativeVolumeBaseline.RelativeVolume[i],
                standardIndicators.MacdLine[i],
                standardIndicators.MacdSignal[i],
                standardIndicators.MacdHistogram[i],
                Sma10: standardIndicators.Sma10[i],
                Sma20: standardIndicators.Sma20[i],
                Sma50: standardIndicators.Sma50[i],
                SlotRelativeVolume: slotVolumeBaseline.RelativeVolume[i],
                SessionRelativeVolume: sessionVolumeBaseline.RelativeVolume[i],
                Ema10: standardIndicators.Ema10[i],
                SlotAverageVolume: slotVolumeBaseline.AverageVolume[i],
                CumulativeAverageVolume: cumulativeVolumeBaseline.AverageVolume[i],
                AverageSessionVolume: sessionVolumeBaseline.AverageVolume[i],
                RelativeVolumeSampleCount: cumulativeVolumeBaseline.SampleCount[i],
                Sma150: standardIndicators.Sma150[i],
                Sma200: standardIndicators.Sma200[i],
                Ema5: standardIndicators.Ema5[i]));
        }

        return snapshots;
    }

    private static StandardIndicatorSeries ComputeStandardIndicators(IReadOnlyList<OhlcvBar> bars)
    {
        var quotes = bars.Select(bar => new Quote
        {
            Date = bar.Timestamp.UtcDateTime,
            Open = bar.Open,
            High = bar.High,
            Low = bar.Low,
            Close = bar.Close,
            Volume = bar.Volume
        }).ToArray();

        var sma10 = quotes.GetSma(10).Select(x => ToDecimal(x.Sma)).ToArray();
        var sma20 = quotes.GetSma(20).Select(x => ToDecimal(x.Sma)).ToArray();
        var sma50 = quotes.GetSma(50).Select(x => ToDecimal(x.Sma)).ToArray();
        var sma150 = quotes.GetSma(150).Select(x => ToDecimal(x.Sma)).ToArray();
        var sma200 = quotes.GetSma(200).Select(x => ToDecimal(x.Sma)).ToArray();
        var ema5 = quotes.GetEma(5).Select(x => ToDecimal(x.Ema)).ToArray();
        var ema10 = quotes.GetEma(10).Select(x => ToDecimal(x.Ema)).ToArray();
        var ema20 = quotes.GetEma(20).Select(x => ToDecimal(x.Ema)).ToArray();
        var ema50 = quotes.GetEma(50).Select(x => ToDecimal(x.Ema)).ToArray();
        var ema200 = quotes.GetEma(200).Select(x => ToDecimal(x.Ema)).ToArray();
        var rsi = quotes.GetRsi(RsiPeriod).Select(x => ToDecimal(x.Rsi)).ToArray();
        var atr = quotes.GetAtr(AtrPeriod).Select(x => ToDecimal(x.Atr)).ToArray();
        var macd = quotes.GetMacd(12, 26, 9).ToArray();
        var bollinger = quotes.GetBollingerBands(BollingerPeriod, 2).ToArray();

        return new StandardIndicatorSeries(
            sma10,
            sma20,
            sma50,
            sma150,
            sma200,
            ema5,
            ema10,
            ema20,
            ema50,
            ema200,
            rsi,
            atr,
            macd.Select(x => ToDecimal(x.Macd)).ToArray(),
            macd.Select(x => ToDecimal(x.Signal)).ToArray(),
            macd.Select(x => ToDecimal(x.Histogram)).ToArray(),
            bollinger.Select(x => ToDecimal(x.Sma)).ToArray(),
            bollinger.Select(x => ToDecimal(x.UpperBand)).ToArray(),
            bollinger.Select(x => ToDecimal(x.LowerBand)).ToArray());
    }

    private static VolumeBaselineSeries ComputeCumulativeVolumeBaseline(IReadOnlyList<OhlcvBar> bars)
    {
        var relativeVolume = new decimal?[bars.Count];
        var averageVolume = new decimal?[bars.Count];
        var sampleCount = new int[bars.Count];
        var cumulativeVolumesBySlot = new Dictionary<TimeSpan, List<decimal>>();
        DateOnly? activeDate = null;
        decimal activeSessionVolume = 0m;

        for (var i = 0; i < bars.Count; i++)
        {
            var exchangeTime = TimeZoneInfo.ConvertTime(bars[i].Timestamp, ExchangeTimeZone);
            var exchangeDate = DateOnly.FromDateTime(exchangeTime.DateTime);
            if (activeDate is not null && activeDate != exchangeDate)
            {
                activeSessionVolume = 0m;
            }

            activeDate = exchangeDate;
            activeSessionVolume += bars[i].Volume;

            var slot = exchangeTime.TimeOfDay;
            if (!cumulativeVolumesBySlot.TryGetValue(slot, out var comparableCumulativeVolumes))
            {
                comparableCumulativeVolumes = new List<decimal>();
                cumulativeVolumesBySlot[slot] = comparableCumulativeVolumes;
            }

            if (comparableCumulativeVolumes.Count >= RelativeVolumeMinimumComparableBars)
            {
                var lookback = comparableCumulativeVolumes
                    .Skip(Math.Max(0, comparableCumulativeVolumes.Count - RelativeVolumeLookbackSessions))
                    .ToArray();
                var average = lookback.Average();
                averageVolume[i] = average;
                sampleCount[i] = lookback.Length;
                relativeVolume[i] = average <= 0 ? null : activeSessionVolume / average;
            }

            comparableCumulativeVolumes.Add(activeSessionVolume);
        }

        return new VolumeBaselineSeries(relativeVolume, averageVolume, sampleCount);
    }

    private static VolumeBaselineSeries ComputeSlotVolumeBaseline(IReadOnlyList<OhlcvBar> bars)
    {
        var relativeVolume = new decimal?[bars.Count];
        var averageVolume = new decimal?[bars.Count];
        var sampleCount = new int[bars.Count];
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
                averageVolume[i] = average;
                sampleCount[i] = lookback.Length;
                relativeVolume[i] = average <= 0 ? null : bars[i].Volume / average;
            }

            comparableVolumes.Add(bars[i].Volume);
        }

        return new VolumeBaselineSeries(relativeVolume, averageVolume, sampleCount);
    }

    private static VolumeBaselineSeries ComputeSessionVolumeBaseline(IReadOnlyList<OhlcvBar> bars)
    {
        var relativeVolume = new decimal?[bars.Count];
        var averageVolume = new decimal?[bars.Count];
        var sampleCount = new int[bars.Count];
        var completedSessionVolumes = new List<decimal>();
        DateOnly? activeDate = null;
        decimal activeSessionVolume = 0m;

        for (var i = 0; i < bars.Count; i++)
        {
            var exchangeDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bars[i].Timestamp, ExchangeTimeZone).DateTime);
            if (activeDate is not null && activeDate != exchangeDate)
            {
                completedSessionVolumes.Add(activeSessionVolume);
                activeSessionVolume = 0m;
            }

            activeDate = exchangeDate;
            activeSessionVolume += bars[i].Volume;

            if (completedSessionVolumes.Count >= RelativeVolumeMinimumComparableBars)
            {
                var lookback = completedSessionVolumes
                    .Skip(Math.Max(0, completedSessionVolumes.Count - RelativeVolumeLookbackSessions))
                    .ToArray();
                var averageSessionVolume = lookback.Average();
                averageVolume[i] = averageSessionVolume;
                sampleCount[i] = lookback.Length;
                relativeVolume[i] = averageSessionVolume <= 0 ? null : activeSessionVolume / averageSessionVolume;
            }
        }

        return new VolumeBaselineSeries(relativeVolume, averageVolume, sampleCount);
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

    private static decimal? ToDecimal(double? value)
    {
        return value is null ? null : Convert.ToDecimal(value.Value);
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

    private sealed record VolumeBaselineSeries(
        decimal?[] RelativeVolume,
        decimal?[] AverageVolume,
        int[] SampleCount);

    private sealed record StandardIndicatorSeries(
        decimal?[] Sma10,
        decimal?[] Sma20,
        decimal?[] Sma50,
        decimal?[] Sma150,
        decimal?[] Sma200,
        decimal?[] Ema5,
        decimal?[] Ema10,
        decimal?[] Ema20,
        decimal?[] Ema50,
        decimal?[] Ema200,
        decimal?[] Rsi,
        decimal?[] Atr,
        decimal?[] MacdLine,
        decimal?[] MacdSignal,
        decimal?[] MacdHistogram,
        decimal?[] BollingerMiddle,
        decimal?[] BollingerUpper,
        decimal?[] BollingerLower);
}

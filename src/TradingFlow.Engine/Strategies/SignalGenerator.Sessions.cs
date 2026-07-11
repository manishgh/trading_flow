using System;
using System.Collections.Generic;
using System.Linq;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Sessions;

namespace TradingFlow.Engine.Strategies;

// Session / opening-range / breakout / confluence detectors for SignalGenerator (partial split).
public sealed partial class SignalGenerator
{
    private static decimal? GetSessionOpen(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var sessionOpen = currentExchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        return bars
            .Take(index + 1)
            .FirstOrDefault(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date == currentExchangeTime.Date &&
                    exchangeTime >= sessionOpen;
            })
            ?.Open;
    }

    private static decimal? GetPreviousRegularClose(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var regularOpen = new TimeSpan(9, 30, 0);
        var regularClose = new TimeSpan(16, 0, 0);

        return bars
            .Take(index)
            .Where(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date < currentExchangeTime.Date &&
                    exchangeTime.TimeOfDay >= regularOpen &&
                    exchangeTime.TimeOfDay <= regularClose;
            })
            .OrderBy(bar => bar.Timestamp)
            .LastOrDefault()
            ?.Close;
    }

    private static (bool IsInsideDay, bool IsNr7) GetPriorDayStructure(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var sessions = GetCompletedRegularSessions(strategy, bars.Take(index).ToArray(), currentExchangeTime.Date);
        if (sessions.Count < 2)
        {
            return (false, false);
        }

        var prior = sessions[^1];
        var previous = sessions[^2];
        var isInsideDay = prior.High <= previous.High && prior.Low >= previous.Low;

        var nrLookback = Math.Max(2, strategy.EntryRules.PriorNr7LookbackDays);
        var nrWindow = sessions.Skip(Math.Max(0, sessions.Count - nrLookback)).ToArray();
        var priorRange = prior.High - prior.Low;
        var isNr7 = nrWindow.Length >= nrLookback && priorRange == nrWindow.Min(session => session.High - session.Low);

        return (isInsideDay, isNr7);
    }

    private static IReadOnlyList<RegularSessionSummary> GetCompletedRegularSessions(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> historicalBars,
        DateTime currentExchangeDate)
    {
        var regularOpen = new TimeSpan(9, 30, 0);
        var regularClose = new TimeSpan(16, 0, 0);

        return historicalBars
            .Select(bar => new
            {
                Bar = bar,
                ExchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone)
            })
            .Where(x => x.ExchangeTime.Date < currentExchangeDate &&
                x.ExchangeTime.TimeOfDay >= regularOpen &&
                x.ExchangeTime.TimeOfDay <= regularClose)
            .GroupBy(x => x.ExchangeTime.Date)
            .OrderBy(group => group.Key)
            .Select(group => new RegularSessionSummary(
                group.Key,
                group.Max(x => x.Bar.High),
                group.Min(x => x.Bar.Low),
                group.Last().Bar.Close))
            .ToArray();
    }

    private static (bool IsBullishFade, bool IsBearishFade) GetMacdDivergenceFadeContext(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var lookback = Math.Max(3, strategy.EntryRules.DivergenceLookbackBars);
        if (index <= lookback || snapshots[index].MacdHistogram is null)
        {
            return (false, false);
        }

        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        if (currentExchangeTime.Hour < strategy.EntryRules.DivergenceStartHour)
        {
            return (false, false);
        }

        var start = Math.Max(0, index - lookback);
        var priorIndexes = Enumerable.Range(start, index - start)
            .Where(i => ConvertToExchangeTime(snapshots[i].Timestamp, strategy.Session.ExchangeTimezone).Date == currentExchangeTime.Date)
            .Where(i => snapshots[i].MacdHistogram is not null)
            .ToArray();
        if (priorIndexes.Length == 0)
        {
            return (false, false);
        }

        var currentBar = bars[index];
        var currentHistogram = snapshots[index].MacdHistogram!.Value;
        var priorLowIndex = priorIndexes.MinBy(i => bars[i].Low);
        var priorHighIndex = priorIndexes.MaxBy(i => bars[i].High);

        var bullishFade = currentBar.Low < bars[priorLowIndex].Low &&
            currentHistogram > snapshots[priorLowIndex].MacdHistogram!.Value &&
            currentBar.Close > bars[index - 1].Low &&
            currentBar.Close < bars[index - 1].High;

        var bearishFade = currentBar.High > bars[priorHighIndex].High &&
            currentHistogram < snapshots[priorHighIndex].MacdHistogram!.Value &&
            currentBar.Close < bars[index - 1].High &&
            currentBar.Close > bars[index - 1].Low;

        return (bullishFade, bearishFade);
    }

    private static decimal? ComputeCloseLocationValue(OhlcvBar bar)
    {
        var range = bar.High - bar.Low;
        return range <= 0m
            ? null
            : (bar.Close - bar.Low) / range;
    }

    private static decimal? ComputePriorEntryGainPct(
        IReadOnlyList<OhlcvBar> bars,
        int index,
        int lookbackBars)
    {
        var lookback = Math.Max(1, lookbackBars);
        var priorIndex = index - lookback;
        if (priorIndex < 0)
        {
            return null;
        }

        var priorClose = bars[priorIndex].Close;
        if (priorClose <= 0m)
        {
            return null;
        }

        return ((bars[index].Close / priorClose) - 1m) * 100m;
    }

    private static (decimal Slope, decimal R2)? ComputeLogTrend(
        IReadOnlyList<OhlcvBar> bars,
        int index,
        int lookback,
        Func<OhlcvBar, decimal> valueSelector,
        bool addOne)
    {
        if (lookback < 2 || index < lookback - 1)
        {
            return null;
        }

        var startIndex = index - lookback + 1;
        var n = lookback;
        var sumX = 0d;
        var sumY = 0d;
        var sumX2 = 0d;
        var sumXY = 0d;
        var yValues = new double[n];

        for (var offset = 0; offset < n; offset++)
        {
            var rawValue = (double)valueSelector(bars[startIndex + offset]);
            if (addOne)
            {
                rawValue += 1d;
            }

            if (rawValue <= 0d)
            {
                return null;
            }

            var x = (double)offset;
            var y = Math.Log(rawValue);
            yValues[offset] = y;
            sumX += x;
            sumY += y;
            sumX2 += x * x;
            sumXY += x * y;
        }

        var denominator = n * sumX2 - sumX * sumX;
        if (Math.Abs(denominator) < Double.Epsilon)
        {
            return null;
        }

        var slope = (n * sumXY - sumX * sumY) / denominator;
        var intercept = (sumY - slope * sumX) / n;
        var meanY = sumY / n;
        var sse = 0d;
        var sst = 0d;

        for (var offset = 0; offset < n; offset++)
        {
            var predicted = intercept + slope * offset;
            var residual = yValues[offset] - predicted;
            sse += residual * residual;

            var variance = yValues[offset] - meanY;
            sst += variance * variance;
        }

        var r2 = sst <= Double.Epsilon
            ? 1d
            : Math.Clamp(1d - sse / sst, 0d, 1d);

        return ((decimal)slope, (decimal)r2);
    }

    private static bool IsOpeningRangeBreakout(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        if (strategy.EntryRules.OpeningRangeMinutes <= 0)
        {
            return false;
        }

        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var sessionOpen = currentExchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        var openingRangeEnd = sessionOpen.AddMinutes(strategy.EntryRules.OpeningRangeMinutes);
        if (currentExchangeTime < openingRangeEnd)
        {
            return false;
        }

        var openingRangeHigh = bars
            .Take(index)
            .Where(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date == currentExchangeTime.Date &&
                    exchangeTime >= sessionOpen &&
                    exchangeTime < openingRangeEnd;
            })
            .Select(bar => (decimal?)bar.High)
            .Max();

        var breakLevel = openingRangeHigh + strategy.EntryRules.OpeningRangeBreakBuffer;
        return breakLevel is not null &&
            snapshots[index].CurrentPrice >= breakLevel.Value &&
            snapshots[index - 1].CurrentPrice < breakLevel.Value;
    }

    private static bool IsOpeningDriveContinuation(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        if (strategy.EntryRules.OpeningRangeMinutes <= 0)
        {
            return false;
        }

        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var sessionOpen = currentExchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        var openingWindowEnd = sessionOpen.AddMinutes(strategy.EntryRules.OpeningRangeMinutes);
        if (currentExchangeTime < openingWindowEnd)
        {
            return false;
        }

        var sessionOpenBar = bars
            .Take(index + 1)
            .FirstOrDefault(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date == currentExchangeTime.Date &&
                    exchangeTime >= sessionOpen;
            });
        if (sessionOpenBar is null || snapshots[index].CurrentPrice <= sessionOpenBar.Open)
        {
            return false;
        }

        var holdBars = Math.Max(1, strategy.EntryRules.VwapHoldBars);
        if (index < holdBars - 1)
        {
            return false;
        }

        for (var i = index - holdBars + 1; i <= index; i++)
        {
            var exchangeTime = ConvertToExchangeTime(snapshots[i].Timestamp, strategy.Session.ExchangeTimezone);
            if (exchangeTime.Date != currentExchangeTime.Date ||
                snapshots[i].Vwap is null ||
                snapshots[i].CurrentPrice < snapshots[i].Vwap!.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOpeningRangeBreakdown(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        if (strategy.EntryRules.OpeningRangeMinutes <= 0)
        {
            return false;
        }

        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var sessionOpen = currentExchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        var openingRangeEnd = sessionOpen.AddMinutes(strategy.EntryRules.OpeningRangeMinutes);
        if (currentExchangeTime < openingRangeEnd)
        {
            return false;
        }

        var openingRangeLow = bars
            .Take(index)
            .Where(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date == currentExchangeTime.Date &&
                    exchangeTime >= sessionOpen &&
                    exchangeTime < openingRangeEnd;
            })
            .Select(bar => (decimal?)bar.Low)
            .Min();

        var breakLevel = openingRangeLow - strategy.EntryRules.OpeningRangeBreakBuffer;
        return breakLevel is not null &&
            snapshots[index].CurrentPrice <= breakLevel.Value &&
            snapshots[index - 1].CurrentPrice > breakLevel.Value;
    }

    private static bool IsAboveSessionOpen(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var sessionOpen = currentExchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        var sessionOpenBar = bars
            .Take(index + 1)
            .FirstOrDefault(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date == currentExchangeTime.Date &&
                    exchangeTime >= sessionOpen;
            });

        return sessionOpenBar is not null && snapshots[index].CurrentPrice > sessionOpenBar.Open;
    }

    private static bool IsBelowSessionOpen(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var currentExchangeTime = ConvertToExchangeTime(snapshots[index].Timestamp, strategy.Session.ExchangeTimezone);
        var sessionOpen = currentExchangeTime.Date.Add(new TimeSpan(9, 30, 0));
        var sessionOpenBar = bars
            .Take(index + 1)
            .FirstOrDefault(bar =>
            {
                var exchangeTime = ConvertToExchangeTime(bar.Timestamp, strategy.Session.ExchangeTimezone);
                return exchangeTime.Date == currentExchangeTime.Date &&
                    exchangeTime >= sessionOpen;
            });

        return sessionOpenBar is not null && snapshots[index].CurrentPrice < sessionOpenBar.Open;
    }

    private static bool IsRecentHighBreakout(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        int index)
    {
        var lookback = strategy.EntryRules.RecentHighLookbackBars;
        if (lookback <= 0 || index <= lookback)
        {
            return false;
        }

        var priorHigh = bars
            .Skip(index - lookback)
            .Take(lookback)
            .Max(x => x.High);
        return bars[index].Close > priorHigh;
    }

    private static bool IsRecentLowBreakdown(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        int index)
    {
        var lookback = strategy.EntryRules.RecentHighLookbackBars;
        if (lookback <= 0 || index <= lookback)
        {
            return false;
        }

        var priorLow = bars
            .Skip(index - lookback)
            .Take(lookback)
            .Min(x => x.Low);
        return bars[index].Close < priorLow;
    }

    private static bool IsVolatilityContraction(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var lookback = strategy.EntryRules.VolatilityContractionLookbackBars;
        if (lookback <= 0 ||
            index <= lookback ||
            snapshots[index].Atr is null ||
            snapshots[index - lookback].Atr is null)
        {
            return false;
        }

        var currentRange = bars[index].High - bars[index].Low;
        var priorWindowAverageRange = bars
            .Skip(index - lookback)
            .Take(lookback)
            .Average(x => x.High - x.Low);
        return snapshots[index].Atr!.Value < snapshots[index - lookback].Atr!.Value &&
            currentRange <= priorWindowAverageRange;
    }

    private static DateTime ConvertToExchangeTime(DateTimeOffset timestamp, string timezoneId)
    {
        return TimeZoneInfo.ConvertTime(timestamp, ResolveTimezone(timezoneId)).DateTime;
    }

    private sealed record RegularSessionSummary(DateTime Date, decimal High, decimal Low, decimal Close);

    private static TimeZoneInfo ResolveTimezone(string timezoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException) when (timezoneId.Equals("America/New_York", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    public bool PassesConfluenceGate(
        StrategyDefinition strategy,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe)
    {
        return GetConfluenceRejection(strategy, timestamp, snapshotsByTimeframe) is null;
    }

    public string? GetConfluenceRejection(
        StrategyDefinition strategy,
        DateTimeOffset timestamp,
        IReadOnlyDictionary<string, IReadOnlyList<IndicatorSnapshot>> snapshotsByTimeframe)
    {
        if (!strategy.Confluence.Enabled)
        {
            return null;
        }

        if (!snapshotsByTimeframe.TryGetValue(strategy.Confluence.Timeframe, out var confluenceSnapshots))
        {
            return $"confluence_missing_timeframe (Required: {strategy.Confluence.Timeframe})";
        }

        var confluence = confluenceSnapshots.LastOrDefault(x => x.Timestamp.Add(TimeframeParser.Parse(x.Timeframe)) <= timestamp);
        if (confluence is null)
        {
            return $"confluence_no_closed_bar (Required: {strategy.Confluence.Timeframe}, SignalTime: {timestamp:O})";
        }

        if (confluence.MacdHistogram is null)
        {
            return $"confluence_missing_macd (Timeframe: {confluence.Timeframe}, Bar: {confluence.Timestamp:O})";
        }

        var ema = strategy.Confluence.EmaPeriod switch
        {
            20 => confluence.Ema20,
            50 => confluence.Ema50,
            200 => confluence.Ema200,
            _ => throw new NotSupportedException($"Unsupported confluence EMA period: {strategy.Confluence.EmaPeriod}")
        };

        if (ema is null)
        {
            return $"confluence_missing_ema{strategy.Confluence.EmaPeriod} (Timeframe: {confluence.Timeframe}, Bar: {confluence.Timestamp:O})";
        }

        if (strategy.Confluence.MacdFilter.Equals("bearish", StringComparison.OrdinalIgnoreCase))
        {
            if (confluence.CurrentPrice >= ema.Value)
            {
                return $"confluence_price_above_ema{strategy.Confluence.EmaPeriod} (Price: {confluence.CurrentPrice:F2}, EMA: {ema.Value:F2}, Timeframe: {confluence.Timeframe})";
            }

            if (confluence.MacdHistogram.Value >= 0)
            {
                return $"confluence_macd_not_bearish (Histogram: {confluence.MacdHistogram.Value:F4}, Timeframe: {confluence.Timeframe})";
            }

            return null;
        }

        if (confluence.CurrentPrice <= ema.Value)
        {
            return $"confluence_price_below_ema{strategy.Confluence.EmaPeriod} (Price: {confluence.CurrentPrice:F2}, EMA: {ema.Value:F2}, Timeframe: {confluence.Timeframe})";
        }

        if (strategy.Confluence.MacdFilter.Equals("not_bearish", StringComparison.OrdinalIgnoreCase) &&
            confluence.MacdHistogram.Value < 0)
        {
            return $"confluence_macd_bearish (Histogram: {confluence.MacdHistogram.Value:F4}, Timeframe: {confluence.Timeframe})";
        }

        return null;
    }
}

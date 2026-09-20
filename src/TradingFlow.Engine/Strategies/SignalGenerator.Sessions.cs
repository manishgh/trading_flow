using System;
using System.Collections.Generic;
using System.Linq;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Sessions;

namespace TradingFlow.Engine.Strategies;

// Swing breakout and confluence detectors for SignalGenerator.
public sealed partial class SignalGenerator
{
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

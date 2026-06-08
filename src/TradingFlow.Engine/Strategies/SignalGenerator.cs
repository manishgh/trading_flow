using System;
using System.Collections.Generic;
using System.Linq;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Strategies;

public sealed class SignalGenerator
{
    public TradeSignal? CreateTradeSignal(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var snapshot = snapshots[index];
        var previous = snapshots[index - 1];
        var bar = bars[index];
        if (snapshot.Rsi is null ||
            snapshot.Atr is null ||
            snapshot.RelativeVolume is null ||
            snapshot.Vwap is null ||
            snapshot.BollingerMiddle is null ||
            snapshot.MacdHistogram is null)
        {
            return null;
        }

        var previousHistogram = previous.MacdHistogram ?? snapshot.MacdHistogram.Value;
        var isAboveVwap = snapshot.CurrentPrice > snapshot.Vwap.Value;
        var isVwapPullback = previous.Vwap is not null &&
            previous.CurrentPrice >= previous.Vwap.Value &&
            bar.Low <= snapshot.Vwap.Value &&
            snapshot.CurrentPrice >= snapshot.Vwap.Value;
        var isVwapReclaim = previous.Vwap is not null &&
            previous.CurrentPrice < previous.Vwap.Value &&
            snapshot.CurrentPrice >= snapshot.Vwap.Value;
        var isEma20Pullback = snapshot.Ema20 is not null &&
            previous.Ema20 is not null &&
            previous.CurrentPrice >= previous.Ema20.Value &&
            bar.Low <= snapshot.Ema20.Value &&
            snapshot.CurrentPrice >= snapshot.Ema20.Value;
        var isPriceAboveEma20 = snapshot.Ema20 is not null && snapshot.CurrentPrice > snapshot.Ema20.Value;
        var isPriceAboveEma50 = snapshot.Ema50 is not null && snapshot.CurrentPrice > snapshot.Ema50.Value;
        var isEma20AboveEma50 = snapshot.Ema20 is not null &&
            snapshot.Ema50 is not null &&
            snapshot.Ema20.Value > snapshot.Ema50.Value;
        decimal? vwapExtensionAtr = snapshot.Atr.Value <= 0
            ? null
            : Math.Max(0, snapshot.CurrentPrice - snapshot.Vwap.Value) / snapshot.Atr.Value;

        return new TradeSignal(
            snapshot.Ticker,
            snapshot.Timestamp,
            snapshot.Timeframe,
            snapshot.CurrentPrice,
            snapshot.CurrentVolume,
            snapshot.Rsi.Value,
            snapshot.Atr.Value,
            isAboveVwap,
            isVwapPullback,
            isVwapReclaim,
            isEma20Pullback,
            IsOpeningRangeBreakout(strategy, bars, snapshots, index),
            IsRecentHighBreakout(strategy, bars, index),
            IsVolatilityContraction(strategy, bars, snapshots, index),
            isPriceAboveEma20,
            isPriceAboveEma50,
            isEma20AboveEma50,
            vwapExtensionAtr,
            snapshot.CurrentPrice > snapshot.BollingerMiddle.Value,
            snapshot.MacdHistogram.Value > 0,
            snapshot.MacdHistogram.Value >= 0 || snapshot.MacdHistogram.Value >= previousHistogram);
    }

    private static bool IsOpeningRangeBreakout(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
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

        return openingRangeHigh is not null &&
            snapshots[index].CurrentPrice > openingRangeHigh.Value &&
            snapshots[index - 1].CurrentPrice <= openingRangeHigh.Value;
    }

    private static bool IsRecentHighBreakout(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        int index)
    {
        var lookback = strategy.EntryRules.RecentHighLookbackBars;
        if (index <= lookback)
        {
            return false;
        }

        var priorHigh = bars
            .Skip(index - lookback)
            .Take(lookback)
            .Max(x => x.High);
        return bars[index].Close > priorHigh;
    }

    private static bool IsVolatilityContraction(
        StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int index)
    {
        var lookback = strategy.EntryRules.VolatilityContractionLookbackBars;
        if (index <= lookback ||
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
        if (!strategy.Confluence.Enabled)
        {
            return true;
        }

        if (!snapshotsByTimeframe.TryGetValue(strategy.Confluence.Timeframe, out var confluenceSnapshots))
        {
            return false;
        }

        var confluence = confluenceSnapshots.LastOrDefault(x => x.Timestamp.Add(ParseTimeframe(x.Timeframe)) <= timestamp);
        if (confluence is null || confluence.MacdHistogram is null)
        {
            return false;
        }

        var ema = strategy.Confluence.EmaPeriod switch
        {
            20 => confluence.Ema20,
            50 => confluence.Ema50,
            200 => confluence.Ema200,
            _ => throw new NotSupportedException($"Unsupported confluence EMA period: {strategy.Confluence.EmaPeriod}")
        };

        if (ema is null || confluence.CurrentPrice <= ema.Value)
        {
            return false;
        }

        return !strategy.Confluence.MacdFilter.Equals("not_bearish", StringComparison.OrdinalIgnoreCase) ||
            confluence.MacdHistogram.Value >= 0;
    }

    private static TimeSpan ParseTimeframe(string timeframe)
    {
        if (timeframe.EndsWith("m", StringComparison.OrdinalIgnoreCase) &&
            Int32.TryParse(timeframe[..^1], out var minutes))
        {
            return TimeSpan.FromMinutes(minutes);
        }

        if (timeframe.EndsWith("h", StringComparison.OrdinalIgnoreCase) &&
            Int32.TryParse(timeframe[..^1], out var hours))
        {
            return TimeSpan.FromHours(hours);
        }

        if (timeframe.EndsWith("d", StringComparison.OrdinalIgnoreCase) &&
            Int32.TryParse(timeframe[..^1], out var days))
        {
            return TimeSpan.FromDays(days);
        }

        throw new NotSupportedException($"Unsupported timeframe: {timeframe}");
    }
}

using TradingFlow.Domain.Market;
using Skender.Stock.Indicators;
using TradingFlow.Engine.Market;

namespace TradingFlow.Engine.Indicators;

public interface IIndicatorCalculator
{
    IReadOnlyList<IndicatorSnapshot> Compute(IReadOnlyList<OhlcvBar> inputBars);
}

public sealed class IndicatorEngine : IIndicatorCalculator
{
    private const int RsiPeriod = 14;
    private const int Rsi2Period = 2;
    private const int AtrPeriod = 14;
    private const int BollingerPeriod = 20;
    public const int RelativeVolumeLookbackSessions = MarketEvidenceProfile.DefaultLookbackSessions;
    private static readonly TimeOnly PremarketOpen = new(4, 0);
    private static readonly TimeOnly PostmarketClose = new(20, 0);
    private readonly MarketEvidenceProfile marketEvidenceProfile;
    private readonly TimeZoneInfo exchangeTimeZone;

    public IndicatorEngine(MarketEvidenceProfile? marketEvidenceProfile = null)
    {
        this.marketEvidenceProfile = marketEvidenceProfile ?? MarketEvidenceProfile.ProductionDefault;
        exchangeTimeZone = ResolveExchangeTimeZone(this.marketEvidenceProfile.ExchangeTimeZone);
    }

    public IReadOnlyList<IndicatorSnapshot> Compute(IReadOnlyList<OhlcvBar> inputBars)
    {
        var bars = inputBars.OrderBy(x => x.Timestamp).ToArray();
        if (bars.Length == 0)
        {
            return Array.Empty<IndicatorSnapshot>();
        }

        var standardIndicators = ComputeStandardIndicators(bars);
        var dataFeeds = bars
            .Select(bar => NormalizeDataFeed(bar.DataFeed))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var mixedFeed = dataFeeds.Length != 1;
        var dataFeed = mixedFeed ? "mixed" : dataFeeds[0];
        var adjustmentPolicies = bars
            .Select(bar => NormalizeAdjustmentPolicy(bar.AdjustmentPolicy))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var mixedAdjustmentPolicy = adjustmentPolicies.Length != 1;
        var adjustmentPolicy = mixedAdjustmentPolicy ? "mixed" : adjustmentPolicies[0];
        var marketEvidenceReliability = ResolveMarketEvidenceReliability(
            mixedFeed,
            dataFeed,
            mixedAdjustmentPolicy,
            adjustmentPolicy);
        var volumeEvidence = marketEvidenceReliability == "verified_same_feed"
            ? ComputeVolumeEvidence(bars)
            : VolumeEvidenceSeries.Unavailable(bars.Length);
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
                volumeEvidence.CumulativeRelativeVolume[i],
                standardIndicators.MacdLine[i],
                standardIndicators.MacdSignal[i],
                standardIndicators.MacdHistogram[i],
                Sma10: standardIndicators.Sma10[i],
                Sma20: standardIndicators.Sma20[i],
                Sma50: standardIndicators.Sma50[i],
                SlotRelativeVolume: volumeEvidence.SlotRelativeVolume[i],
                Ema10: standardIndicators.Ema10[i],
                SlotMedianVolume: volumeEvidence.SlotMedianVolume[i],
                CumulativeSameTimeMedianVolume: volumeEvidence.CumulativeMedianVolume[i],
                RelativeVolumeSampleCount: volumeEvidence.CumulativeSampleCount[i],
                Sma150: standardIndicators.Sma150[i],
                Sma200: standardIndicators.Sma200[i],
                Ema5: standardIndicators.Ema5[i],
                Adx: standardIndicators.Adx[i],
                Obv: standardIndicators.Obv[i],
                Rsi2: standardIndicators.Rsi2[i],
                SlotRelativeVolumeSampleCount: volumeEvidence.SlotSampleCount[i],
                MarketEvidenceProfileVersion: marketEvidenceProfile.Version,
                RelativeVolumeCohort: volumeEvidence.Cohort[i],
                RelativeVolumeMinimumSamples: marketEvidenceProfile.MinimumValidSamples,
                DataFeed: dataFeed,
                AdjustmentPolicy: adjustmentPolicy,
                MarketEvidenceReliability: marketEvidenceReliability));
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
        var rsi2 = quotes.GetRsi(Rsi2Period).Select(x => ToDecimal(x.Rsi)).ToArray();
        var atr = quotes.GetAtr(AtrPeriod).Select(x => ToDecimal(x.Atr)).ToArray();
        var adx = quotes.GetAdx(AtrPeriod).Select(x => ToDecimal(x.Adx)).ToArray();
        var obv = quotes.GetObv().Select(x => ToDecimal(x.Obv)).ToArray();
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
            rsi2,
            atr,
            adx,
            obv,
            macd.Select(x => ToDecimal(x.Macd)).ToArray(),
            macd.Select(x => ToDecimal(x.Signal)).ToArray(),
            macd.Select(x => ToDecimal(x.Histogram)).ToArray(),
            bollinger.Select(x => ToDecimal(x.Sma)).ToArray(),
            bollinger.Select(x => ToDecimal(x.UpperBand)).ToArray(),
            bollinger.Select(x => ToDecimal(x.LowerBand)).ToArray());
    }

    private VolumeEvidenceSeries ComputeVolumeEvidence(IReadOnlyList<OhlcvBar> bars)
    {
        var cumulativeRelativeVolume = new decimal?[bars.Count];
        var slotRelativeVolume = new decimal?[bars.Count];
        var cumulativeMedianVolume = new decimal?[bars.Count];
        var slotMedianVolume = new decimal?[bars.Count];
        var cumulativeSampleCount = new int[bars.Count];
        var slotSampleCount = new int[bars.Count];
        var cohorts = new string?[bars.Count];
        var sessionVolumeHistory = new Dictionary<VolumeSessionKey, SessionVolumeState>();
        var slotHistory = new Dictionary<VolumeSlotKey, List<SessionVolumeSample>>();

        for (var i = 0; i < bars.Count; i++)
        {
            if (!TryResolveVolumeSlot(bars[i], out var resolvedSlot))
            {
                continue;
            }

            if (!sessionVolumeHistory.TryGetValue(resolvedSlot.SessionKey, out var currentSession))
            {
                currentSession = new SessionVolumeState();
                sessionVolumeHistory[resolvedSlot.SessionKey] = currentSession;
            }

            currentSession.Slots[resolvedSlot.SequenceMinute] = bars[i].Volume;
            if (bars[i].CoverageVerifiedThroughUtc is { } verifiedThrough &&
                (currentSession.CoverageVerifiedThroughUtc is null ||
                 verifiedThrough.ToUniversalTime() > currentSession.CoverageVerifiedThroughUtc.Value))
            {
                currentSession.CoverageVerifiedThroughUtc = verifiedThrough.ToUniversalTime();
            }

            var cumulative = currentSession.Slots
                .Where(pair => pair.Key <= resolvedSlot.SequenceMinute)
                .Sum(pair => pair.Value);
            cohorts[i] = resolvedSlot.Cohort.ToString().ToLowerInvariant();
            var priorTradingSessions = ResolvePriorTradingSessions(resolvedSlot.SessionKey.TradeDate);

            EvaluateCumulativeBaseline(
                sessionVolumeHistory,
                priorTradingSessions,
                resolvedSlot,
                cumulative,
                out cumulativeRelativeVolume[i],
                out cumulativeMedianVolume[i],
                out cumulativeSampleCount[i]);
            EvaluateBaseline(
                slotHistory,
                priorTradingSessions,
                resolvedSlot.SlotKey,
                resolvedSlot.SessionKey.TradeDate,
                bars[i].Volume,
                out slotRelativeVolume[i],
                out slotMedianVolume[i],
                out slotSampleCount[i]);
        }

        return new VolumeEvidenceSeries(
            cumulativeRelativeVolume,
            slotRelativeVolume,
            cumulativeMedianVolume,
            slotMedianVolume,
            cumulativeSampleCount,
            slotSampleCount,
            cohorts);
    }

    private void EvaluateCumulativeBaseline(
        IReadOnlyDictionary<VolumeSessionKey, SessionVolumeState> sessionHistory,
        IReadOnlyList<DateOnly>? priorTradingSessions,
        ResolvedVolumeSlot currentSlot,
        decimal currentVolume,
        out decimal? relativeVolume,
        out decimal? medianVolume,
        out int sampleCount)
    {
        relativeVolume = null;
        medianVolume = null;
        sampleCount = 0;
        if (priorTradingSessions is null)
        {
            return;
        }

        var lookback = priorTradingSessions
            .Select(tradeDate =>
            {
                if (!TryResolveComparableSlot(
                        tradeDate,
                        currentSlot,
                        out var comparableSequenceMinute,
                        out var requiredCoverageUtc) ||
                    !sessionHistory.TryGetValue(
                        new VolumeSessionKey(tradeDate, currentSlot.Cohort),
                        out var session) ||
                    session.CoverageVerifiedThroughUtc is null ||
                    session.CoverageVerifiedThroughUtc.Value < requiredCoverageUtc)
                {
                    return (decimal?)null;
                }

                var comparableSlots = session.Slots
                    .Where(slot => slot.Key <= comparableSequenceMinute)
                    .Select(slot => slot.Value)
                    .ToArray();
                return comparableSlots.Length == 0
                    ? (decimal?)null
                    : comparableSlots.Sum();
            })
            .Where(volume => volume.HasValue)
            .Select(volume => volume!.Value)
            .ToArray();
        sampleCount = lookback.Length;
        if (sampleCount < marketEvidenceProfile.MinimumValidSamples)
        {
            return;
        }

        var median = Median(lookback);
        medianVolume = median;
        relativeVolume = median <= 0m ? null : currentVolume / median;
    }

    private void EvaluateBaseline(
        IDictionary<VolumeSlotKey, List<SessionVolumeSample>> history,
        IReadOnlyList<DateOnly>? priorTradingSessions,
        VolumeSlotKey slotKey,
        DateOnly currentTradeDate,
        decimal currentVolume,
        out decimal? relativeVolume,
        out decimal? medianVolume,
        out int sampleCount)
    {
        if (!history.TryGetValue(slotKey, out var samples))
        {
            samples = [];
            history[slotKey] = samples;
        }

        var priorDates = priorTradingSessions?.ToHashSet() ?? [];
        var lookback = samples
            .Where(sample => priorDates.Contains(sample.TradeDate))
            .OrderBy(sample => sample.TradeDate)
            .Select(sample => sample.Volume)
            .ToArray();
        sampleCount = lookback.Length;
        relativeVolume = null;
        medianVolume = null;
        if (sampleCount >= marketEvidenceProfile.MinimumValidSamples)
        {
            var median = Median(lookback);
            medianVolume = median;
            relativeVolume = median <= 0m ? null : currentVolume / median;
        }

        var existingIndex = samples.FindIndex(sample => sample.TradeDate == currentTradeDate);
        var current = new SessionVolumeSample(currentTradeDate, currentVolume);
        if (existingIndex >= 0)
        {
            samples[existingIndex] = current;
        }
        else
        {
            samples.Add(current);
        }
    }

    private bool TryResolveVolumeSlot(
        OhlcvBar bar,
        out ResolvedVolumeSlot resolvedSlot)
    {
        var exchangeTimestamp = TimeZoneInfo.ConvertTime(bar.Timestamp, exchangeTimeZone);
        var exchangeDate = DateOnly.FromDateTime(exchangeTimestamp.DateTime);
        var timeframe = TimeframeParser.Parse(bar.Timeframe);
        if (IsDailyTimeframe(bar.Timeframe))
        {
            if (!marketEvidenceProfile.TryResolveSchedule(exchangeDate, out var dailySchedule) ||
                !dailySchedule.IsTradingDay)
            {
                resolvedSlot = default;
                return false;
            }

            resolvedSlot = new ResolvedVolumeSlot(
                new VolumeSessionKey(exchangeDate, MarketVolumeCohort.Daily),
                new VolumeSlotKey(MarketVolumeCohort.Daily, 0),
                MarketVolumeCohort.Daily,
                0,
                timeframe);
            return true;
        }

        var time = TimeOnly.FromDateTime(exchangeTimestamp.DateTime);
        var tradeDate = time >= PostmarketClose ? exchangeDate.AddDays(1) : exchangeDate;
        if (!TryClassifyClock(tradeDate, time, out var cohort, out var sequenceMinute))
        {
            resolvedSlot = default;
            return false;
        }

        resolvedSlot = new ResolvedVolumeSlot(
            new VolumeSessionKey(tradeDate, cohort),
            new VolumeSlotKey(cohort, MinutesBetween(TimeOnly.MinValue, time)),
            cohort,
            sequenceMinute,
            timeframe);
        return true;
    }

    private IReadOnlyList<DateOnly>? ResolvePriorTradingSessions(DateOnly currentTradeDate)
    {
        var result = new List<DateOnly>(marketEvidenceProfile.LookbackSessions);
        var cursor = currentTradeDate.AddDays(-1);
        var safetyLimit = marketEvidenceProfile.LookbackSessions * 4 + 31;
        for (var inspected = 0; inspected < safetyLimit && result.Count < marketEvidenceProfile.LookbackSessions; inspected++)
        {
            if (!marketEvidenceProfile.TryResolveSchedule(cursor, out var schedule))
            {
                return null;
            }

            if (schedule.IsTradingDay)
            {
                result.Add(cursor);
            }

            cursor = cursor.AddDays(-1);
        }

        return result.Count == marketEvidenceProfile.LookbackSessions ? result : null;
    }

    private bool TryResolveComparableSlot(
        DateOnly tradeDate,
        ResolvedVolumeSlot currentSlot,
        out int sequenceMinute,
        out DateTimeOffset requiredCoverageUtc)
    {
        if (currentSlot.Cohort == MarketVolumeCohort.Daily)
        {
            if (!marketEvidenceProfile.TryResolveSchedule(tradeDate, out var dailySchedule) ||
                !dailySchedule.IsTradingDay)
            {
                sequenceMinute = default;
                requiredCoverageUtc = default;
                return false;
            }

            sequenceMinute = 0;
            requiredCoverageUtc = ToUtc(tradeDate, TimeOnly.MinValue).Add(currentSlot.Timeframe);
            return true;
        }

        var clock = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(currentSlot.SlotKey.ClockMinute));
        if (!TryClassifyClock(tradeDate, clock, out var cohort, out sequenceMinute) ||
            cohort != currentSlot.Cohort)
        {
            requiredCoverageUtc = default;
            return false;
        }

        var localDate = currentSlot.Cohort == MarketVolumeCohort.Overnight && clock >= PostmarketClose
            ? tradeDate.AddDays(-1)
            : tradeDate;
        requiredCoverageUtc = ToUtc(localDate, clock).Add(currentSlot.Timeframe);
        return true;
    }

    private bool TryClassifyClock(
        DateOnly tradeDate,
        TimeOnly time,
        out MarketVolumeCohort cohort,
        out int sequenceMinute)
    {
        if (!marketEvidenceProfile.TryResolveSchedule(tradeDate, out var schedule) || !schedule.IsTradingDay)
        {
            cohort = default;
            sequenceMinute = default;
            return false;
        }

        if (time >= PostmarketClose || time < PremarketOpen)
        {
            cohort = MarketVolumeCohort.Overnight;
            sequenceMinute = time >= PostmarketClose
                ? MinutesBetween(PostmarketClose, time)
                : 240 + MinutesBetween(TimeOnly.MinValue, time);
            return true;
        }

        if (time < schedule.RegularOpen)
        {
            cohort = MarketVolumeCohort.Premarket;
            sequenceMinute = MinutesBetween(PremarketOpen, time);
            return true;
        }

        if (time < schedule.RegularClose)
        {
            cohort = MarketVolumeCohort.Regular;
            sequenceMinute = MinutesBetween(schedule.RegularOpen, time);
            return true;
        }

        if (time < PostmarketClose)
        {
            cohort = MarketVolumeCohort.Postmarket;
            sequenceMinute = MinutesBetween(schedule.RegularClose, time);
            return true;
        }

        cohort = default;
        sequenceMinute = default;
        return false;
    }

    private static bool IsDailyTimeframe(string timeframe) =>
        timeframe.EndsWith("d", StringComparison.OrdinalIgnoreCase) ||
        timeframe.EndsWith("w", StringComparison.OrdinalIgnoreCase) ||
        timeframe.EndsWith("mo", StringComparison.OrdinalIgnoreCase);

    private static int MinutesBetween(TimeOnly start, TimeOnly end) =>
        (int)(end.ToTimeSpan() - start.ToTimeSpan()).TotalMinutes;

    private DateTimeOffset ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, exchangeTimeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static decimal Median(IReadOnlyCollection<decimal> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2m
            : ordered[middle];
    }

    private static string NormalizeDataFeed(string? dataFeed) =>
        String.IsNullOrWhiteSpace(dataFeed)
            ? "unspecified"
            : dataFeed.Trim().ToLowerInvariant();

    private static string NormalizeAdjustmentPolicy(string? adjustmentPolicy) =>
        String.IsNullOrWhiteSpace(adjustmentPolicy)
            ? "unspecified"
            : adjustmentPolicy.Trim().ToLowerInvariant();

    private string ResolveMarketEvidenceReliability(
        bool mixedFeed,
        string dataFeed,
        bool mixedAdjustmentPolicy,
        string adjustmentPolicy)
    {
        if (mixedFeed)
        {
            return "mixed_feed";
        }

        if (dataFeed == "unspecified")
        {
            return "feed_unspecified";
        }

        if (!dataFeed.Equals(
                marketEvidenceProfile.OperationalRules.RequiredDataFeed,
                StringComparison.OrdinalIgnoreCase))
        {
            return "unexpected_feed";
        }

        if (mixedAdjustmentPolicy)
        {
            return "mixed_adjustment_policy";
        }

        if (adjustmentPolicy == "unspecified")
        {
            return "adjustment_policy_unspecified";
        }

        return adjustmentPolicy.Equals(
            marketEvidenceProfile.OperationalRules.RequiredAdjustmentPolicy,
            StringComparison.OrdinalIgnoreCase)
            ? "verified_same_feed"
            : "unexpected_adjustment_policy";
    }

    private decimal?[] ComputeVwap(IReadOnlyList<OhlcvBar> bars)
    {
        var output = new decimal?[bars.Count];
        DateOnly? activeDate = null;
        decimal cumulativePriceVolume = 0;
        decimal cumulativeVolume = 0;

        for (var i = 0; i < bars.Count; i++)
        {
            var barDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bars[i].Timestamp, exchangeTimeZone).DateTime);
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

    private static TimeZoneInfo ResolveExchangeTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException) when (timeZoneId.Equals("America/New_York", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    private readonly record struct VolumeSessionKey(DateOnly TradeDate, MarketVolumeCohort Cohort);

    private readonly record struct VolumeSlotKey(MarketVolumeCohort Cohort, int ClockMinute);

    private readonly record struct SessionVolumeSample(DateOnly TradeDate, decimal Volume);

    private readonly record struct ResolvedVolumeSlot(
        VolumeSessionKey SessionKey,
        VolumeSlotKey SlotKey,
        MarketVolumeCohort Cohort,
        int SequenceMinute,
        TimeSpan Timeframe);

    private sealed class SessionVolumeState
    {
        public SortedDictionary<int, decimal> Slots { get; } = [];

        public DateTimeOffset? CoverageVerifiedThroughUtc { get; set; }
    }

    private sealed record VolumeEvidenceSeries(
        decimal?[] CumulativeRelativeVolume,
        decimal?[] SlotRelativeVolume,
        decimal?[] CumulativeMedianVolume,
        decimal?[] SlotMedianVolume,
        int[] CumulativeSampleCount,
        int[] SlotSampleCount,
        string?[] Cohort)
    {
        public static VolumeEvidenceSeries Unavailable(int count) => new(
            new decimal?[count],
            new decimal?[count],
            new decimal?[count],
            new decimal?[count],
            new int[count],
            new int[count],
            new string?[count]);
    }

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
        decimal?[] Rsi2,
        decimal?[] Atr,
        decimal?[] Adx,
        decimal?[] Obv,
        decimal?[] MacdLine,
        decimal?[] MacdSignal,
        decimal?[] MacdHistogram,
        decimal?[] BollingerMiddle,
        decimal?[] BollingerUpper,
        decimal?[] BollingerLower);
}

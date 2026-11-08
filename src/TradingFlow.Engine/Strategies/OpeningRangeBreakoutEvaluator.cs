using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;
using static TradingFlow.Engine.Strategies.IntradayFiveMinuteBars;

namespace TradingFlow.Engine.Strategies;

/// <summary>Builds a stop intent from frozen completed opening evidence; execution owns fill counts and re-entry state.</summary>
public sealed class OpeningRangeBreakoutEvaluator
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // Re-evaluate a completed session prefix without moving the opening range or borrowing a later volume observation.
    public IntradayBreakoutEvaluation Evaluate(StrategyDefinition strategy,
        IReadOnlyList<OhlcvBar> completedBars, IReadOnlyList<IndicatorSnapshot> completedSnapshots,
        IntradayBreakoutContext context, DateTimeOffset asOfUtc)
    {
        if (strategy.EntryRules.OpeningRangeBreakout is not { } policy)
            return Reject("opening_range_breakout_not_declared");
        policy.Validate(strategy);
        if (context.TickSize <= 0m || context.CalendarEvidenceAsOfUtc == default ||
            context.CalendarEvidenceAsOfUtc > asOfUtc || context.CalendarEvidenceAsOfUtc.Offset != TimeSpan.Zero)
            return Reject("opening_range_tick_or_calendar_unavailable");
        if (completedBars.Count == 0 || !CompletedSeriesIsValid(completedBars, asOfUtc) ||
            completedBars[^1].Timestamp.Add(Interval) != asOfUtc)
            return Reject("opening_range_completed_bar_evidence_invalid");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(strategy.Session.ExchangeTimezone);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(asOfUtc, zone).DateTime);
        if (!context.SessionSchedules.TryGetValue(date, out var session) || !session.IsTradingDay || session.TradeDate != date)
            return Reject("current_exchange_session_unavailable");
        if (policy.IsPublishedRecipe)
            return EvaluatePublishedRecipe(strategy, policy, completedBars, context, asOfUtc, date, session, zone);
        var open = AtUtc(date, session.RegularOpen, zone);
        var rangeCompletedAt = open.AddMinutes(15);
        var deadline = AtUtc(date, session.RegularClose, zone).Subtract(Interval);
        if (asOfUtc < rangeCompletedAt || asOfUtc >= deadline)
            return Reject("outside_opening_range_entry_window");
        if (!TryPreviousSession(context.SessionSchedules, date, out var previousSession))
            return Reject("previous_exchange_session_unavailable");
        var previousOpen = AtUtc(previousSession.TradeDate, previousSession.RegularOpen, zone);
        var previousClose = AtUtc(previousSession.TradeDate, previousSession.RegularClose, zone);
        var previousBars = completedBars.Where(bar => bar.Timestamp >= previousOpen && bar.Timestamp < previousClose).ToArray();
        if (!HasCompleteCoverage(previousBars, previousOpen, previousClose))
            return Reject("previous_session_five_minute_coverage_incomplete");
        var current = completedBars.Where(bar => bar.Timestamp >= open).ToArray();
        if (!HasCompleteCoverage(current, open, asOfUtc))
            return Reject("opening_range_current_session_coverage_incomplete");
        var opening = current.Take(3).ToArray();
        if (!TryOpeningBaseline(context, date, zone, rangeCompletedAt, opening[0], out var baselineMedian, out var baselineSha256))
            return Reject("opening_range_volume_baseline_unavailable");
        var snapshot = completedSnapshots.SingleOrDefault(item => item.Timestamp == opening[2].Timestamp);
        if (!HasOpeningVolumeEvidence(snapshot, opening, rangeCompletedAt, baselineMedian))
            return Reject("opening_range_volume_baseline_unavailable");
        if (snapshot!.RelativeVolume <= 1m)
            return Reject("opening_range_relative_volume_not_above_one");
        var openingOpen = opening[0].Open;
        var openingClose = opening[2].Close;
        if (openingClose == openingOpen) return Reject("opening_range_doji");
        var isLong = openingClose > openingOpen;
        var high = opening.Max(bar => bar.High);
        var low = opening.Min(bar => bar.Low);
        var tick = context.TickSize;
        if (!TrySubmittedLevels(policy, isLong, high, low, tick, out var trigger, out var stop))
            return Reject("entry_or_structural_stop_incompatible_with_tick");
        if (policy.PolicyId == OpeningRangeBreakoutRules.CompletedBreakoutPolicyId)
            return CompletedBreakout(policy, completedBars, current, isLong, trigger, tick, previousBars,
                opening, high, low, rangeCompletedAt, snapshot, baselineSha256, asOfUtc, deadline);
        var openingLastIndex = completedBars.Count - current.Length + 2;
        return new(new IntradayBreakoutSignal(isLong ? "long" : "short", "opening_range_stop", trigger, stop,
            tick, previousBars.Max(bar => bar.High), previousBars.Min(bar => bar.Low),
            opening[0].Timestamp, opening[2].Timestamp, asOfUtc, deadline, deadline, openingLastIndex,
            IntradayEntryOrderMode.StopMarket, high, low, openingOpen, openingClose, rangeCompletedAt,
            snapshot.RelativeVolume, snapshot.RelativeVolumeSampleCount, policy.MaxFilledParentTradesPerSession, true, baselineSha256), null, true);
    }

    // Published recipe (Zarattini, Barbon and Aziz): when the opening range completes, trade in the direction of its
    // body with a stop order at the range extreme, provided its volume is at least the configured multiple of the mean
    // of the same opening interval over the preceding sessions. The protective stop sits a fraction of the prior-session
    // daily ATR beyond the trigger. One order per stock per session, live until the session deadline.
    private static IntradayBreakoutEvaluation EvaluatePublishedRecipe(StrategyDefinition strategy, OpeningRangeBreakoutRules policy,
        IReadOnlyList<OhlcvBar> completedBars, IntradayBreakoutContext context, DateTimeOffset asOfUtc, DateOnly date,
        MarketSessionSchedule session, TimeZoneInfo zone)
    {
        var open = AtUtc(date, session.RegularOpen, zone);
        var rangeCompletedAt = open.AddMinutes(policy.RangeMinutes!.Value);
        var deadline = AtUtc(date, session.RegularClose, zone).Subtract(Interval);
        if (asOfUtc != rangeCompletedAt || asOfUtc >= deadline)
            return Reject(asOfUtc < rangeCompletedAt ? "outside_opening_range_entry_window" : "opening_range_order_placed_at_range_completion");
        var current = completedBars.Where(bar => bar.Timestamp >= open).ToArray();
        if (!HasCompleteCoverage(current, open, asOfUtc))
            return Reject("opening_range_current_session_coverage_incomplete");
        var opening = current.Take(policy.RangeBars).ToArray();
        if (!TryMeanOpeningBaseline(context, date, zone, rangeCompletedAt, opening[0], policy, out var baselineMean, out var baselineSha256))
            return Reject("opening_range_volume_baseline_unavailable");
        var relativeVolume = opening.Sum(bar => bar.Volume) / baselineMean;
        if (relativeVolume < policy.MinRelativeVolume!.Value)
            return Reject("opening_range_relative_volume_below_minimum");
        if (!TryDailyAtr(context, date, zone, policy.AtrSessions!.Value, opening[0], asOfUtc, out var atr, out var prior))
            return Reject("opening_range_daily_atr_unavailable");
        var openingOpen = opening[0].Open;
        var openingClose = opening[^1].Close;
        if (openingClose == openingOpen) return Reject("opening_range_doji");
        var isLong = openingClose > openingOpen;
        var high = opening.Max(bar => bar.High);
        var low = opening.Min(bar => bar.Low);
        var tick = context.TickSize;
        var trigger = (isLong ? decimal.Ceiling(high / tick) : decimal.Floor(low / tick)) * tick;
        var distance = strategy.ExitRules.StopAtrMultiple * atr;
        var stop = (isLong ? decimal.Floor((trigger - distance) / tick) : decimal.Ceiling((trigger + distance) / tick)) * tick;
        if (trigger <= 0m || stop <= 0m || !(isLong ? trigger > stop : trigger < stop))
            return Reject("entry_or_structural_stop_incompatible_with_tick");
        var rangeLastIndex = completedBars.Count - current.Length + opening.Length - 1;
        return new(new IntradayBreakoutSignal(isLong ? "long" : "short", "opening_range_recipe_stop", trigger, stop,
            tick, prior.High, prior.Low, opening[0].Timestamp, opening[^1].Timestamp, asOfUtc, deadline, deadline, rangeLastIndex,
            IntradayEntryOrderMode.StopMarket, high, low, openingOpen, openingClose, rangeCompletedAt,
            relativeVolume, policy.VolumeLookbackSessions, policy.MaxFilledParentTradesPerSession, false, baselineSha256,
            DailyAtr: atr), null, true);
    }

    // Mean opening-interval volume over exactly the configured immediately preceding sessions (the paper's 14-day average).
    private static bool TryMeanOpeningBaseline(IntradayBreakoutContext context, DateOnly current, TimeZoneInfo zone,
        DateTimeOffset completedAt, OhlcvBar currentOpening, OpeningRangeBreakoutRules policy, out decimal mean, out string? evidenceSha256)
    {
        mean = 0m;
        evidenceSha256 = null;
        var sessions = policy.VolumeLookbackSessions!.Value;
        var bars = context.OpeningRangeBaselineBars;
        if (bars is null || bars.Count != sessions * policy.RangeBars || !CompletedSeriesIsValid(bars, completedAt)) return false;
        var index = bars.Count - 1;
        var total = 0m;
        var used = new List<MarketSessionSchedule>(sessions);
        for (var sample = 0; sample < sessions; sample++)
        {
            if (!TryPreviousSession(context.SessionSchedules, current, out var session)) return false;
            var open = AtUtc(session.TradeDate, session.RegularOpen, zone);
            for (var slot = policy.RangeBars - 1; slot >= 0; slot--)
            {
                var bar = bars[index--];
                if (bar.Timestamp != open.AddMinutes(slot * 5) || bar.Ticker != currentOpening.Ticker ||
                    bar.DataFeed != currentOpening.DataFeed || bar.AdjustmentPolicy != currentOpening.AdjustmentPolicy)
                    return false;
                total += bar.Volume;
            }
            used.Add(session);
            current = session.TradeDate;
        }
        mean = total / sessions;
        evidenceSha256 = EvidenceCanonicalJson.ComputeSha256(new { sessions = used, openingBars = bars });
        return mean > 0m;
    }

    // Average true range over the configured number of completed preceding sessions; one more daily bar supplies the
    // first previous close. With exactly that history, Wilder's average equals the simple mean of the true ranges.
    private static bool TryDailyAtr(IntradayBreakoutContext context, DateOnly current, TimeZoneInfo zone, int sessions,
        OhlcvBar currentOpening, DateTimeOffset asOfUtc, out decimal atr, out OhlcvBar prior)
    {
        atr = 0m;
        prior = null!;
        var daily = context.PriorSessionDailyBars;
        if (daily is null || daily.Count != sessions + 1) return false;
        for (var index = daily.Count - 1; index >= 0; index--)
        {
            if (!TryPreviousSession(context.SessionSchedules, current, out var session)) return false;
            var bar = daily[index];
            if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(bar.Timestamp, zone).DateTime) != session.TradeDate ||
                bar.Ticker != currentOpening.Ticker || bar.DataFeed != currentOpening.DataFeed ||
                bar.AdjustmentPolicy != currentOpening.AdjustmentPolicy || bar.KnownAtUtc > asOfUtc ||
                bar.Low <= 0m || bar.High < bar.Low) return false;
            current = session.TradeDate;
        }
        var total = 0m;
        for (var index = 1; index < daily.Count; index++)
        {
            var previousClose = daily[index - 1].Close;
            total += Math.Max(daily[index].High - daily[index].Low,
                Math.Max(Math.Abs(daily[index].High - previousClose), Math.Abs(daily[index].Low - previousClose)));
        }
        atr = total / sessions;
        prior = daily[^1];
        return atr > 0m;
    }

    // Commit only the latest completed post-range candle; its close sizes the order without a future opening price.
    private static IntradayBreakoutEvaluation CompletedBreakout(OpeningRangeBreakoutRules policy,
        IReadOnlyList<OhlcvBar> bars, IReadOnlyList<OhlcvBar> current, bool isLong, decimal trigger, decimal tick,
        IReadOnlyList<OhlcvBar> previous, IReadOnlyList<OhlcvBar> opening, decimal high, decimal low,
        DateTimeOffset rangeCompletedAt, IndicatorSnapshot snapshot, string? baselineSha256,
        DateTimeOffset asOfUtc, DateTimeOffset deadline)
    {
        var candle = current[^1];
        if (candle.Timestamp < rangeCompletedAt || !(isLong ? candle.Close > trigger : candle.Close < trigger))
            return Reject("completed_post_opening_range_close_beyond_trigger_required");
        var stop = (isLong ? decimal.Floor(candle.Low / tick) : decimal.Ceiling(candle.High / tick)) * tick;
        if (stop <= 0m || !(isLong ? candle.Close > stop : candle.Close < stop))
            return Reject("entry_or_structural_stop_incompatible_with_tick");
        return new(new IntradayBreakoutSignal(isLong ? "long" : "short", "opening_range_completed_breakout",
            candle.Close, stop, tick, previous.Max(bar => bar.High), previous.Min(bar => bar.Low),
            candle.Timestamp, candle.Timestamp, asOfUtc, deadline, deadline, bars.Count - 1,
            IntradayEntryOrderMode.NextBarMarket, high, low, opening[0].Open, opening[2].Close, rangeCompletedAt,
            snapshot.RelativeVolume, snapshot.RelativeVolumeSampleCount, policy.MaxFilledParentTradesPerSession,
            true, baselineSha256, trigger), null, true);
    }

    // Preserve observed extrema; only the opted-in recipe rounds submitted orders outward with the full breakout tick.
    private static bool TrySubmittedLevels(OpeningRangeBreakoutRules policy, bool isLong,
        decimal high, decimal low, decimal tick, out decimal trigger, out decimal stop)
    {
        trigger = isLong ? high + tick : low - tick;
        stop = isLong ? low : high;
        if (high <= low || low <= tick) return false;
        if (policy.PolicyId is not (OpeningRangeBreakoutRules.OutwardOrderRoundingPolicyId or OpeningRangeBreakoutRules.CompletedBreakoutPolicyId))
            return high % tick == 0m && low % tick == 0m;
        trigger = (isLong ? decimal.Ceiling(trigger / tick) : decimal.Floor(trigger / tick)) * tick;
        stop = (isLong ? decimal.Floor(stop / tick) : decimal.Ceiling(stop / tick)) * tick;
        return trigger > 0m && stop > 0m && (isLong ? trigger > stop : trigger < stop);
    }

    // Validate only sixty supplied opening bars against the immediately preceding calendar sessions, never replacing missing sessions with older ones.
    private static bool TryOpeningBaseline(IntradayBreakoutContext context, DateOnly current, TimeZoneInfo zone,
        DateTimeOffset completedAt, OhlcvBar currentOpening, out decimal median, out string? evidenceSha256)
    {
        median = 0m;
        evidenceSha256 = null;
        var bars = context.OpeningRangeBaselineBars;
        if (bars is null || bars.Count != MarketEvidenceProfile.DefaultLookbackSessions * 3 ||
            !CompletedSeriesIsValid(bars, completedAt)) return false;
        var index = bars.Count - 1;
        var totals = new decimal[MarketEvidenceProfile.DefaultLookbackSessions];
        var sessions = new List<MarketSessionSchedule>(totals.Length);
        for (var sample = 0; sample < totals.Length; sample++)
        {
            if (!TryPreviousSession(context.SessionSchedules, current, out var session)) return false;
            var open = AtUtc(session.TradeDate, session.RegularOpen, zone);
            if (AtUtc(session.TradeDate, session.RegularClose, zone) < open.AddMinutes(15)) return false;
            for (var slot = 2; slot >= 0; slot--)
            {
                var bar = bars[index--];
                if (bar.Timestamp != open.AddMinutes(slot * 5) || bar.Ticker != currentOpening.Ticker ||
                    bar.DataFeed != currentOpening.DataFeed || bar.AdjustmentPolicy != currentOpening.AdjustmentPolicy)
                    return false;
                totals[sample] += bar.Volume;
            }
            sessions.Add(session);
            current = session.TradeDate;
        }
        Array.Sort(totals);
        median = (totals[totals.Length / 2 - 1] + totals[totals.Length / 2]) / 2m;
        evidenceSha256 = EvidenceCanonicalJson.ComputeSha256(new { sessions, openingBars = bars });
        return true;
    }

    // Require the existing production twenty-session median and the exact completed 09:40 snapshot on the same price/feed basis.
    private static bool HasOpeningVolumeEvidence(IndicatorSnapshot? snapshot, IReadOnlyList<OhlcvBar> opening,
        DateTimeOffset rangeCompletedAt, decimal baselineMedian)
    {
        if (snapshot is null || snapshot.Timestamp.Add(Interval) != rangeCompletedAt || snapshot.Timeframe != "5m" ||
            snapshot.Ticker != opening[0].Ticker || snapshot.DataFeed != opening[0].DataFeed ||
            snapshot.AdjustmentPolicy != opening[0].AdjustmentPolicy ||
            snapshot.MarketEvidenceReliability != "verified_same_feed" ||
            snapshot.MarketEvidenceProfileVersion != MarketEvidenceProfile.ProductionVersion ||
            snapshot.RelativeVolumeCohort != "regular" ||
            snapshot.RelativeVolumeMinimumSamples != MarketEvidenceProfile.DefaultMinimumValidSamples ||
            snapshot.RelativeVolumeSampleCount != MarketEvidenceProfile.DefaultLookbackSessions ||
            baselineMedian <= 0m || snapshot.CumulativeSameTimeMedianVolume != baselineMedian || snapshot.RelativeVolume is null)
            return false;
        return snapshot.RelativeVolume == opening.Sum(bar => bar.Volume) / snapshot.CumulativeSameTimeMedianVolume;
    }

    // Preserve a concrete missing/filter reason without manufacturing a trade or zero-volume observation.
    private static IntradayBreakoutEvaluation Reject(string reason) => new(null, reason);
}

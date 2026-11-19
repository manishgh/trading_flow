using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Engine.Strategies;

/// <summary>
/// Authoritative exchange-session boundaries in UTC for a single trading date, honoring early-close schedules
/// (for example 13:00 New York) when an exchange calendar is provided.
/// </summary>
public sealed record DailySessionBounds(
    bool IsTradingDay,
    DateOnly SessionDate,
    DateTimeOffset SessionOpenUtc,
    DateTimeOffset OpeningRangeEndUtc,
    DateTimeOffset SessionCloseUtc);

/// <summary>
/// Completed regular-session 5-minute confirmation summary for a single daily bar D (side-neutral so both long
/// <c>news_orb_swing</c> and short <c>news_orb_breakdown</c> consume identical evidence).
/// </summary>
public sealed record DailyOpeningRangeEvidence(
    bool IsComplete,
    decimal? OpeningRangeHigh,
    decimal? OpeningRangeLow,
    decimal? OpeningVolume,
    decimal? OpeningRelativeVolume,
    decimal? SessionVwap,
    bool? OpeningStayedAtOrBelowVwap,
    int BaselineSessionCount,
    string? UnavailableReason)
{
    /// <summary>Creates a fail-closed evidence record naming the exact missing sub-session condition.</summary>
    public static DailyOpeningRangeEvidence Unavailable(string reason, int baselineSessionCount = 0) =>
        new(false, null, null, null, null, null, null, baselineSessionCount, reason);
}

/// <summary>
/// Computes completed regular-session 5-minute opening-range high/low, first-N-minute relative volume (versus the
/// median of the prior 20 completed sessions' opening volumes), session VWAP, and opening-window VWAP containment
/// for a completed daily bar without look-ahead. Uses an <c>O(log M + 21 * sessionBars)</c> backward scan over
/// ascending 5-minute bars with exact timestamp deduplication and optional exchange early-close schedules.
/// </summary>
public static class DailyOpeningRangeSummary
{
    public static readonly TimeSpan FiveMinuteInterval = TimeSpan.FromMinutes(5);
    public const int BaselineSessionsRequired = 20;
    private static readonly TimeOnly DefaultRegularSessionOpen = new(9, 30);
    private static readonly TimeOnly DefaultRegularSessionClose = new(16, 0);

    /// <summary>
    /// Resolves a daily bar's exchange session date and UTC open, opening-range end, and regular close timestamps.
    /// When <paramref name="sessionSchedules"/> is supplied, non-trading dates return <c>IsTradingDay = false</c> and
    /// early-close sessions (such as 13:00 New York) bound <c>SessionCloseUtc</c> to <c>RegularClose</c>.
    /// </summary>
    public static DailySessionBounds ResolveSessionBounds(
        DateTimeOffset dailyBarTimestamp,
        string exchangeTimezone,
        int openingRangeMinutes,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? sessionSchedules = null)
    {
        var zone = ResolveTimeZone(exchangeTimezone);
        var sessionDate = ToExchangeDate(dailyBarTimestamp, zone);
        return ResolveSessionBoundsForDate(sessionDate, zone, openingRangeMinutes, sessionSchedules);
    }

    /// <summary>
    /// Summarizes completed regular-session 5-minute bars belonging to <paramref name="dailyBar"/>'s exchange session
    /// date with <c>barEndUtc &lt;= min(decisionTimeUtc, sessionCloseUtc)</c> and <c>KnownAtUtc &lt;= decisionTimeUtc</c>.
    /// Deduplicates exact bar timestamps, fails closed if any opening 5-minute bar is missing, regular-session volume
    /// is non-positive, or fewer than 20 prior trading sessions have complete opening bars.
    /// </summary>
    public static DailyOpeningRangeEvidence Compute(
        OhlcvBar dailyBar,
        IReadOnlyList<OhlcvBar>? fiveMinuteBars,
        int openingRangeMinutes,
        string exchangeTimezone,
        DateTimeOffset decisionTimeUtc,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? sessionSchedules = null,
        bool barsAreSortedAscending = false)
    {
        if (openingRangeMinutes <= 0 || openingRangeMinutes % 5 != 0)
        {
            return DailyOpeningRangeEvidence.Unavailable("invalid_opening_range_minutes");
        }

        if (fiveMinuteBars is null || fiveMinuteBars.Count == 0)
        {
            return DailyOpeningRangeEvidence.Unavailable("five_minute_bars_unavailable");
        }

        var zone = ResolveTimeZone(exchangeTimezone);
        var sessionDate = ToExchangeDate(dailyBar.Timestamp, zone);
        var currentBounds = ResolveSessionBoundsForDate(sessionDate, zone, openingRangeMinutes, sessionSchedules);
        if (!currentBounds.IsTradingDay)
        {
            return DailyOpeningRangeEvidence.Unavailable("current_exchange_session_unavailable");
        }

        var rawDecisionUtc = decisionTimeUtc.ToUniversalTime();
        var effectiveCutoffUtc = rawDecisionUtc < currentBounds.SessionCloseUtc
            ? rawDecisionUtc
            : currentBounds.SessionCloseUtc;

        var orderedBars = barsAreSortedAscending || IsNonDecreasing(fiveMinuteBars)
            ? fiveMinuteBars
            : fiveMinuteBars.OrderBy(bar => bar.Timestamp).ToArray();

        var upperBound = FindLastCompletedBarIndex(orderedBars, effectiveCutoffUtc);
        if (upperBound < 0)
        {
            return DailyOpeningRangeEvidence.Unavailable("current_session_five_minute_bars_missing");
        }

        var requiredOpeningBars = openingRangeMinutes / 5;
        var seenTimestamps = new HashSet<DateTimeOffset>();
        var currentSessionBarsReversed = new List<OhlcvBar>(80);

        // Current-day computed outputs, evaluated as soon as the backward scan crosses below sessionDate.
        var currentEvaluated = false;
        decimal openingRangeHigh = 0m;
        decimal openingRangeLow = 0m;
        decimal openingVolume = 0m;
        decimal sessionVwap = 0m;
        bool openingStayedAtOrBelowVwap = false;

        // Rolling state for the currently-scanned prior session (walking backward session-by-session).
        DateOnly? activePriorDate = null;
        DailySessionBounds? activePriorBounds = null;
        var activePriorOpeningReversed = new List<OhlcvBar>(requiredOpeningBars);
        var priorOpeningVolumes = new List<decimal>(BaselineSessionsRequired);

        // Local per-date bounds cache for the backward walk (avoid repeated dictionary/DST lookups on the ~78 bars of a day).
        DateOnly? cachedDate = null;
        DailySessionBounds? cachedBounds = null;

        for (var i = upperBound; i >= 0; i--)
        {
            var bar = orderedBars[i];
            if (bar.KnownAtUtc is { } knownAt && knownAt > rawDecisionUtc)
            {
                continue;
            }

            if (!seenTimestamps.Add(bar.Timestamp))
            {
                continue;
            }

            var barDate = ToExchangeDate(bar.Timestamp, zone);
            if (barDate > sessionDate)
            {
                continue;
            }

            if (barDate == sessionDate)
            {
                if (bar.Timestamp >= currentBounds.SessionOpenUtc && bar.Timestamp < currentBounds.SessionCloseUtc)
                {
                    currentSessionBarsReversed.Add(bar);
                }

                continue;
            }

            // First bar strictly before sessionDate: validate day D right away so incomplete days fail fast (~78 bars).
            if (!currentEvaluated)
            {
                currentEvaluated = true;
                var currentFailure = TryEvaluateCurrentSession(
                    currentSessionBarsReversed,
                    currentBounds.SessionOpenUtc,
                    requiredOpeningBars,
                    out openingRangeHigh,
                    out openingRangeLow,
                    out openingVolume,
                    out sessionVwap,
                    out openingStayedAtOrBelowVwap);
                if (currentFailure is not null)
                {
                    return currentFailure;
                }
            }

            if (activePriorDate is { } previousPriorDate && barDate != previousPriorDate)
            {
                TryRecordPriorSessionOpeningVolume(
                    activePriorOpeningReversed,
                    activePriorBounds!,
                    requiredOpeningBars,
                    priorOpeningVolumes);
                activePriorOpeningReversed.Clear();
                if (priorOpeningVolumes.Count >= BaselineSessionsRequired)
                {
                    break;
                }
            }

            if (cachedDate != barDate)
            {
                cachedDate = barDate;
                cachedBounds = ResolveSessionBoundsForDate(barDate, zone, openingRangeMinutes, sessionSchedules);
            }

            activePriorDate = barDate;
            activePriorBounds = cachedBounds;

            if (activePriorBounds!.IsTradingDay &&
                bar.Timestamp >= activePriorBounds.SessionOpenUtc &&
                bar.Timestamp < activePriorBounds.OpeningRangeEndUtc &&
                bar.Timestamp < activePriorBounds.SessionCloseUtc)
            {
                activePriorOpeningReversed.Add(bar);
            }
        }

        if (!currentEvaluated)
        {
            var currentFailure = TryEvaluateCurrentSession(
                currentSessionBarsReversed,
                currentBounds.SessionOpenUtc,
                requiredOpeningBars,
                out openingRangeHigh,
                out openingRangeLow,
                out openingVolume,
                out sessionVwap,
                out openingStayedAtOrBelowVwap);
            if (currentFailure is not null)
            {
                return currentFailure;
            }
        }

        if (activePriorDate is not null &&
            activePriorBounds is not null &&
            priorOpeningVolumes.Count < BaselineSessionsRequired)
        {
            TryRecordPriorSessionOpeningVolume(
                activePriorOpeningReversed,
                activePriorBounds,
                requiredOpeningBars,
                priorOpeningVolumes);
        }

        if (priorOpeningVolumes.Count < BaselineSessionsRequired)
        {
            return DailyOpeningRangeEvidence.Unavailable(
                "opening_range_baseline_sessions_insufficient",
                priorOpeningVolumes.Count);
        }

        var baselineMedian = RelativeVolume.Baseline(priorOpeningVolumes, VolumeBaselineAverage.Median);
        if (baselineMedian <= 0m)
        {
            return DailyOpeningRangeEvidence.Unavailable(
                "opening_range_baseline_volume_non_positive",
                priorOpeningVolumes.Count);
        }

        var openingRelativeVolume = openingVolume / baselineMedian;
        return new DailyOpeningRangeEvidence(
            IsComplete: true,
            OpeningRangeHigh: openingRangeHigh,
            OpeningRangeLow: openingRangeLow,
            OpeningVolume: openingVolume,
            OpeningRelativeVolume: openingRelativeVolume,
            SessionVwap: sessionVwap,
            OpeningStayedAtOrBelowVwap: openingStayedAtOrBelowVwap,
            BaselineSessionCount: priorOpeningVolumes.Count,
            UnavailableReason: null);
    }

    // Evaluates day D's regular-session bars once the backward scan finishes sessionDate, failing closed immediately
    // if day D is missing bars, any opening slot, or positive regular-session volume.
    private static DailyOpeningRangeEvidence? TryEvaluateCurrentSession(
        List<OhlcvBar> currentSessionBarsReversed,
        DateTimeOffset sessionOpenUtc,
        int requiredOpeningBars,
        out decimal openingRangeHigh,
        out decimal openingRangeLow,
        out decimal openingVolume,
        out decimal sessionVwap,
        out bool openingStayedAtOrBelowVwap)
    {
        openingRangeHigh = 0m;
        openingRangeLow = 0m;
        openingVolume = 0m;
        sessionVwap = 0m;
        openingStayedAtOrBelowVwap = false;

        if (currentSessionBarsReversed.Count == 0)
        {
            return DailyOpeningRangeEvidence.Unavailable("current_session_five_minute_bars_missing");
        }

        currentSessionBarsReversed.Reverse();
        var openingBars = ExtractExactOpeningBars(currentSessionBarsReversed, sessionOpenUtc, requiredOpeningBars);
        if (openingBars is null)
        {
            return DailyOpeningRangeEvidence.Unavailable("opening_range_five_minute_bars_incomplete");
        }

        if (!TryComputeSessionVwap(currentSessionBarsReversed, out sessionVwap))
        {
            return DailyOpeningRangeEvidence.Unavailable("session_vwap_volume_unavailable");
        }

        openingRangeHigh = openingBars.Max(bar => bar.High);
        openingRangeLow = openingBars.Min(bar => bar.Low);
        openingVolume = openingBars.Sum(bar => bar.Volume);
        openingStayedAtOrBelowVwap = CheckOpeningStayedAtOrBelowRunningVwap(openingBars);
        return null;
    }

    // Records a prior session's total opening-range volume when all required consecutive 5-minute opening slots exist.
    private static void TryRecordPriorSessionOpeningVolume(
        List<OhlcvBar> openingReversed,
        DailySessionBounds bounds,
        int requiredOpeningBars,
        List<decimal> priorOpeningVolumes)
    {
        if (!bounds.IsTradingDay || openingReversed.Count < requiredOpeningBars)
        {
            return;
        }

        openingReversed.Reverse();
        var openingBars = ExtractExactOpeningBars(openingReversed, bounds.SessionOpenUtc, requiredOpeningBars);
        if (openingBars is not null)
        {
            priorOpeningVolumes.Add(openingBars.Sum(bar => bar.Volume));
        }
    }

    // Binary-searches the highest index in an ascending 5-minute series whose bar completed by cutoffUtc.
    private static int FindLastCompletedBarIndex(IReadOnlyList<OhlcvBar> ascendingBars, DateTimeOffset cutoffUtc)
    {
        var lo = 0;
        var hi = ascendingBars.Count - 1;
        var result = -1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (ascendingBars[mid].Timestamp.Add(FiveMinuteInterval) <= cutoffUtc)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    // Checks whether bars are already in non-decreasing timestamp order so sorted callers skip allocation and sorting.
    private static bool IsNonDecreasing(IReadOnlyList<OhlcvBar> bars)
    {
        for (var i = 1; i < bars.Count; i++)
        {
            if (bars[i].Timestamp < bars[i - 1].Timestamp)
            {
                return false;
            }
        }

        return true;
    }

    // Extracts the exact consecutive 5-minute opening bars starting at sessionOpenUtc (09:30, 09:35, ...).
    // Returns null if any expected slot is missing or has invalid OHLC/volume.
    private static OhlcvBar[]? ExtractExactOpeningBars(
        IReadOnlyList<OhlcvBar> orderedSessionBars,
        DateTimeOffset sessionOpenUtc,
        int requiredOpeningBars)
    {
        var result = new OhlcvBar[requiredOpeningBars];
        var found = 0;
        var expectedTimestamp = sessionOpenUtc;

        for (var i = 0; i < orderedSessionBars.Count && found < requiredOpeningBars; i++)
        {
            var bar = orderedSessionBars[i];
            if (bar.Timestamp < expectedTimestamp)
            {
                continue;
            }

            if (bar.Timestamp != expectedTimestamp)
            {
                return null;
            }

            if (bar.High <= 0m || bar.Low <= 0m || bar.High < bar.Low || bar.Volume < 0m)
            {
                return null;
            }

            result[found] = bar;
            found++;
            expectedTimestamp = expectedTimestamp.Add(FiveMinuteInterval);
        }

        return found == requiredOpeningBars ? result : null;
    }

    // Computes typical-price ((H + L + C) / 3) volume-weighted average price over deduplicated regular-session 5m bars.
    private static bool TryComputeSessionVwap(IReadOnlyList<OhlcvBar> regularBars, out decimal vwap)
    {
        decimal weightedSum = 0m;
        decimal totalVolume = 0m;
        foreach (var bar in regularBars)
        {
            if (bar.Volume <= 0m)
            {
                continue;
            }

            var typicalPrice = (bar.High + bar.Low + bar.Close) / 3m;
            weightedSum += typicalPrice * bar.Volume;
            totalVolume += bar.Volume;
        }

        if (totalVolume <= 0m)
        {
            vwap = 0m;
            return false;
        }

        vwap = weightedSum / totalVolume;
        return true;
    }

    // Checks whether every completed opening-range 5-minute bar closes at or below the running session VWAP
    // (used by Checkpoint 8's short mirror news_orb_breakdown). Fails closed (false) if cumulative volume is zero.
    private static bool CheckOpeningStayedAtOrBelowRunningVwap(IReadOnlyList<OhlcvBar> openingBars)
    {
        decimal weightedSum = 0m;
        decimal cumulativeVolume = 0m;
        foreach (var bar in openingBars)
        {
            if (bar.Volume > 0m)
            {
                var typicalPrice = (bar.High + bar.Low + bar.Close) / 3m;
                weightedSum += typicalPrice * bar.Volume;
                cumulativeVolume += bar.Volume;
            }

            if (cumulativeVolume <= 0m)
            {
                return false;
            }

            var runningVwap = weightedSum / cumulativeVolume;
            if (bar.Close > runningVwap)
            {
                return false;
            }
        }

        return true;
    }

    // Resolves open, opening-range end, and regular close for a calendar date using authoritative schedules when present.
    private static DailySessionBounds ResolveSessionBoundsForDate(
        DateOnly date,
        TimeZoneInfo zone,
        int openingRangeMinutes,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? sessionSchedules)
    {
        TimeOnly openTime = DefaultRegularSessionOpen;
        TimeOnly closeTime = DefaultRegularSessionClose;
        if (sessionSchedules is not null)
        {
            if (!sessionSchedules.TryGetValue(date, out var schedule) || !schedule.IsTradingDay)
            {
                return new DailySessionBounds(false, date, default, default, default);
            }

            openTime = schedule.RegularOpen;
            closeTime = schedule.RegularClose;
        }

        var openUtc = ToExchangeUtc(date, openTime, zone);
        var closeUtc = ToExchangeUtc(date, closeTime, zone);
        var openingEndUtc = openingRangeMinutes > 0 ? openUtc.AddMinutes(openingRangeMinutes) : openUtc;
        return new DailySessionBounds(true, date, openUtc, openingEndUtc, closeUtc);
    }

    // Resolves the configured exchange timezone across Windows and Linux (IANA or Windows ID).
    private static TimeZoneInfo ResolveTimeZone(string timezoneId)
    {
        var id = string.IsNullOrWhiteSpace(timezoneId) ? "America/New_York" : timezoneId.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException) when (id.Equals("America/New_York", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    // Converts a UTC/offset instant to its calendar date in the exchange timezone.
    private static DateOnly ToExchangeDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    // Converts an exchange-local date and wall-clock time to UTC using the timezone's offset for that date.
    private static DateTimeOffset ToExchangeUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}

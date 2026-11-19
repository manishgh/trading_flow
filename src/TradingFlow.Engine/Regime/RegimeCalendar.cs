using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;

namespace TradingFlow.Engine.Regime;

/// <summary>
/// The set of trading days a market regime rule is "on" (e.g. SPY above its 50-day SMA).
/// Built no-lookahead from benchmark daily bars: a day's regime is decided only from bars dated
/// before that day. An inactive calendar never gates anything, so strategies without a regime
/// rule behave exactly as before.
/// </summary>
public sealed class RegimeCalendar
{
    private readonly IReadOnlySet<DateOnly> onDates;

    private readonly Func<DateTimeOffset, bool>? evaluateAt;

    // Option A of the 18 Nov 2026 design: the home-market fund's calendar (QQQ) and each symbol's listing exchange.
    private readonly RegimeCalendar? nasdaqCalendar;
    private readonly IReadOnlyDictionary<string, string>? listingExchanges;

    /// <summary>Rejection when the benchmark (for example SPY) fails the rule.</summary>
    public const string MarketOffReason = "market_regime_off_on_entry_day";
    /// <summary>Rejection when a Nasdaq-listed stock's home-market fund (QQQ) fails the rule.</summary>
    public const string NasdaqMarketOffReason = "nasdaq_market_regime_off_on_entry_day";
    /// <summary>Rejection when the home-market rule is on but the stock's listing exchange is not known (fails closed).</summary>
    public const string ListingExchangeUnknownReason = "listing_exchange_unknown";
    /// <summary>Rejection (live/paper) when the broker's asset lookup for the stock failed, for example a rate limit (fails closed).</summary>
    public const string ListingExchangeLookupFailedReason = "listing_exchange_lookup_failed";
    // The VIX reasons (vix_regime_off_on_entry_day and the two fail-closed reasons) live in VixRegime.

    // Checkpoint 3: pre-indexed benchmark bars (O(log N) session & freshness lookup), the VIX rule and the frozen closes.
    private readonly BenchmarkSessionIndex? benchmarkIndex;
    private readonly RegimeRules? vixRules;
    private readonly VixCloseSeries? vixCloses;
    // Frozen single-opportunity decision used by independent shadow replay to preserve exact rejection & risk scale.
    private readonly RegimeEntryDecision? fixedDecision;

    private RegimeCalendar(IReadOnlySet<DateOnly> onDates, bool active, Func<DateTimeOffset, bool>? evaluateAt = null,
        RegimeCalendar? nasdaqCalendar = null, IReadOnlyDictionary<string, string>? listingExchanges = null,
        BenchmarkSessionIndex? benchmarkIndex = null, RegimeRules? vixRules = null,
        VixCloseSeries? vixCloses = null, RegimeEntryDecision? fixedDecision = null)
    {
        this.onDates = onDates;
        IsActive = active;
        this.evaluateAt = evaluateAt;
        this.nasdaqCalendar = nasdaqCalendar;
        this.listingExchanges = listingExchanges;
        this.benchmarkIndex = benchmarkIndex;
        this.vixRules = vixRules;
        this.vixCloses = vixCloses;
        this.fixedDecision = fixedDecision;
    }

    /// <summary>True when this calendar applies VIX sizing rules.</summary>
    public bool UsesVix => vixRules is not null;

    /// <summary>
    /// The same benchmark calendar, extended so a stock listed on Nasdaq also needs <paramref name="nasdaq"/> (the QQQ
    /// calendar) to be on. <paramref name="exchanges"/> maps each symbol to its listing exchange (Alpaca codes).
    /// </summary>
    public RegimeCalendar WithNasdaqHomeMarket(RegimeCalendar nasdaq, IReadOnlyDictionary<string, string> exchanges) =>
        new(onDates, IsActive, evaluateAt, nasdaq ?? throw new ArgumentNullException(nameof(nasdaq)),
            exchanges ?? throw new ArgumentNullException(nameof(exchanges)), benchmarkIndex, vixRules, vixCloses, fixedDecision);

    /// <summary>
    /// The same calendar, extended with the VIX sizing of <paramref name="rules"/> over the frozen <paramref name="closes"/>.
    /// Needs a calendar built from benchmark bars (<see cref="RegimeCalendarBuilder.Build"/>), whose dates name the sessions.
    /// When <paramref name="schedules"/> is provided, also enforces authoritative exchange session freshness so stale
    /// benchmark bars fail closed instead of falling back to an older VIX close.
    /// </summary>
    public RegimeCalendar WithVix(RegimeRules rules, VixCloseSeries closes,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!rules.UsesVix) throw new ArgumentException("The regime rule declares no VIX sizing.", nameof(rules));
        rules.Validate();
        if (benchmarkIndex is null) throw new InvalidOperationException("VIX sizing needs a calendar built from the benchmark's daily bars.");
        var index = schedules is { Count: > 0 } ? benchmarkIndex.WithSchedules(schedules) : benchmarkIndex;
        return new(onDates, IsActive, index.IsOnAt, nasdaqCalendar, listingExchanges, index, rules,
            closes ?? throw new ArgumentNullException(nameof(closes)), fixedDecision);
    }

    /// <summary>
    /// Evaluates the complete market-regime gate once at <paramref name="decisionTime"/> for <paramref name="ticker"/>:
    /// benchmark (SPY) -> optional Nasdaq home-market (QQQ) -> optional VIX sizing. Returns the exact refusal reason,
    /// per-trade risk scale, and VIX reading without running the lookup twice.
    /// </summary>
    public RegimeEntryDecision EntryDecisionFor(DateTimeOffset decisionTime, string ticker)
    {
        if (fixedDecision is not null) return fixedDecision;
        if (!IsOn(decisionTime)) return RegimeEntryDecision.Refused(MarketOffReason);
        if (!IsActive) return RegimeEntryDecision.Allowed;
        if (nasdaqCalendar is not null)
        {
            if (!listingExchanges!.TryGetValue(ticker.Trim().ToUpperInvariant(), out var exchange))
                return RegimeEntryDecision.Refused(ListingExchangeUnknownReason);
            if (exchange.Equals(RegimeRules.NasdaqExchange, StringComparison.OrdinalIgnoreCase) &&
                !nasdaqCalendar.IsOn(decisionTime))
                return RegimeEntryDecision.Refused(NasdaqMarketOffReason);
        }
        if (vixRules is null) return RegimeEntryDecision.Allowed;
        var vix = VixDecisionAt(decisionTime);
        return new(vix.Rejection, vix.Rejection is null ? vix.RiskScale : 0m, vix);
    }

    /// <summary>
    /// Why a new entry in <paramref name="ticker"/> is not allowed at <paramref name="decisionTime"/>, or null when it is:
    /// the benchmark must be on; with the home-market rule a stock must have a known listing exchange, and a Nasdaq listing
    /// also needs the home-market fund on; with VIX sizing the previous session's VIX close must be published and not above
    /// the no-entry threshold. Without those rules this equals <see cref="IsOn(DateTimeOffset)"/>.
    /// </summary>
    public string? RejectionFor(DateTimeOffset decisionTime, string ticker) => EntryDecisionFor(decisionTime, ticker).Rejection;

    /// <summary>
    /// The per-trade risk scale of an allowed entry at <paramref name="decisionTime"/>: 1 without VIX sizing or with the
    /// previous session's VIX below the full-risk threshold, otherwise the reduced scale. Read only after
    /// <see cref="RejectionFor"/> allowed the entry.
    /// </summary>
    public decimal EntryRiskScaleAt(DateTimeOffset decisionTime) =>
        fixedDecision is not null ? fixedDecision.RiskScale : VixDecisionAt(decisionTime).RiskScale;

    /// <summary>The VIX rule's decision for an entry at <paramref name="decisionTime"/> (allowed at full risk when unused).</summary>
    public VixEntryDecision VixDecisionAt(DateTimeOffset decisionTime) => vixRules is null
        ? VixEntryDecision.NotRequired
        : VixRegime.Decide(vixRules, vixCloses, benchmarkIndex!.PreviousSessionAt(decisionTime));

    /// <summary>O(log N) previous completed benchmark session date at <paramref name="decisionTime"/>, when backed by bars.</summary>
    public DateOnly? PreviousSessionAt(DateTimeOffset decisionTime) => benchmarkIndex?.PreviousSessionAt(decisionTime);

    public bool IsActive { get; }

    public static RegimeCalendar Inactive { get; } = new(new HashSet<DateOnly>(), active: false);

    public static RegimeCalendar FromOnDates(IReadOnlySet<DateOnly> onDates) => new(onDates, active: true);

    /// <summary>
    /// A frozen single-decision calendar for independent opportunity shadow replay: preserves the exact original refusal
    /// reason (such as VIX or Nasdaq home-market refusal) and the exact per-entry risk scale without re-querying market bars.
    /// </summary>
    public static RegimeCalendar FromFixedDecision(string? rejection, decimal riskScale) =>
        new(new HashSet<DateOnly>(), active: true, evaluateAt: _ => rejection is null,
            fixedDecision: rejection is null ? new RegimeEntryDecision(null, riskScale) : RegimeEntryDecision.Refused(rejection));

    /// <summary>True if new entries are allowed on the given day. Always true when inactive.</summary>
    // Date-level convenience uses the end of that UTC date; execution decisions must use the timestamp overload.
    public bool IsOn(DateOnly day) => IsOn(new DateTimeOffset(day, TimeOnly.MaxValue, TimeSpan.Zero));

    public bool IsOn(DateTimeOffset decisionTime) => !IsActive ||
        (evaluateAt?.Invoke(decisionTime) ?? onDates.Contains(DateOnly.FromDateTime(decisionTime.UtcDateTime)));

    internal static RegimeCalendar FromBenchmark(IReadOnlySet<DateOnly> dates, IReadOnlyList<OhlcvBar> bars, int period, int slopeLookback,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules = null, bool bearish = false)
    {
        var index = new BenchmarkSessionIndex(bars, period, slopeLookback, schedules, bearish);
        return new(dates, true, index.IsOnAt, benchmarkIndex: index);
    }

    /// <summary>
    /// Pre-indexed benchmark daily bars sorted by availability timestamp so per-stock-day regime and previous-session
    /// queries execute in O(log N) time with zero allocations while maintaining exact parity with
    /// <see cref="VixRegime.PreviousSession"/> and <see cref="RegimeCalendarBuilder.IsRegimeOnAt"/>.
    /// </summary>
    private sealed class BenchmarkSessionIndex
    {
        private readonly IReadOnlyList<OhlcvBar> validBars;
        private readonly DateTimeOffset[] availableAt;
        private readonly DateOnly[] prefixMaxSession;
        private readonly bool[]? prefixIsOn;
        private readonly ScheduleIndex? scheduleIndex;
        private readonly int smaPeriod;
        private readonly int slopeLookback;
        private readonly bool bearish;

        public BenchmarkSessionIndex(
            IReadOnlyList<OhlcvBar> benchmarkDailyBars,
            int smaPeriod,
            int slopeLookback,
            IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules,
            bool bearish = false)
        {
            this.smaPeriod = smaPeriod;
            this.slopeLookback = slopeLookback;
            this.bearish = bearish;
            scheduleIndex = schedules is { Count: > 0 } ? new ScheduleIndex(schedules) : null;
            validBars = VixRegime.FilterTradingDayBars(benchmarkDailyBars, schedules);

            var entries = new (DateTimeOffset AvailableAt, DateTimeOffset Timestamp, DateOnly Day, decimal Close)[validBars.Count];
            for (var i = 0; i < validBars.Count; i++)
            {
                var bar = validBars[i];
                var end = bar.Timestamp.Add(TimeframeParser.Parse(bar.Timeframe));
                var available = bar.KnownAtUtc is { } known && known > end ? known : end;
                entries[i] = (available, bar.Timestamp, DateOnly.FromDateTime(bar.Timestamp.UtcDateTime), bar.Close);
            }
            Array.Sort(entries, static (a, b) =>
            {
                var cmp = a.AvailableAt.CompareTo(b.AvailableAt);
                return cmp != 0 ? cmp : a.Timestamp.CompareTo(b.Timestamp);
            });

            availableAt = new DateTimeOffset[entries.Length];
            prefixMaxSession = new DateOnly[entries.Length];
            var closes = new decimal[entries.Length];
            var strictlyIncreasingDays = true;
            var maxTimestamp = DateTimeOffset.MinValue;
            for (var i = 0; i < entries.Length; i++)
            {
                availableAt[i] = entries[i].AvailableAt;
                if (i == 0 || entries[i].Timestamp > maxTimestamp)
                    maxTimestamp = entries[i].Timestamp;
                prefixMaxSession[i] = DateOnly.FromDateTime(maxTimestamp.UtcDateTime);
                closes[i] = entries[i].Close;
                if (i > 0 && entries[i].Day <= entries[i - 1].Day)
                    strictlyIncreasingDays = false;
            }

            if (strictlyIncreasingDays && smaPeriod > 0)
            {
                var slope = Math.Max(0, slopeLookback);
                prefixIsOn = new bool[entries.Length];
                var prefixSums = new decimal[entries.Length + 1];
                for (var i = 0; i < entries.Length; i++)
                    prefixSums[i + 1] = prefixSums[i] + closes[i];
                var minCount = smaPeriod + slope;
                for (var i = minCount - 1; i < entries.Length; i++)
                {
                    var currentAvg = (prefixSums[i + 1] - prefixSums[i + 1 - smaPeriod]) / smaPeriod;
                    var pricePasses = bearish ? closes[i] < currentAvg : closes[i] > currentAvg;
                    if (!pricePasses) continue;
                    if (slope == 0)
                    {
                        prefixIsOn[i] = true;
                        continue;
                    }
                    var priorEnd = i + 1 - slope;
                    var priorAvg = (prefixSums[priorEnd] - prefixSums[priorEnd - smaPeriod]) / smaPeriod;
                    prefixIsOn[i] = bearish ? currentAvg < priorAvg : currentAvg > priorAvg;
                }
            }
        }

        /// <summary>Returns the date of the latest completed benchmark bar known at <paramref name="decisionTime"/>.</summary>
        public DateOnly? PreviousSessionAt(DateTimeOffset decisionTime)
        {
            var index = UpperBound(availableAt, decisionTime);
            return index < 0 ? null : prefixMaxSession[index];
        }

        /// <summary>
        /// Evaluates the benchmark SMA rule at <paramref name="decisionTime"/>, failing closed when an authoritative
        /// schedule shows a completed trading session newer than the latest known benchmark bar.
        /// </summary>
        public bool IsOnAt(DateTimeOffset decisionTime)
        {
            var index = UpperBound(availableAt, decisionTime);
            if (index < 0) return false;
            if (scheduleIndex is not null &&
                (!scheduleIndex.TryExpectedPreviousSession(decisionTime, out var expected) || prefixMaxSession[index] < expected))
                return false;
            return prefixIsOn is not null
                ? prefixIsOn[index]
                : RegimeCalendarBuilder.IsRegimeOnAt(validBars, smaPeriod, decisionTime, slopeLookback, bearish);
        }

        /// <summary>Rebuilds the index over the same bars bound to <paramref name="schedules"/>.</summary>
        public BenchmarkSessionIndex WithSchedules(IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules) =>
            new(validBars, smaPeriod, slopeLookback, schedules, bearish);

        private static int UpperBound(DateTimeOffset[] sorted, DateTimeOffset target)
        {
            int lo = 0, hi = sorted.Length - 1, result = -1;
            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                if (sorted[mid] <= target)
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
    }

    /// <summary>Pre-indexed trading-day receipt boundaries for O(log N) expected-previous-session checks.</summary>
    private sealed class ScheduleIndex
    {
        private readonly IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules;
        private readonly DateTimeOffset[] tradingDayReceipts;
        private readonly DateOnly[] tradingDays;

        public ScheduleIndex(IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules)
        {
            this.schedules = schedules;
            var zone = VixRegime.NewYorkTimeZone;
            var ordered = schedules
                .Where(pair => pair.Value.IsTradingDay)
                .OrderBy(pair => pair.Key)
                .ToArray();
            tradingDays = new DateOnly[ordered.Length];
            tradingDayReceipts = new DateTimeOffset[ordered.Length];
            for (var i = 0; i < ordered.Length; i++)
            {
                var day = ordered[i].Key;
                var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
                tradingDays[i] = day;
                tradingDayReceipts[i] = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime().AddDays(1);
            }
        }

        /// <summary>
        /// Resolves the latest completed trading session at <paramref name="decisionTime"/> and checks calendar
        /// continuity through the decision's New York date, matching ExpectedCompletedSessions(..., 1).
        /// </summary>
        public bool TryExpectedPreviousSession(DateTimeOffset decisionTime, out DateOnly expected)
        {
            int lo = 0, hi = tradingDayReceipts.Length - 1, index = -1;
            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                if (tradingDayReceipts[mid] <= decisionTime)
                {
                    index = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            if (index < 0)
            {
                expected = default;
                return false;
            }
            expected = tradingDays[index];
            var nyToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(decisionTime, VixRegime.NewYorkTimeZone).DateTime);
            for (var day = expected; day <= nyToday; day = day.AddDays(1))
            {
                if (!schedules.ContainsKey(day))
                {
                    expected = default;
                    return false;
                }
            }
            return true;
        }
    }
}

public static class RegimeCalendarBuilder
{
    /// <summary>
    /// Builds the regime calendar for either a bullish ("benchmark price above its N-day SMA", <paramref name="bearish"/> = false)
    /// or bearish ("benchmark price below its falling N-day SMA", <paramref name="bearish"/> = true) rule. For each
    /// trading day D, the regime is on iff the most recent benchmark close strictly before D passes the comparison against
    /// the N-session average ending on that prior day. Using only pre-D bars is the no-lookahead guarantee. When
    /// <paramref name="schedules"/> is provided, non-trading-day bars are ignored and stale benchmark histories fail closed.
    /// </summary>
    /// <param name="risingLookback">0 = off; otherwise the average must also rise (bullish) or fall (bearish) relative to that many sessions earlier.</param>
    public static RegimeCalendar Build(IReadOnlyList<OhlcvBar> benchmarkDailyBars, int smaPeriod, int risingLookback = 0,
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? schedules = null, bool bearish = false)
    {
        if (smaPeriod <= 0) return RegimeCalendar.Inactive;
        var filtered = VixRegime.FilterTradingDayBars(benchmarkDailyBars, schedules);
        if (filtered.Count == 0) return RegimeCalendar.FromOnDates(new HashSet<DateOnly>());

        // Collapse to one close per date, ascending.
        var ordered = filtered
            .GroupBy(bar => DateOnly.FromDateTime(bar.Timestamp.UtcDateTime))
            .Select(group => (Day: group.Key, Close: group.OrderBy(bar => bar.Timestamp).Last().Close))
            .OrderBy(item => item.Day)
            .ToArray();
        var closes = ordered.Select(item => item.Close).ToArray();

        var onDates = new HashSet<DateOnly>();
        for (var index = 1; index < ordered.Length; index++)
        {
            // Day D is decided from the closes ending on the day before D (indices up to index-1).
            if (IsOn(closes.AsSpan(0, index), smaPeriod, risingLookback, bearish))
            {
                onDates.Add(ordered[index].Day);
            }
        }

        return RegimeCalendar.FromBenchmark(onDates, filtered, smaPeriod, risingLookback, schedules, bearish);
    }

    /// <summary>
    /// The one regime rule, applied to completed daily closes in date order ending with the latest close known before the
    /// decision: bullish ON when close &gt; SMA and (when slopeLookback &gt; 0) SMA &gt; earlier SMA; bearish ON when
    /// close &lt; SMA and (when slopeLookback &gt; 0) SMA &lt; earlier SMA. Too few closes for either average is OFF (fail closed).
    /// </summary>
    internal static bool IsOn(ReadOnlySpan<decimal> priorCloses, int period, int slopeLookback, bool bearish = false)
    {
        var slope = Math.Max(0, slopeLookback);
        if (period <= 0 || priorCloses.Length < period + slope) return false;
        var current = Average(priorCloses.Slice(priorCloses.Length - period, period));
        if (bearish ? priorCloses[^1] >= current : priorCloses[^1] <= current) return false;
        if (slope == 0) return true;
        var prior = Average(priorCloses.Slice(priorCloses.Length - period - slope, period));
        return bearish ? current < prior : current > prior;
    }

    // Arithmetic mean of a non-empty window of closes.
    private static decimal Average(ReadOnlySpan<decimal> window)
    {
        decimal sum = 0m;
        foreach (var close in window) sum += close;
        return sum / window.Length;
    }

    /// <summary>
    /// No-lookahead point query: is the regime on for entering at <paramref name="decisionTime"/>? Uses only
    /// benchmark closes completed before that time. Insufficient history returns false, matching <see cref="Build"/>.
    /// </summary>
    public static bool IsRegimeOnAt(IReadOnlyList<OhlcvBar> bars, int period, DateTimeOffset decisionTime, int risingLookback = 0, bool bearish = false)
    {
        if (period <= 0 || bars.Count == 0) return false;
        var known = bars.Where(bar => bar.Timestamp.Add(TimeframeParser.Parse(bar.Timeframe)) <= decisionTime &&
                (bar.KnownAtUtc is null || bar.KnownAtUtc <= decisionTime))
            .GroupBy(bar => DateOnly.FromDateTime(bar.Timestamp.UtcDateTime))
            .Select(group => group.OrderBy(bar => bar.KnownAtUtc ?? bar.Timestamp.Add(TimeframeParser.Parse(bar.Timeframe))).Last())
            .OrderBy(bar => bar.Timestamp).TakeLast(period + Math.Max(0, risingLookback)).Select(bar => bar.Close).ToArray();
        return IsOn(known, period, risingLookback, bearish);
    }

    /// <summary>Date-level query: the regime for entering on <paramref name="asOfDay"/> from closes dated strictly before it.</summary>
    public static bool IsRegimeOnAsOf(IReadOnlyList<OhlcvBar> benchmarkDailyBars, int smaPeriod, DateOnly asOfDay, int risingLookback = 0, bool bearish = false)
    {
        if (smaPeriod <= 0 || benchmarkDailyBars.Count == 0)
        {
            return false;
        }

        var priorCloses = benchmarkDailyBars
            .GroupBy(bar => DateOnly.FromDateTime(bar.Timestamp.UtcDateTime))
            .Where(group => group.Key < asOfDay)
            .Select(group => (Day: group.Key, Close: group.OrderBy(bar => bar.Timestamp).Last().Close))
            .OrderBy(item => item.Day)
            .Select(item => item.Close)
            .ToArray();
        return IsOn(priorCloses, smaPeriod, risingLookback, bearish);
    }
}

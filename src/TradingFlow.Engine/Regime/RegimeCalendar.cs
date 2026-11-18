using TradingFlow.Domain.Market;
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

    private RegimeCalendar(IReadOnlySet<DateOnly> onDates, bool active, Func<DateTimeOffset, bool>? evaluateAt = null,
        RegimeCalendar? nasdaqCalendar = null, IReadOnlyDictionary<string, string>? listingExchanges = null)
    {
        this.onDates = onDates;
        IsActive = active;
        this.evaluateAt = evaluateAt;
        this.nasdaqCalendar = nasdaqCalendar;
        this.listingExchanges = listingExchanges;
    }

    /// <summary>
    /// The same benchmark calendar, extended so a stock listed on Nasdaq also needs <paramref name="nasdaq"/> (the QQQ
    /// calendar) to be on. <paramref name="exchanges"/> maps each symbol to its listing exchange (Alpaca codes).
    /// </summary>
    public RegimeCalendar WithNasdaqHomeMarket(RegimeCalendar nasdaq, IReadOnlyDictionary<string, string> exchanges) =>
        new(onDates, IsActive, evaluateAt, nasdaq ?? throw new ArgumentNullException(nameof(nasdaq)),
            exchanges ?? throw new ArgumentNullException(nameof(exchanges)));

    /// <summary>
    /// Why a new entry in <paramref name="ticker"/> is not allowed at <paramref name="decisionTime"/>, or null when it is:
    /// the benchmark must be on; with the home-market rule a stock must have a known listing exchange, and a Nasdaq listing
    /// also needs the home-market fund on. Without the home-market rule this equals <see cref="IsOn(DateTimeOffset)"/>.
    /// </summary>
    public string? RejectionFor(DateTimeOffset decisionTime, string ticker)
    {
        if (!IsOn(decisionTime)) return MarketOffReason;
        if (!IsActive || nasdaqCalendar is null) return null;
        if (!listingExchanges!.TryGetValue(ticker.Trim().ToUpperInvariant(), out var exchange)) return ListingExchangeUnknownReason;
        return exchange.Equals(TradingFlow.Domain.Strategies.RegimeRules.NasdaqExchange, StringComparison.OrdinalIgnoreCase) &&
            !nasdaqCalendar.IsOn(decisionTime) ? NasdaqMarketOffReason : null;
    }

    public bool IsActive { get; }

    public static RegimeCalendar Inactive { get; } = new(new HashSet<DateOnly>(), active: false);

    public static RegimeCalendar FromOnDates(IReadOnlySet<DateOnly> onDates) => new(onDates, active: true);

    /// <summary>True if new entries are allowed on the given day. Always true when inactive.</summary>
    // Date-level convenience uses the end of that UTC date; execution decisions must use the timestamp overload.
    public bool IsOn(DateOnly day) => IsOn(new DateTimeOffset(day, TimeOnly.MaxValue, TimeSpan.Zero));

    public bool IsOn(DateTimeOffset decisionTime) => !IsActive ||
        (evaluateAt?.Invoke(decisionTime) ?? onDates.Contains(DateOnly.FromDateTime(decisionTime.UtcDateTime)));

    internal static RegimeCalendar FromBenchmark(IReadOnlySet<DateOnly> dates, IReadOnlyList<OhlcvBar> bars, int period, int risingLookback)
    {
        var frozen = bars.ToArray();
        return new(dates, true, time => RegimeCalendarBuilder.IsRegimeOnAt(frozen, period, time, risingLookback));
    }
}

public static class RegimeCalendarBuilder
{
    /// <summary>
    /// Builds the regime calendar for a "benchmark price above its N-day SMA" rule. For each
    /// trading day D, the regime is on iff the most recent benchmark close strictly before D
    /// exceeds the average of the N closes ending on that prior day. Using only pre-D bars is
    /// the no-lookahead guarantee (a day never sees its own or any future close).
    /// </summary>
    /// <param name="risingLookback">0 = off; otherwise the average must also exceed the average ending that many sessions earlier.</param>
    public static RegimeCalendar Build(IReadOnlyList<OhlcvBar> benchmarkDailyBars, int smaPeriod, int risingLookback = 0)
    {
        if (smaPeriod <= 0) return RegimeCalendar.Inactive;
        if (benchmarkDailyBars.Count == 0) return RegimeCalendar.FromOnDates(new HashSet<DateOnly>());

        // Collapse to one close per date, ascending.
        var ordered = benchmarkDailyBars
            .GroupBy(bar => DateOnly.FromDateTime(bar.Timestamp.UtcDateTime))
            .Select(group => (Day: group.Key, Close: group.OrderBy(bar => bar.Timestamp).Last().Close))
            .OrderBy(item => item.Day)
            .ToArray();
        var closes = ordered.Select(item => item.Close).ToArray();

        var onDates = new HashSet<DateOnly>();
        for (var index = 1; index < ordered.Length; index++)
        {
            // Day D is decided from the closes ending on the day before D (indices up to index-1).
            if (IsOn(closes.AsSpan(0, index), smaPeriod, risingLookback))
            {
                onDates.Add(ordered[index].Day);
            }
        }

        return RegimeCalendar.FromBenchmark(onDates, benchmarkDailyBars, smaPeriod, risingLookback);
    }

    /// <summary>
    /// The one regime rule, applied to completed daily closes in date order ending with the latest close known before the
    /// decision: ON when that close is above the average of the last <paramref name="period"/> closes and, when
    /// <paramref name="risingLookback"/> is positive, that average is above the average ending <paramref name="risingLookback"/>
    /// sessions earlier. Too few closes for either average is OFF (fail closed).
    /// </summary>
    internal static bool IsOn(ReadOnlySpan<decimal> priorCloses, int period, int risingLookback)
    {
        var rising = Math.Max(0, risingLookback);
        if (period <= 0 || priorCloses.Length < period + rising) return false;
        var current = Average(priorCloses.Slice(priorCloses.Length - period, period));
        if (priorCloses[^1] <= current) return false;
        return rising == 0 || current > Average(priorCloses.Slice(priorCloses.Length - period - rising, period));
    }

    // Arithmetic mean of a non-empty window of closes.
    private static decimal Average(ReadOnlySpan<decimal> window)
    {
        decimal sum = 0m;
        foreach (var close in window) sum += close;
        return sum / window.Length;
    }

    /// <summary>
    /// No-lookahead point query: is the regime on for entering on <paramref name="asOfDay"/>? Uses only
    /// benchmark closes dated strictly before that day, so it is robust to a still-forming daily bar for
    /// today (used by the live/paper path). Insufficient history returns false, matching <see cref="Build"/>.
    /// This is the single-day equivalent of an on-date produced by <see cref="Build"/>.
    /// </summary>
    public static bool IsRegimeOnAt(IReadOnlyList<OhlcvBar> bars, int period, DateTimeOffset decisionTime, int risingLookback = 0)
    {
        if (period <= 0 || bars.Count == 0) return false;
        var known = bars.Where(bar => bar.Timestamp.Add(TimeframeParser.Parse(bar.Timeframe)) <= decisionTime &&
                (bar.KnownAtUtc is null || bar.KnownAtUtc <= decisionTime))
            .GroupBy(bar => DateOnly.FromDateTime(bar.Timestamp.UtcDateTime))
            .Select(group => group.OrderBy(bar => bar.KnownAtUtc ?? bar.Timestamp.Add(TimeframeParser.Parse(bar.Timeframe))).Last())
            .OrderBy(bar => bar.Timestamp).TakeLast(period + Math.Max(0, risingLookback)).Select(bar => bar.Close).ToArray();
        return IsOn(known, period, risingLookback);
    }

    /// <summary>Date-level query: the regime for entering on <paramref name="asOfDay"/> from closes dated strictly before it.</summary>
    public static bool IsRegimeOnAsOf(IReadOnlyList<OhlcvBar> benchmarkDailyBars, int smaPeriod, DateOnly asOfDay, int risingLookback = 0)
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
        return IsOn(priorCloses, smaPeriod, risingLookback);
    }
}

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

    private RegimeCalendar(IReadOnlySet<DateOnly> onDates, bool active, Func<DateTimeOffset, bool>? evaluateAt = null)
    {
        this.onDates = onDates;
        IsActive = active;
        this.evaluateAt = evaluateAt;
    }

    public bool IsActive { get; }

    public static RegimeCalendar Inactive { get; } = new(new HashSet<DateOnly>(), active: false);

    public static RegimeCalendar FromOnDates(IReadOnlySet<DateOnly> onDates) => new(onDates, active: true);

    /// <summary>True if new entries are allowed on the given day. Always true when inactive.</summary>
    // Date-level convenience uses the end of that UTC date; execution decisions must use the timestamp overload.
    public bool IsOn(DateOnly day) => IsOn(new DateTimeOffset(day, TimeOnly.MaxValue, TimeSpan.Zero));

    public bool IsOn(DateTimeOffset decisionTime) => !IsActive ||
        (evaluateAt?.Invoke(decisionTime) ?? onDates.Contains(DateOnly.FromDateTime(decisionTime.UtcDateTime)));

    internal static RegimeCalendar FromBenchmark(IReadOnlySet<DateOnly> dates, IReadOnlyList<OhlcvBar> bars, int period)
    {
        var frozen = bars.ToArray();
        return new(dates, true, time => RegimeCalendarBuilder.IsRegimeOnAt(frozen, period, time));
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
    public static RegimeCalendar Build(IReadOnlyList<OhlcvBar> benchmarkDailyBars, int smaPeriod)
    {
        if (smaPeriod <= 0) return RegimeCalendar.Inactive;
        if (benchmarkDailyBars.Count == 0) return RegimeCalendar.FromOnDates(new HashSet<DateOnly>());

        // Collapse to one close per date, ascending.
        var ordered = benchmarkDailyBars
            .GroupBy(bar => DateOnly.FromDateTime(bar.Timestamp.UtcDateTime))
            .Select(group => (Day: group.Key, Close: group.OrderBy(bar => bar.Timestamp).Last().Close))
            .OrderBy(item => item.Day)
            .ToArray();

        var onDates = new HashSet<DateOnly>();
        for (var index = smaPeriod; index < ordered.Length; index++)
        {
            // Window is the N closes ending on the day before D (indices [index-smaPeriod, index-1]).
            var window = ordered.AsSpan(index - smaPeriod, smaPeriod);
            decimal sum = 0m;
            foreach (var item in window)
            {
                sum += item.Close;
            }

            var sma = sum / smaPeriod;
            var latestPriorClose = ordered[index - 1].Close;
            if (latestPriorClose > sma)
            {
                onDates.Add(ordered[index].Day);
            }
        }

        return RegimeCalendar.FromBenchmark(onDates, benchmarkDailyBars, smaPeriod);
    }

    /// <summary>
    /// No-lookahead point query: is the regime on for entering on <paramref name="asOfDay"/>? Uses only
    /// benchmark closes dated strictly before that day, so it is robust to a still-forming daily bar for
    /// today (used by the live/paper path). Insufficient history returns false, matching <see cref="Build"/>.
    /// This is the single-day equivalent of an on-date produced by <see cref="Build"/>.
    /// </summary>
    public static bool IsRegimeOnAt(IReadOnlyList<OhlcvBar> bars, int period, DateTimeOffset decisionTime)
    {
        if (period <= 0 || bars.Count == 0) return false;
        var known = bars.Where(bar => bar.Timestamp.Add(TimeframeParser.Parse(bar.Timeframe)) <= decisionTime &&
                (bar.KnownAtUtc is null || bar.KnownAtUtc <= decisionTime))
            .GroupBy(bar => DateOnly.FromDateTime(bar.Timestamp.UtcDateTime))
            .Select(group => group.OrderBy(bar => bar.KnownAtUtc ?? bar.Timestamp.Add(TimeframeParser.Parse(bar.Timeframe))).Last())
            .OrderBy(bar => bar.Timestamp).TakeLast(period).ToArray();
        return known.Length == period && known[^1].Close > known.Average(bar => bar.Close);
    }

    public static bool IsRegimeOnAsOf(IReadOnlyList<OhlcvBar> benchmarkDailyBars, int smaPeriod, DateOnly asOfDay)
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
            .ToArray();

        if (priorCloses.Length < smaPeriod)
        {
            return false;
        }

        var window = priorCloses.AsSpan(priorCloses.Length - smaPeriod, smaPeriod);
        decimal sum = 0m;
        foreach (var item in window)
        {
            sum += item.Close;
        }

        var sma = sum / smaPeriod;
        return priorCloses[^1].Close > sma;
    }
}

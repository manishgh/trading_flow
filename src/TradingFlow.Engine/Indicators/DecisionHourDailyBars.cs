using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Indicators;

/// <summary>
/// The generic decision-hour primitive (execution timing decision-hour-close-last-hour-fill-as-traded-v1): a daily strategy
/// decides one hour before the session's regular close and fills in that last hour. For each session it builds a provisional
/// daily bar known at the decision time (open = the session's daily open; high, low and volume of the hourly bars that end by
/// the decision time; close = the close of the hourly bar ending at the decision time) and finds the last-hour bar the orders
/// fill on. On a half day the decision time is one hour before the early close. Nothing after the decision time is read.
/// </summary>
public static class DecisionHourDailyBars
{
    /// <summary>Skip reason: the hourly bar ending at the decision time is missing, so the session has no decision price.</summary>
    public const string DecisionBarMissingReason = "decision_hour_bar_missing";

    /// <summary>Skip reason: the last-hour bar the orders would fill on is missing.</summary>
    public const string LastHourBarMissingReason = "last_hour_bar_missing";

    /// <summary>One session's decision evidence: the provisional daily bar, the last-hour fill bar and the decision instant.</summary>
    public sealed record SessionDecision(DateOnly Session, OhlcvBar ProvisionalDailyBar, OhlcvBar LastHourBar, DateTimeOffset DecisionTimeUtc);

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>The decision instant in UTC: one hour before the session's regular (or early) close in the exchange time zone.</summary>
    public static DateTimeOffset DecisionTimeUtc(MarketSessionSchedule session, TimeZoneInfo exchangeZone)
    {
        if (!session.IsTradingDay) throw new ArgumentException($"{session.TradeDate:yyyy-MM-dd} is not a trading day.", nameof(session));
        var close = session.TradeDate.ToDateTime(session.RegularClose, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(close, exchangeZone), TimeSpan.Zero) - Hour;
    }

    /// <summary>
    /// Builds one session's decision evidence from its completed daily bar (only its open is read) and its hourly bars, or
    /// returns the named reason the session cannot be decided. Hourly bars are clock-aligned one-hour bars of the same stock.
    /// </summary>
    public static (SessionDecision? Decision, string? SkipReason) Build(OhlcvBar dailyBar, IReadOnlyList<OhlcvBar> sessionHourlyBars,
        MarketSessionSchedule session, TimeZoneInfo exchangeZone)
    {
        var decisionTime = DecisionTimeUtc(session, exchangeZone);
        var decisionBar = sessionHourlyBars.FirstOrDefault(bar => bar.Timestamp == decisionTime - Hour);
        if (decisionBar is null) return (null, DecisionBarMissingReason);
        var lastHour = sessionHourlyBars.FirstOrDefault(bar => bar.Timestamp == decisionTime);
        if (lastHour is null) return (null, LastHourBarMissingReason);
        var known = sessionHourlyBars.Where(bar => bar.Timestamp + Hour <= decisionTime).ToArray();
        var provisional = dailyBar with
        {
            High = Math.Max(dailyBar.Open, known.Max(bar => bar.High)),
            Low = Math.Min(dailyBar.Open, known.Min(bar => bar.Low)),
            Close = decisionBar.Close,
            Volume = known.Sum(bar => bar.Volume),
            KnownAtUtc = decisionTime,
            CoverageVerifiedThroughUtc = null
        };
        return (new SessionDecision(session.TradeDate, provisional, lastHour, decisionTime), null);
    }

    /// <summary>Window kind: every regular-session hourly bar is present, so only 09:30 to the first bar (10:00) is unseen.</summary>
    public const string FirstHalfHourWindow = "first_half_hour";

    /// <summary>Window kind: some regular-session hourly bars are missing, so their part of the session is unseen too.</summary>
    public const string MissingHourlyBarsWindow = "missing_hourly_bar";

    /// <summary>
    /// The part of a session the hourly bars do not show, as one slice from the regular open to the first hourly bar (or the
    /// close when there is none), for resting stop and limit orders only: open = the daily open; its low (high) is the daily
    /// low (high) when no hourly bar reaches it, so a touch seen only in the completed daily bar is placed here, before every
    /// hourly bar; close = the first hourly bar's open (the daily close without hourly bars). Never an input to a decision.
    /// </summary>
    public sealed record UnresolvedWindow(OhlcvBar Bar, DateTimeOffset EndUtc, string Kind);

    /// <summary>True when an hourly bar starts inside the session's [regular open, scheduled close), early closes included.</summary>
    public static bool IsRegularSessionHour(OhlcvBar hourlyBar, MarketSessionSchedule session, TimeZoneInfo exchangeZone)
    {
        if (!session.IsTradingDay) return false;
        var (open, close) = RegularSessionUtc(session, exchangeZone);
        return hourlyBar.Timestamp >= open && hourlyBar.Timestamp < close;
    }

    /// <summary>
    /// The unresolved window of a session (see <see cref="UnresolvedWindow"/>) from its completed daily bar and its
    /// regular-session hourly bars; null when the first hourly bar starts at the open, so nothing is unseen before it.
    /// </summary>
    public static UnresolvedWindow? OpeningWindow(OhlcvBar dailyBar, IReadOnlyList<OhlcvBar> regularHourlyBars,
        MarketSessionSchedule session, TimeZoneInfo exchangeZone)
    {
        var (open, close) = RegularSessionUtc(session, exchangeZone);
        var hourly = regularHourlyBars.OrderBy(bar => bar.Timestamp).ToArray();
        var end = hourly.Length > 0 ? hourly[0].Timestamp : close;
        if (end <= open) return null;
        var last = hourly.Length > 0 ? hourly[0].Open : dailyBar.Close;
        var low = hourly.Length == 0 || dailyBar.Low < hourly.Min(bar => bar.Low) ? dailyBar.Low : last;
        var high = hourly.Length == 0 || dailyBar.High > hourly.Max(bar => bar.High) ? dailyBar.High : last;
        var unseenVolume = dailyBar.Volume - hourly.Sum(bar => bar.Volume);
        var window = dailyBar with
        {
            Timestamp = open, Timeframe = "1h", High = Math.Max(high, Math.Max(dailyBar.Open, last)),
            Low = Math.Min(low, Math.Min(dailyBar.Open, last)), Close = last, Volume = unseenVolume > 0 ? unseenVolume : dailyBar.Volume,
            KnownAtUtc = null, CoverageVerifiedThroughUtc = null
        };
        var kind = hourly.Length == ExpectedHourStarts(open, close).Count() ? FirstHalfHourWindow : MissingHourlyBarsWindow;
        return new(window, end, kind);
    }

    // The regular open and the scheduled (or early) close of a trading session, in UTC.
    private static (DateTimeOffset Open, DateTimeOffset Close) RegularSessionUtc(MarketSessionSchedule session, TimeZoneInfo zone) =>
        (Utc(session.TradeDate, session.RegularOpen, zone), Utc(session.TradeDate, session.RegularClose, zone));

    // The clock-aligned hourly bar starts inside [open, close): 10:00 to 15:00 on a regular session.
    private static IEnumerable<DateTimeOffset> ExpectedHourStarts(DateTimeOffset open, DateTimeOffset close)
    {
        var first = new DateTimeOffset(open.Year, open.Month, open.Day, open.Hour, 0, 0, TimeSpan.Zero);
        if (first < open) first += Hour;
        for (var start = first; start < close; start += Hour) yield return start;
    }

    // A local exchange time on a date, in UTC.
    private static DateTimeOffset Utc(DateOnly date, TimeOnly time, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), zone), TimeSpan.Zero);

    /// <summary>
    /// The indicator values a decision reads: every indicator computed over the completed daily bars before the session plus
    /// the session's provisional bar, taking the last value. The final daily close of the session is never an input.
    /// </summary>
    public static IndicatorSnapshot ProvisionalSnapshot(IndicatorEngine engine, IReadOnlyList<OhlcvBar> priorCompletedDailyBars,
        OhlcvBar provisionalDailyBar)
    {
        if (priorCompletedDailyBars.Count > 0 && priorCompletedDailyBars[^1].Timestamp >= provisionalDailyBar.Timestamp)
            throw new ArgumentException("Prior daily bars must end before the provisional session.", nameof(priorCompletedDailyBars));
        return engine.Compute([.. priorCompletedDailyBars, provisionalDailyBar])[^1];
    }
}

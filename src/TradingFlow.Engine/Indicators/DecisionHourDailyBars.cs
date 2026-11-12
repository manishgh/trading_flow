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

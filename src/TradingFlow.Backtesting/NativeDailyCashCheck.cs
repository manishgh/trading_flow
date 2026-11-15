using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Backtesting;

/// <summary>Checks cash-only native books after actual fill accounting and before publishing economic results.</summary>
internal static class NativeDailyCashCheck
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // A close is known only after its scheduled close and the native daily replay receipt.
    // Missing marks do not change cash; missing executions prevent a complete-period minimum.
    internal static BacktestPortfolioState Apply(BacktestPortfolioState state, NativeDailyAccountingContext context)
    {
        var observed = new List<BacktestSessionObservation>();
        var openCount = 0;
        string? unavailable = null;
        foreach (var session in state.Sessions.OrderBy(item => item.SessionDate))
        {
            if (!context.Sessions.TryGetValue(session.SessionDate, out var schedule) || !schedule.IsTradingDay)
                throw new InvalidDataException($"Cash accounting lacks a trading session for {session.SessionDate:yyyy-MM-dd}.");
            if (At(session.SessionDate, schedule.RegularClose) > context.CutoffUtc)
            {
                openCount++;
                continue;
            }
            if (At(session.SessionDate.AddDays(1), TimeOnly.MinValue) > context.CutoffUtc)
            {
                unavailable ??= $"The native daily replay has not consumed the {session.SessionDate:yyyy-MM-dd} session receipt.";
                continue;
            }
            if (state.CashEvidenceUnavailableFromSession is { } boundary && session.SessionDate >= boundary)
            {
                unavailable ??= "Execution evidence is incomplete; a full-period cash minimum cannot be established.";
                continue;
            }
            if (session.Cash < 0m)
                throw new InvalidOperationException(
                    $"The cash-only daily book overdrew cash: New York session {session.SessionDate:yyyy-MM-dd} " +
                    $"ended with cash {session.Cash:G29} after actual fills and fees. The result is not published.");
            observed.Add(session);
        }
        if (observed.Count == 0)
            unavailable ??= "No completed session with replayed execution evidence is available.";
        var minimum = unavailable is null ? observed.MinBy(item => item.Cash) : null;
        return state with { CashSummary = new(minimum?.Cash, minimum?.SessionDate, observed.Count,
            openCount, unavailable is null, unavailable) };
    }

    // Exchange clocks use the date's New York DST offset, including early-close schedules supplied by the calendar.
    private static DateTimeOffset At(DateOnly date, TimeOnly time) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time), NewYork), TimeSpan.Zero);
}
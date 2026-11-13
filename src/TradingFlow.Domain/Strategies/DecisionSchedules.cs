namespace TradingFlow.Domain.Strategies;

/// <summary>
/// Named schedules that limit on which completed exchange sessions a rule is decided. A null schedule decides on every
/// session. The schedule is judged from the exchange calendar, which is published in advance, never from later price bars.
/// </summary>
public static class DecisionSchedules
{
    // Decide only at the close of the last trading session of each calendar month (New York dates).
    public const string MonthEnd = "month_end";

    // Reject an unknown schedule name so a typo cannot silently decide on every session.
    public static void Validate(string? schedule, string key)
    {
        if (schedule is not (null or MonthEnd))
            throw new InvalidOperationException($"{key} must be {MonthEnd} or absent; '{schedule}' is not a known schedule.");
    }

    /// <summary>
    /// Whether a rule with this schedule is decided at the close of <paramref name="session"/>, given the exchange's next
    /// trading session after it. The next session must come later; month_end holds when it falls in another month.
    /// </summary>
    public static bool IsDecisionSession(string? schedule, DateOnly session, DateOnly nextSession)
    {
        Validate(schedule, "decision schedule");
        if (nextSession <= session)
            throw new InvalidOperationException($"The next trading session {nextSession:yyyy-MM-dd} must follow {session:yyyy-MM-dd}.");
        return schedule is null || nextSession.Month != session.Month || nextSession.Year != session.Year;
    }
}

using TradingFlow.Domain.Earnings;

namespace TradingFlow.Earnings;

public enum EarningsEpsOutcome
{
    NotReported,
    ActualReported,
    Met,
    Beat,
    Miss
}

/// <summary>
/// Compares provider EPS actuals with their matching estimate. Direct numeric comparison is
/// intentional: a smaller-than-expected loss is a beat even when both values are negative.
/// </summary>
public static class EarningsEpsOutcomeClassifier
{
    public static EarningsEpsResult Classify(EarningsCalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        var estimate = calendarEvent.EpsEstimate ?? calendarEvent.ReportedEpsEstimate;
        var actual = calendarEvent.EpsActual ?? calendarEvent.ReportedEpsActual;
        var surprise = calendarEvent.EpsSurprisePercent ?? calendarEvent.ReportedEpsSurprisePercent;
        var outcome = actual is null
            ? EarningsEpsOutcome.NotReported
            : estimate is null
                ? EarningsEpsOutcome.ActualReported
                : actual > estimate
                    ? EarningsEpsOutcome.Beat
                    : actual < estimate
                        ? EarningsEpsOutcome.Miss
                        : EarningsEpsOutcome.Met;

        return new EarningsEpsResult(estimate, actual, surprise, outcome);
    }

    public static string Format(EarningsEpsOutcome outcome) => outcome switch
    {
        EarningsEpsOutcome.NotReported => "Pending",
        EarningsEpsOutcome.ActualReported => "Actual reported; no estimate",
        EarningsEpsOutcome.Met => "Met estimate",
        EarningsEpsOutcome.Beat => "EPS beat",
        EarningsEpsOutcome.Miss => "EPS miss",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };
}

public sealed record EarningsEpsResult(
    decimal? Estimate,
    decimal? Actual,
    decimal? SurprisePercent,
    EarningsEpsOutcome Outcome);

using TradingFlow.Domain.Earnings;

namespace TradingFlow.Earnings;

/// <summary>
/// When the monitor watches an earnings event closely. It loads 5-minute bars and analyses the event from
/// <see cref="LeadTime"/> before its scheduled slot, or from the moment its result is first seen if that comes
/// earlier (Sony on 5 Nov 2026 was out 13 hours before its Finviz slot), until <see cref="FollowTime"/> after the
/// result was first seen. It keeps watching past that while the card is incomplete: no assessment yet (no bar
/// after the release) or provider actuals the last analysis has not used. After that the event counts as
/// reported: its last analysis stays on the card, the Earnings page collapses it at the end of its day, and its
/// bars leave memory. Bar files on disk are never removed.
/// </summary>
public static class EarningsWatchWindow
{
    public static readonly TimeSpan LeadTime = TimeSpan.FromHours(1);
    public static readonly TimeSpan FollowTime = TimeSpan.FromHours(1);

    /// <summary>
    /// The earliest sign the result was out: the provider first showing it, or the result headline the last
    /// analysis matched. The headline counts only when that analysis found a result, so an announcement such as
    /// "Acme to report third-quarter results" cannot end the watch before the release.
    /// </summary>
    public static DateTimeOffset? FirstResultSign(EarningsCalendarEvent calendarEvent, EarningsAnalysisSnapshot? latest) =>
        new[]
        {
            calendarEvent.ResultFirstSeenAtUtc,
            latest is { ResultAssessment: not EarningsResultAssessment.Unknown } ? latest.ResultNewsPublishedAtUtc : null
        }.Min();

    /// <summary>True once <see cref="FollowTime"/> has passed since the result was first seen.</summary>
    public static bool IsReported(DateTimeOffset? firstResultSign, DateTimeOffset nowUtc) =>
        firstResultSign is { } seen && nowUtc - seen >= FollowTime;

    /// <summary>
    /// True while the event needs bars and analysis: no result yet and its slot starts within
    /// <see cref="LeadTime"/> or has passed; a result first seen less than <see cref="FollowTime"/> ago; or a
    /// reported event whose card is still incomplete.
    /// </summary>
    public static bool IsWatched(EarningsCalendarEvent calendarEvent, EarningsAnalysisSnapshot? latest, DateTimeOffset nowUtc)
    {
        var firstSign = FirstResultSign(calendarEvent, latest);
        if (firstSign is null)
        {
            return nowUtc >= calendarEvent.ScheduledAtUtc - LeadTime;
        }

        return !IsReported(firstSign, nowUtc) || IsIncomplete(calendarEvent, latest);
    }

    /// <summary>
    /// True when the card cannot be final yet: no analysis, an analysis without an assessment (a result first
    /// shown after 20:00 New York has no bar after it until the next session), or provider actuals that differ
    /// from the values the analysis used (Finviz often posts revenue after EPS).
    /// </summary>
    private static bool IsIncomplete(EarningsCalendarEvent calendarEvent, EarningsAnalysisSnapshot? latest) =>
        latest is null ||
        latest.BreakoutAssessment is EarningsBreakoutAssessment.InsufficientData or EarningsBreakoutAssessment.AwaitingRelease ||
        (calendarEvent.EpsActual ?? calendarEvent.ReportedEpsActual) is { } eps && eps != latest.EffectiveEpsActual ||
        calendarEvent.RevenueActualMillions is { } revenue && revenue != latest.EffectiveRevenueActualMillions;
}

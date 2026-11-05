using TradingFlow.Domain.Earnings;
using TradingFlow.Earnings;

namespace TradingFlow.Tests;

/// <summary>When the earnings monitor loads bars for an event and analyses it.</summary>
public sealed class EarningsWatchWindowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T22:00:00Z");

    /// <summary>
    /// An announcement headline before the release ("to report results") left the analysis without a result.
    /// It is not a sign that results are out, so it neither starts the follow hour nor ends the watch.
    /// </summary>
    [Fact]
    public void A_headline_without_a_result_is_not_a_result_sign()
    {
        var calendarEvent = Event("2026-11-05T21:30:00Z");
        var announcement = new EarningsAnalysisSnapshot
        {
            ResultAssessment = EarningsResultAssessment.Unknown,
            ResultNewsPublishedAtUtc = DateTimeOffset.Parse("2026-11-05T19:45:00Z"),
            BreakoutAssessment = EarningsBreakoutAssessment.AwaitingRelease
        };

        Assert.Null(EarningsWatchWindow.FirstResultSign(calendarEvent, announcement));
        Assert.True(EarningsWatchWindow.IsWatched(calendarEvent, announcement, Now));
    }

    /// <summary>
    /// Past the follow hour the watch continues while the card is incomplete: no assessment yet, or a provider
    /// revenue figure the analysis has not used. A complete card stops being watched.
    /// </summary>
    [Theory]
    [InlineData(EarningsBreakoutAssessment.InsufficientData, 4869, true)]
    [InlineData(EarningsBreakoutAssessment.Possible, 4900, true)]
    [InlineData(EarningsBreakoutAssessment.Possible, 4869, false)]
    public void A_reported_event_is_watched_until_its_card_is_complete(
        EarningsBreakoutAssessment breakout, int providerRevenue, bool expectedWatched)
    {
        var calendarEvent = Event("2026-11-05T21:30:00Z");
        calendarEvent.ResultFirstSeenAtUtc = DateTimeOffset.Parse("2026-11-05T20:30:00Z");
        calendarEvent.EpsActual = 3.16m;
        calendarEvent.RevenueActualMillions = providerRevenue;
        var analysis = new EarningsAnalysisSnapshot
        {
            ResultAssessment = EarningsResultAssessment.Positive,
            BreakoutAssessment = breakout,
            EffectiveEpsActual = 3.16m,
            EffectiveRevenueActualMillions = 4869m
        };

        Assert.True(EarningsWatchWindow.IsReported(EarningsWatchWindow.FirstResultSign(calendarEvent, analysis), Now));
        Assert.Equal(expectedWatched, EarningsWatchWindow.IsWatched(calendarEvent, analysis, Now));
    }

    /// <summary>With no result yet, watching starts one hour before the slot.</summary>
    [Theory]
    [InlineData("2026-11-05T22:59:00Z", true)]
    [InlineData("2026-11-05T23:01:00Z", false)]
    public void Watching_starts_an_hour_before_the_slot(string scheduledUtc, bool expectedWatched)
    {
        Assert.Equal(expectedWatched, EarningsWatchWindow.IsWatched(Event(scheduledUtc), null, Now));
    }

    private static EarningsCalendarEvent Event(string scheduledUtc) => new()
    {
        Id = "finviz:test", Ticker = "TEST", ReportDateExchange = new DateOnly(2026, 11, 5),
        ScheduledAtUtc = DateTimeOffset.Parse(scheduledUtc), ReleaseWindow = EarningsReleaseWindow.AfterMarketClose
    };
}

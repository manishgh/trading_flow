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

    /// <summary>
    /// Headlines that say results were published count, with or without figures (the ICCM and KNDI releases of
    /// 18 Nov 2026 carried none); announcements of a later release, its date or its call do not.
    /// </summary>
    [Theory]
    [InlineData("Kandi Technologies Reports Unaudited Financial Results for the Nine Months Ended September 30, 2026", true)]
    [InlineData("IceCure Reports Financial & Operational Results for the Nine Months Ended September 30, 2026: 83% Year-Over-Year Growth in U.S. Sales", true)]
    [InlineData("Acme Announces Third Quarter 2026 Results", true)]
    [InlineData("Acme Q3 EPS $1.10 Beats $1.02 Estimate", true)]
    [InlineData("Acme to Report Third Quarter 2026 Results on November 20", false)]
    [InlineData("Acme Announces Date of Third Quarter 2026 Earnings Release", false)]
    [InlineData("Acme Schedules Third Quarter 2026 Earnings Conference Call", false)]
    [InlineData("Acme will report results after the close", false)]
    [InlineData("Acme shares rise ahead of earnings", false)]
    [InlineData("Acme raises guidance", false)]
    [InlineData("Acme Reports Next Week: Wall Street Expects Earnings Growth", false)]
    [InlineData("Acme Announces Q3 Earnings Release on November 20", false)]
    [InlineData("Acme Announces Fourth Quarter Earnings Release Timing", false)]
    [InlineData("Acme Reports Results Tomorrow", false)]
    [InlineData("Acme Will Release Q3 Results on Thursday", false)]
    [InlineData("Acme Reports Q3 Earnings After the Bell Today", false)]
    [InlineData("Acme Delays Release of Third Quarter Results", false)]
    [InlineData("Acme Postpones Third Quarter Earnings Release", false)]
    [InlineData("Acme Receives Nasdaq Notice Regarding Late Filing of Quarterly Results", false)]
    [InlineData("Acme Announces Results of Annual Meeting of Shareholders", false)]
    [InlineData("Acme Announces Positive Topline Results from Phase 2 Trial", false)]
    [InlineData("Acme Announces Final Results of Tender Offer", false)]
    [InlineData("Acme Posts Q3 Earnings Call Transcript", false)]
    [InlineData("Acme Reports Record Third Quarter Results", true)]
    public void Results_release_headlines_are_recognised(string headline, bool expected)
    {
        Assert.Equal(expected, EarningsNewsResultExtractor.IsResultsRelease(headline));
    }

    /// <summary>
    /// A matched release headline without figures is the first result sign: the card counts as reported an hour
    /// later and reads "figures not yet published" until Finviz posts actuals.
    /// </summary>
    [Fact]
    public void A_release_headline_without_figures_is_a_result_sign()
    {
        // After-close slot: its release window opens at 16:00 New York (21:00 UTC) on the report day.
        var calendarEvent = Event("2026-11-05T21:30:00Z");
        var release = new EarningsAnalysisSnapshot
        {
            ResultAssessment = EarningsResultAssessment.Unknown,
            BreakoutAssessment = EarningsBreakoutAssessment.InsufficientData,
            ResultNewsPublishedAtUtc = DateTimeOffset.Parse("2026-11-05T21:00:00Z"),
            NewsHeadline = "Test Corp Reports Unaudited Financial Results for the Nine Months Ended September 30, 2026"
        };

        Assert.Equal(release.ResultNewsPublishedAtUtc, EarningsWatchWindow.FirstResultSign(calendarEvent, release));
        Assert.True(EarningsWatchWindow.IsReported(EarningsWatchWindow.FirstResultSign(calendarEvent, release), Now));
        Assert.True(EarningsWatchWindow.ReleasedWithoutFigures(calendarEvent, release));

        calendarEvent.EpsActual = -0.12m;
        Assert.False(EarningsWatchWindow.ReleasedWithoutFigures(calendarEvent, release));
    }

    /// <summary>
    /// A figure-less release headline published before the slot's release window opens (here 16:00 New York on
    /// the report day for an after-close slot) is not trusted as the release; a structured result headline is.
    /// </summary>
    [Fact]
    public void A_figure_less_release_headline_before_the_release_window_is_not_a_result_sign()
    {
        var calendarEvent = Event("2026-11-05T21:30:00Z");
        const string release = "Test Corp Reports Third Quarter 2026 Results";
        Assert.False(EarningsWatchWindow.HeadlineShowsRelease(calendarEvent, release, DateTimeOffset.Parse("2026-11-05T20:30:00Z")));
        Assert.True(EarningsWatchWindow.HeadlineShowsRelease(calendarEvent, release, DateTimeOffset.Parse("2026-11-05T21:05:00Z")));
        Assert.True(EarningsWatchWindow.HeadlineShowsRelease(calendarEvent, "Test Q3 EPS $1.10 Beats $1.02 Estimate", DateTimeOffset.Parse("2026-11-05T20:30:00Z")));
    }

    /// <summary>An announcement headline stays a non-sign even when the analysis saved it.</summary>
    [Fact]
    public void A_saved_announcement_headline_is_not_a_result_sign()
    {
        var calendarEvent = Event("2026-11-05T21:30:00Z");
        var announcement = new EarningsAnalysisSnapshot
        {
            ResultAssessment = EarningsResultAssessment.Unknown,
            ResultNewsPublishedAtUtc = DateTimeOffset.Parse("2026-11-05T19:45:00Z"),
            NewsHeadline = "Test Corp to Report Third Quarter Results After the Close",
            BreakoutAssessment = EarningsBreakoutAssessment.AwaitingRelease
        };

        Assert.Null(EarningsWatchWindow.FirstResultSign(calendarEvent, announcement));
        Assert.False(EarningsWatchWindow.ReleasedWithoutFigures(calendarEvent, announcement));
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

using TradingFlow.Domain.Earnings;
using TradingFlow.Earnings;
using TradingFlow.Web;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services.Desk;

namespace TradingFlow.Tests;

public sealed class EarningsExchangeDateTests
{
    [Theory]
    [InlineData("2026-10-18T22:10:00Z", "2026-10-18", "2026-10-19")]
    [InlineData("2026-10-19T00:30:00Z", "2026-10-18", "2026-10-19")]
    [InlineData("2026-10-19T03:59:00Z", "2026-10-18", "2026-10-19")]
    [InlineData("2026-10-19T04:01:00Z", "2026-10-19", "2026-10-20")]
    [InlineData("2026-03-08T06:59:00Z", "2026-03-08", "2026-03-09")]
    [InlineData("2026-03-08T07:01:00Z", "2026-03-08", "2026-03-09")]
    [InlineData("2026-11-01T05:59:00Z", "2026-11-01", "2026-11-02")]
    [InlineData("2026-11-01T06:01:00Z", "2026-11-01", "2026-11-02")]
    public void ResponseUsesNewYorkRequestDateAcrossMidnightAndDst(
        string instant, string today, string next)
    {
        var now = DateTimeOffset.Parse(instant);
        Assert.Equal(DateOnly.Parse(today), EarningsCalendarDates.ExchangeDate(now));
        var response = Map(now, [], true, "nextBusinessDay");
        Assert.Equal(DateOnly.Parse(next), response.NextBusinessDate);
    }

    [Fact]
    public void SundayInNewYorkStillIncludesMondayAfterBerlinMidnight()
    {
        var monday = Event("MON", new(2026, 10, 19), "2026-10-19T12:30:00Z");
        var tuesday = Event("TUE", new(2026, 10, 20), "2026-10-20T12:30:00Z");
        var response = Map(DateTimeOffset.Parse("2026-10-18T22:10:00Z"), [monday, tuesday], true, "nextBusinessDay");
        Assert.Equal("MON", Assert.Single(response.Items).Ticker);
        Assert.Equal("nextBusinessDay", response.Items[0].DayGroup);
    }

    [Fact]
    public void LateReleaseUsesReportExchangeDateRatherThanBerlinOrUtcDate()
    {
        var lateMonday = Event("LATE", new(2026, 10, 19), "2026-10-20T00:30:00Z");
        var response = Map(DateTimeOffset.Parse("2026-10-19T22:10:00Z"), [lateMonday], true, "today");
        Assert.Equal("LATE", Assert.Single(response.Items).Ticker);
        Assert.Equal("today", response.Items[0].DayGroup);
        Assert.StartsWith("2026-10-19", response.Items[0].ScheduledNewYorkText);
    }

    [Fact]
    public void PreviousDaysAfterCloseReportStaysOnItsOwnDayOnTheEarningsPage()
    {
        // Monday 2026-10-19 in New York: Friday's after-close report trades today, but the Earnings page lists it
        // under Friday, never under Today. The Desk's earnings source keeps the trading-session rule (below).
        var fridayEvening = Event("PM", new(2026, 10, 16), "2026-10-16T20:30:00Z");
        fridayEvening.ReleaseWindow = EarningsReleaseWindow.AfterMarketClose;
        var mondayMorning = Event("TODAY", new(2026, 10, 19), "2026-10-19T12:30:00Z");
        var now = DateTimeOffset.Parse("2026-10-19T13:00:00Z");

        Assert.Equal("TODAY", Assert.Single(Map(now, [fridayEvening, mondayMorning], true, "today").Items).Ticker);
        var custom = Map(now, [fridayEvening, mondayMorning], false, "custom");
        Assert.Equal("other", custom.Items.Single(x => x.Ticker == "PM").DayGroup);
        Assert.Equal("today", custom.Items.Single(x => x.Ticker == "TODAY").DayGroup);
        Assert.True(EarningsCalendarDates.IsTradingSession(new(2026, 10, 16), EarningsReleaseWindow.AfterMarketClose, new(2026, 10, 19)));
        Assert.False(EarningsCalendarDates.IsTradingSession(new(2026, 10, 15), EarningsReleaseWindow.AfterMarketClose, new(2026, 10, 19)));
    }

    [Fact]
    public void CustomWindowKeepsOtherDatesButUsesTheSameExchangeDayGroups()
    {
        var rows = new[] { Event("TODAY", new(2026, 10, 18), "2026-10-18T12:30:00Z"),
            Event("NEXT", new(2026, 10, 19), "2026-10-19T12:30:00Z"),
            Event("OTHER", new(2026, 10, 20), "2026-10-20T12:30:00Z") };
        var response = Map(DateTimeOffset.Parse("2026-10-18T22:10:00Z"), rows, false, "custom");
        Assert.Equal(3, response.Items.Count);
        Assert.Equal("today", response.Items.Single(x => x.Ticker == "TODAY").DayGroup);
        Assert.Equal("nextBusinessDay", response.Items.Single(x => x.Ticker == "NEXT").DayGroup);
        Assert.Equal("other", response.Items.Single(x => x.Ticker == "OTHER").DayGroup);
    }

    /// <summary>
    /// An event more than an hour from its slot has no analysis yet. It reads as awaiting release, not as
    /// insufficient data, and is not reported.
    /// </summary>
    [Fact]
    public void AnUpcomingEventWithoutAnAnalysisIsAwaitingRelease()
    {
        var response = Map(DateTimeOffset.Parse("2026-11-05T15:00:00Z"),
            [Event("LATER", new(2026, 11, 5), "2026-11-05T21:30:00Z")], false, "custom");

        var item = Assert.Single(response.Items);
        Assert.Equal(EarningsAssessmentLabel.Format(EarningsResultAssessment.Unknown, EarningsBreakoutAssessment.AwaitingRelease), item.AssessmentLabel);
        Assert.StartsWith("Analysis starts an hour before", item.AnalysisReason, StringComparison.Ordinal);
        Assert.False(item.IsReported);
    }

    private static EarningsCalendarResponse Map(DateTimeOffset now, EarningsCalendarEvent[] rows, bool shortHorizon, string scope) =>
        EarningsApiEndpoints.MapSnapshot(
            new(now, new(2026, 1, 1), new(2026, 12, 31), rows.Select(x => new EarningsCalendarSnapshotItem(x, null, null)).ToArray()),
            new(true, TimeSpan.FromMinutes(1), now, now, rows.Length, null), now, [], shortHorizon, EarningsCalendarFilter.All, scope, _ => null);

    private static EarningsCalendarEvent Event(string ticker, DateOnly reportDate, string scheduled) => new()
    {
        Id = ticker, Ticker = ticker, CompanyName = ticker, ReportDateExchange = reportDate,
        ScheduledAtUtc = DateTimeOffset.Parse(scheduled), Provider = "fixture", SourceUrl = "https://example.invalid/calendar",
        SourceArtifactSha256 = new('a', 64), ProviderReceivedAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        FirstSeenAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), LastSeenAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
    };
}

public sealed class DeskEarningsSourceTests
{
    private static EarningsCalendarSnapshotItem Item(string ticker, DateOnly date, EarningsReleaseWindow window, EarningsResultAssessment? result) =>
        new(new EarningsCalendarEvent { Id = ticker, Ticker = ticker, CompanyName = ticker + " Inc", ReportDateExchange = date, ReleaseWindow = window,
            ScheduledAtUtc = new DateTimeOffset(date.ToDateTime(new TimeOnly(window == EarningsReleaseWindow.AfterMarketClose ? 21 : 12, 0)), TimeSpan.Zero),
            Provider = "fixture", SourceUrl = "https://example.invalid", SourceArtifactSha256 = new('a', 64) },
            result is null ? null : new EarningsAnalysisSnapshot { ResultAssessment = result.Value }, null);

    [Fact]
    public void TodayGroupFollowsTheTradingSessionAndCarriesReleaseAndResult()
    {
        // Monday 2026-10-19: Friday's after-close report trades today; Friday's morning report does not.
        var monday = new DateOnly(2026, 10, 19);
        var items = new[]
        {
            Item("PM", new(2026, 10, 16), EarningsReleaseWindow.AfterMarketClose, EarningsResultAssessment.Positive),
            Item("AM", new(2026, 10, 16), EarningsReleaseWindow.BeforeMarketOpen, EarningsResultAssessment.Positive),
            Item("TODAY", monday, EarningsReleaseWindow.BeforeMarketOpen, EarningsResultAssessment.Negative),
            Item("NEXT", new(2026, 10, 20), EarningsReleaseWindow.BeforeMarketOpen, null)
        };
        var followed = new[] { new DeskMembership("PM", null, [new DeskWishlist(Guid.Empty, "Core", true)]) };

        var today = DeskSources.EarningsMembers(items, "earnings:today", monday, followed);
        Assert.Equal(["PM", "TODAY"], today.Select(member => member.Symbol));
        Assert.Equal("PM Inc · after close 16 Oct · positive", today[0].Name);
        Assert.Equal("Core", Assert.Single(today[0].Wishlists).Name);

        Assert.Equal(["PM"], DeskSources.EarningsMembers(items, "earnings:today-positive", monday, followed).Select(member => member.Symbol));
        Assert.Equal(["NEXT"], DeskSources.EarningsMembers(items, "earnings:next", monday, followed).Select(member => member.Symbol));
    }
}

using TradingFlow.Domain.Earnings;
using TradingFlow.Earnings;
using TradingFlow.Web;
using TradingFlow.Web.Models;

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

    private static EarningsCalendarResponse Map(DateTimeOffset now, EarningsCalendarEvent[] rows, bool shortHorizon, string scope) =>
        EarningsApiEndpoints.MapSnapshot(
            new(now, new(2026, 1, 1), new(2026, 12, 31), rows.Select(x => new EarningsCalendarSnapshotItem(x, null, null)).ToArray()),
            new(true, TimeSpan.FromMinutes(1), now, now, rows.Length, null), now, [], shortHorizon, EarningsCalendarFilter.All, scope);

    private static EarningsCalendarEvent Event(string ticker, DateOnly reportDate, string scheduled) => new()
    {
        Id = ticker, Ticker = ticker, CompanyName = ticker, ReportDateExchange = reportDate,
        ScheduledAtUtc = DateTimeOffset.Parse(scheduled), Provider = "fixture", SourceUrl = "https://example.invalid/calendar",
        SourceArtifactSha256 = new('a', 64), ProviderReceivedAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        FirstSeenAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), LastSeenAtUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
    };
}

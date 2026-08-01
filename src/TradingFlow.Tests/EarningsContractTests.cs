using TradingFlow.Domain.Earnings;
using TradingFlow.Earnings;

namespace TradingFlow.Tests;

public sealed class EarningsContractTests
{
    [Theory]
    [InlineData(-0.10, -0.20, EarningsEpsOutcome.Beat)]
    [InlineData(-0.30, -0.20, EarningsEpsOutcome.Miss)]
    [InlineData(1.25, 1.25, EarningsEpsOutcome.Met)]
    [InlineData(1.30, 1.25, EarningsEpsOutcome.Beat)]
    [InlineData(1.20, 1.25, EarningsEpsOutcome.Miss)]
    public void EpsOutcome_UsesDirectActualVersusEstimateComparison(
        decimal actual,
        decimal estimate,
        EarningsEpsOutcome expected)
    {
        var result = EarningsEpsOutcomeClassifier.Classify(CreateEvent(estimate, actual, 4m));

        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public void EpsOutcome_FallsBackToReportedEpsFields()
    {
        var calendarEvent = CreateEvent(null, null, null);
        calendarEvent.ReportedEpsEstimate = 0.20m;
        calendarEvent.ReportedEpsActual = 0.25m;
        calendarEvent.ReportedEpsSurprisePercent = 25m;

        var result = EarningsEpsOutcomeClassifier.Classify(calendarEvent);

        Assert.Equal(EarningsEpsOutcome.Beat, result.Outcome);
        Assert.Equal(0.20m, result.Estimate);
        Assert.Equal(0.25m, result.Actual);
        Assert.Equal(25m, result.SurprisePercent);
    }

    [Theory]
    [InlineData(null, "all", true)]
    [InlineData("9999.99", "under10b", true)]
    [InlineData("10000", "under10b", false)]
    [InlineData("10000", "10bTo50b", true)]
    [InlineData("50000", "10bTo50b", false)]
    [InlineData("50000", "50bTo100b", true)]
    [InlineData("100000", "50bTo100b", false)]
    [InlineData("100000", "100bPlus", true)]
    [InlineData(null, "unknown", true)]
    public void MarketCapBands_HaveNonOverlappingBoundaries(
        string? marketCapMillions,
        string marketCap,
        bool expected)
    {
        Assert.True(EarningsCalendarFilter.TryParse("all", marketCap, out var filter, out _));

        var value = marketCapMillions is null
            ? (decimal?)null
            : Decimal.Parse(marketCapMillions, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, EarningsCalendarFilter.MatchesMarketCap(value, filter.MarketCapBand));
    }

    [Fact]
    public void Filter_MatchesReleaseWindowAndMarketCapTogether()
    {
        Assert.True(EarningsCalendarFilter.TryParse(
            "afterMarketClose",
            "10bTo50b",
            out var filter,
            out var error));
        var calendarEvent = CreateEvent(1m, null, null);
        calendarEvent.ReleaseWindow = EarningsReleaseWindow.AfterMarketClose;
        calendarEvent.MarketCapMillions = 25_000m;

        Assert.Null(error);
        Assert.True(filter.Matches(calendarEvent));
        calendarEvent.ReleaseWindow = EarningsReleaseWindow.BeforeMarketOpen;
        Assert.False(filter.Matches(calendarEvent));
    }

    [Theory]
    [InlineData("lunch", "all")]
    [InlineData("all", "huge")]
    public void Filter_RejectsUnknownApiValues(string session, string marketCap)
    {
        Assert.False(EarningsCalendarFilter.TryParse(session, marketCap, out _, out var error));
        Assert.False(String.IsNullOrWhiteSpace(error));
    }

    private static EarningsCalendarEvent CreateEvent(
        decimal? estimate,
        decimal? actual,
        decimal? surprise) => new()
        {
            Id = "finviz:test",
            Ticker = "TEST",
            CompanyName = "Test Corp",
            ReportDateExchange = new DateOnly(2026, 8, 1),
            ScheduledAtUtc = DateTimeOffset.Parse("2026-08-01T20:00:00Z"),
            ReleaseWindow = EarningsReleaseWindow.AfterMarketClose,
            EpsEstimate = estimate,
            EpsActual = actual,
            EpsSurprisePercent = surprise,
            Provider = "finviz",
            SourceUrl = "https://finviz.com/calendar/earnings",
            SourceArtifactSha256 = new string('a', 64),
            ProviderReceivedAtUtc = DateTimeOffset.Parse("2026-08-01T20:01:00Z"),
            FirstSeenAtUtc = DateTimeOffset.Parse("2026-08-01T20:01:00Z"),
            LastSeenAtUtc = DateTimeOffset.Parse("2026-08-01T20:01:00Z")
        };
}

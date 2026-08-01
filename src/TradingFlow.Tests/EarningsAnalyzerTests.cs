using TradingFlow.Domain.Earnings;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Earnings;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Tests;

public sealed class EarningsAnalyzerTests
{
    [Theory]
    [InlineData("2026-07-31", "2026-08-03")]
    [InlineData("2026-08-01", "2026-08-03")]
    [InlineData("2026-08-02", "2026-08-03")]
    [InlineData("2026-08-03", "2026-08-04")]
    public void NextWeekday_SkipsWeekendDates(string dateText, string expectedText)
    {
        var result = EarningsCalendarDates.NextWeekday(DateOnly.Parse(dateText));

        Assert.Equal(DateOnly.Parse(expectedText), result);
    }

    [Theory]
    [InlineData("2026-07-31T12:00:00Z", "Premarket")]
    [InlineData("2026-07-31T14:00:00Z", "Regular market")]
    [InlineData("2026-07-31T21:00:00Z", "Post-market")]
    [InlineData("2026-07-31T01:00:00Z", "Overnight")]
    public void UsEquitySession_UsesNewYorkBoundaries(string utcText, string expected)
    {
        Assert.Equal(
            expected,
            EarningsTimeZones.ClassifyUsEquitySession(DateTimeOffset.Parse(utcText)));
    }

    [Fact]
    public void NewYorkClientId_IsBrowserCompatibleIanaTimezone()
    {
        Assert.Equal("America/New_York", EarningsTimeZones.NewYorkClientId);
    }

    [Theory]
    [InlineData("2026-03-20T12:30:00Z", "2026-03-20T13:30:00+01:00")]
    [InlineData("2026-07-31T12:30:00Z", "2026-07-31T14:30:00+02:00")]
    public void OperatorLocalDisplayTime_UsesDateSpecificDaylightSavingRules(string utcText, string expectedText)
    {
        var utc = DateTimeOffset.Parse(utcText);

        var local = TimeZoneInfo.ConvertTime(utc, ResolveAmsterdamForTest());

        Assert.Equal(DateTimeOffset.Parse(expectedText), local);
    }

    [Theory]
    [InlineData("2026-07-31T20:00:00Z", "2026-07-31T20:00:00Z", "Friday 22:00 to Monday 13:00 local time")]
    [InlineData("2026-08-01T08:00:00Z", "2026-07-31T20:00:00Z", "Friday 22:00 to Monday 13:00 local time")]
    [InlineData("2026-08-03T11:00:00Z", "2026-07-31T20:00:00Z", "Friday 22:00 to Monday 13:00 local time")]
    [InlineData("2026-08-03T12:00:00Z", "2026-08-02T12:00:00Z", "Latest 24 hours")]
    public void EarningsNewsWindow_UsesOperatorLocalWeekendBoundary(
        string nowText,
        string expectedStartText,
        string expectedLabel)
    {
        var window = EarningsNewsWindowPolicy.Resolve(
            DateTimeOffset.Parse(nowText),
            ResolveAmsterdamForTest());

        Assert.Equal(DateTimeOffset.Parse(expectedStartText), window.StartUtc);
        Assert.Equal(TimeSpan.Zero, window.StartUtc.Offset);
        Assert.Equal(TimeSpan.Zero, window.EndUtc.Offset);
        Assert.Equal(expectedLabel, window.Label);
    }

    private static TimeZoneInfo ResolveAmsterdamForTest()
    {
        foreach (var id in new[] { "Europe/Amsterdam", "W. Europe Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new InvalidOperationException("The test requires an Amsterdam-compatible timezone.");
    }

    [Fact]
    public void Analyze_FlagsPositiveBreakoutFromCompletedBarsOnly()
    {
        var options = EarningsMonitorOptions.Default with
        {
            MinimumSlotRelativeVolume = 1.5m,
            ReferenceBarCount = 40
        };
        var analyzer = new EarningsAnalyzer(new IndicatorEngine(), options);
        var eventTime = DateTimeOffset.Parse("2026-07-31T12:30:00Z");
        var now = DateTimeOffset.Parse("2026-07-31T14:35:00Z");
        var calendarEvent = CreateEvent(eventTime, epsSurprise: 8m, revenueSurprise: 4m);
        var bars = CreateBars(eventTime);
        bars.Add(new OhlcvBar("TEST", now.AddMinutes(1), "5m", 999m, 1000m, 998m, 999m, 10000m));
        var news = new PersistedNewsItem
        {
            Id = "news-1",
            Ticker = "TEST",
            Timestamp = eventTime,
            Headline = "TEST reports earnings and raises guidance",
            SentimentScore = 0.8m,
            Provider = "alpaca",
            Url = "https://example.com/result"
        };

        var result = analyzer.Analyze(calendarEvent, bars, [news], now);

        Assert.Equal(EarningsResultAssessment.Positive, result.ResultAssessment);
        Assert.Equal(EarningsBreakoutAssessment.Possible, result.BreakoutAssessment);
        Assert.NotEqual(999m, result.LatestClose);
        Assert.True(result.Ema10 > result.Ema20);
        Assert.True(result.MacdHistogram > 0m);
        Assert.True(result.SlotRelativeVolume >= 1.5m);
        Assert.Equal(eventTime, result.ResultNewsPublishedAtUtc);
    }

    [Fact]
    public void Analyze_DoesNotConfirmVolumeUntilSameSlotBaselineIsFullyWarm()
    {
        var analyzer = new EarningsAnalyzer(new IndicatorEngine(), EarningsMonitorOptions.Default);
        var eventTime = DateTimeOffset.Parse("2026-07-31T12:30:00Z");
        var bars = CreateBars(eventTime, historyDays: 20);

        var result = analyzer.Analyze(
            CreateEvent(eventTime, epsSurprise: 8m, revenueSurprise: 4m),
            bars,
            [new PersistedNewsItem
            {
                Id = "baseline-result",
                Ticker = "TEST",
                Timestamp = eventTime,
                Headline = "TEST reports earnings results",
                Provider = "alpaca"
            }],
            DateTimeOffset.Parse("2026-07-31T14:35:00Z"));

        Assert.Equal(EarningsBreakoutAssessment.NotConfirmed, result.BreakoutAssessment);
        Assert.Contains("required prior sessions", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_DoesNotCallMixedEarningsPositive()
    {
        var analyzer = new EarningsAnalyzer(new IndicatorEngine(), EarningsMonitorOptions.Default);
        var eventTime = DateTimeOffset.Parse("2026-07-31T12:30:00Z");
        var result = analyzer.Analyze(
            CreateEvent(eventTime, epsSurprise: 8m, revenueSurprise: -4m),
            CreateBars(eventTime),
            [],
            DateTimeOffset.Parse("2026-07-31T14:35:00Z"));

        Assert.Equal(EarningsResultAssessment.Mixed, result.ResultAssessment);
        Assert.NotEqual(EarningsBreakoutAssessment.Possible, result.BreakoutAssessment);
    }

    [Fact]
    public void Analyze_UsesReportedEpsSurpriseWhenAdjustedSurpriseIsUnavailable()
    {
        var analyzer = new EarningsAnalyzer(new IndicatorEngine(), EarningsMonitorOptions.Default);
        var eventTime = DateTimeOffset.Parse("2026-07-31T12:30:00Z");
        var calendarEvent = CreateEvent(eventTime, epsSurprise: null, revenueSurprise: 4m);
        calendarEvent.ReportedEpsSurprisePercent = 6m;

        var result = analyzer.Analyze(
            calendarEvent,
            CreateBars(eventTime),
            [],
            DateTimeOffset.Parse("2026-07-31T14:35:00Z"));

        Assert.Equal(EarningsResultAssessment.Positive, result.ResultAssessment);
    }

    [Theory]
    [InlineData(EarningsResultAssessment.Positive, EarningsBreakoutAssessment.NotConfirmed, "Positive earnings · breakout not confirmed")]
    [InlineData(EarningsResultAssessment.Positive, EarningsBreakoutAssessment.Possible, "Positive earnings · possible breakout")]
    [InlineData(EarningsResultAssessment.Unknown, EarningsBreakoutAssessment.AwaitingRelease, "Earnings pending · awaiting release")]
    public void AssessmentLabel_StatesBothEarningsAndBreakoutMeaning(
        EarningsResultAssessment result,
        EarningsBreakoutAssessment breakout,
        string expected)
    {
        Assert.Equal(expected, EarningsAssessmentLabel.Format(result, breakout));
    }

    [Fact]
    public void Analyze_IgnoresPreviewArticleAndUsesFirstActualResultNews()
    {
        var analyzer = new EarningsAnalyzer(new IndicatorEngine(), EarningsMonitorOptions.Default);
        var eventTime = DateTimeOffset.Parse("2026-07-31T12:30:00Z");
        var previewTime = eventTime.AddHours(-1);
        var resultTime = eventTime.AddMinutes(5);
        var news = new[]
        {
            new PersistedNewsItem
            {
                Id = "preview",
                Ticker = "TEST",
                Timestamp = previewTime,
                Headline = "TEST earnings preview: what to expect",
                Provider = "alpaca"
            },
            new PersistedNewsItem
            {
                Id = "result",
                Ticker = "TEST",
                Timestamp = resultTime,
                Headline = "TEST reports quarterly results and raises guidance",
                Provider = "alpaca"
            }
        };

        var result = analyzer.Analyze(
            CreateEvent(eventTime, epsSurprise: 8m, revenueSurprise: 4m),
            CreateBars(eventTime),
            news,
            DateTimeOffset.Parse("2026-07-31T14:35:00Z"));

        Assert.Equal(resultTime, result.ResultNewsPublishedAtUtc);
        Assert.Equal("TEST reports quarterly results and raises guidance", result.NewsHeadline);
    }

    [Fact]
    public void Analyze_AwaitingReleaseIncludesLatestCompletedPreEarningsPrice()
    {
        var analyzer = new EarningsAnalyzer(new IndicatorEngine(), EarningsMonitorOptions.Default);
        var eventTime = DateTimeOffset.Parse("2026-08-01T20:00:00Z");
        var now = eventTime.AddHours(-2);
        var bars = CreateBars(eventTime)
            .Where(bar => bar.Timestamp.AddMinutes(5) <= now)
            .ToArray();

        var result = analyzer.Analyze(
            CreateEvent(eventTime, epsSurprise: null, revenueSurprise: null),
            bars,
            [],
            now);

        Assert.Equal(EarningsBreakoutAssessment.AwaitingRelease, result.BreakoutAssessment);
        Assert.Equal(bars[^1].Close, result.PreReleaseReferenceClose);
        Assert.Equal(bars[^1].Close, result.LatestClose);
        Assert.Equal(bars[^1].Timestamp, result.LatestCompletedBarAtUtc);
    }

    [Theory]
    [InlineData("2026-07-31T12:30:00Z", "2026-07-31T13:20:00Z", "Premarket")]
    [InlineData("2026-07-31T20:05:00Z", "2026-07-31T21:05:00Z", "Post-market")]
    public void Analyze_RetainsCompletedExtendedHoursBars(
        string eventTimeText,
        string nowText,
        string expectedSession)
    {
        var analyzer = new EarningsAnalyzer(new IndicatorEngine(), EarningsMonitorOptions.Default);
        var eventTime = DateTimeOffset.Parse(eventTimeText);
        var now = DateTimeOffset.Parse(nowText);

        var result = analyzer.Analyze(
            CreateEvent(eventTime, epsSurprise: 8m, revenueSurprise: 4m),
            CreateBars(eventTime),
            [],
            now);

        Assert.NotNull(result.LatestCompletedBarAtUtc);
        Assert.Equal(expectedSession, EarningsTimeZones.ClassifyUsEquitySession(result.LatestCompletedBarAtUtc!.Value));
        Assert.Equal(
            CreateBars(eventTime).Last(bar => bar.Timestamp.AddMinutes(5) <= now).Close,
            result.LatestClose);
    }

    private static EarningsCalendarEvent CreateEvent(
        DateTimeOffset eventTime,
        decimal? epsSurprise,
        decimal? revenueSurprise) => new()
    {
        Id = "finviz:test",
        Ticker = "TEST",
        CompanyName = "Test Corp",
        ReportDateExchange = new DateOnly(2026, 7, 31),
        ScheduledAtUtc = eventTime,
        ReleaseWindow = EarningsReleaseWindow.BeforeMarketOpen,
        EpsSurprisePercent = epsSurprise,
        RevenueSurprisePercent = revenueSurprise,
        Provider = "finviz"
    };

    private static List<OhlcvBar> CreateBars(DateTimeOffset eventTime, int historyDays = 70)
    {
        var bars = new List<OhlcvBar>();
        for (var day = historyDays - 1; day >= 0; day--)
        {
            var sessionStart = eventTime.AddDays(-day).AddHours(-2);
            for (var index = 0; index < 50; index++)
            {
                var timestamp = sessionStart.AddMinutes(index * 5);
                var postRelease = day == 0 && timestamp >= eventTime;
                var postReleaseIndex = Math.Max(0, index - 24);
                var price = postRelease
                    ? 112m + postReleaseIndex * postReleaseIndex * 0.04m
                    : 98m + index * 0.01m;
                var volume = postRelease ? 400m : 100m;
                bars.Add(new OhlcvBar("TEST", timestamp, "5m", price - 0.1m, price + 0.2m, price - 0.2m, price, volume));
            }
        }

        return bars;
    }
}

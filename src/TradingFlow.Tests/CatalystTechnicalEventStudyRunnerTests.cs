using TradingFlow.Backtesting.Research;
using TradingFlow.Domain.Market;

namespace TradingFlow.Tests;

public sealed class CatalystTechnicalEventStudyRunnerTests
{
    [Fact]
    public void Deduplicate_RemovesSameHeadlineInsideWindow()
    {
        var deduper = new CatalystDeduper();
        var start = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
        var catalysts = new[]
        {
            Catalyst("POET", start, "POET wins new AI optical contract"),
            Catalyst("POET", start.AddHours(1), "POET wins new AI optical contract"),
            Catalyst("POET", start.AddHours(7), "POET wins new AI optical contract")
        };

        var deduped = deduper.Deduplicate(catalysts, TimeSpan.FromHours(6));

        Assert.Equal(2, deduped.Count);
        Assert.Equal(start, deduped[0].Timestamp);
        Assert.Equal(start.AddHours(7), deduped[1].Timestamp);
    }

    [Fact]
    public void NoveltyScore_DropsForRepeatedRelatedHeadline()
    {
        var scorer = new CatalystNoveltyScorer();
        var start = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
        var prior = new[]
        {
            Catalyst("RGTI", start, "Rigetti announces new quantum computing contract")
        };
        var repeated = Catalyst("RGTI", start.AddHours(2), "Rigetti announces quantum computing contract update");
        var unrelated = Catalyst("RGTI", start.AddHours(2), "Rigetti reports earnings and raises guidance");

        var repeatedScore = scorer.Score(repeated, prior, TimeSpan.FromDays(7));
        var unrelatedScore = scorer.Score(unrelated, prior, TimeSpan.FromDays(7));

        Assert.True(repeatedScore < unrelatedScore);
        Assert.InRange(repeatedScore, 0m, 1m);
        Assert.InRange(unrelatedScore, 0m, 1m);
    }

    [Theory]
    [InlineData("Company beats earnings and raises guidance", "earnings_or_guidance")]
    [InlineData("Biotech wins FDA approval for phase 3 drug", "biotech_regulatory")]
    [InlineData("Analyst upgrades stock and raises price target", "analyst_positive")]
    [InlineData("Company announces registered direct offering", "financing_or_dilution")]
    public void Classifier_MapsCommonTradingCatalysts(string headline, string expected)
    {
        var classifier = new CatalystEventClassifier();

        var category = classifier.Classify(Catalyst("ABC", DateTimeOffset.UtcNow, headline));

        Assert.Equal(expected, category);
    }

    [Fact]
    public void Analyze_UsesLatestBarAtOrBeforeEventAndForwardBarsOnly()
    {
        var start = DateTimeOffset.Parse("2026-06-01T13:30:00Z");
        var bars = Enumerable.Range(0, 80)
            .Select(i => new OhlcvBar(
                "POET",
                start.AddMinutes(i * 5),
                "5m",
                10m + i * 0.01m,
                10.20m + i * 0.01m,
                9.90m + i * 0.01m,
                10m + i * 0.10m,
                1000m + i * 10m))
            .ToArray();
        var eventTime = start.AddMinutes(17);
        var receivedAt = eventTime.AddSeconds(12);
        var catalysts = new[] { Catalyst("POET", eventTime, "POET wins commercial supply contract", 0.75m, receivedAt) };
        var runner = new CatalystTechnicalEventStudyRunner();

        var report = runner.Analyze(
            new Dictionary<string, IReadOnlyList<OhlcvBar>>(StringComparer.OrdinalIgnoreCase) { ["POET"] = bars },
            new Dictionary<string, IReadOnlyList<CatalystEvent>>(StringComparer.OrdinalIgnoreCase) { ["POET"] = catalysts },
            start,
            start.AddHours(8),
            "5m",
            new CatalystEventStudyOptions { Horizons = new[] { TimeSpan.FromHours(1) } });

        var observation = Assert.Single(report.Observations);
        Assert.Equal(eventTime.ToUniversalTime(), observation.ProviderPublishedTimestampUtc);
        Assert.Equal(receivedAt.ToUniversalTime(), observation.ReceivedTimestampUtc);
        Assert.Equal(start.AddMinutes(15), observation.AnchorTimestampUtc);
        Assert.Equal(-2m, observation.AnchorBarOffsetMinutes);
        Assert.Equal(start.AddMinutes(20), observation.FirstConfirmableTimestampUtc);
        Assert.Equal(3m, observation.FirstConfirmableDelayMinutes);
        Assert.Equal(bars[3].Close, observation.AnchorClose);
        Assert.True(observation.PreNewsReturn15mPct > 0m);
        Assert.True(observation.PostNewsReturn15mPct > 0m);
        var oneHour = Assert.Single(observation.ForwardReturns);
        Assert.Equal("1h", oneHour.Horizon);
        Assert.Equal(start.AddMinutes(75), oneHour.TargetTimestampUtc);
        Assert.True(oneHour.ReturnPct > 0m);
    }

    private static CatalystEvent Catalyst(string ticker, DateTimeOffset timestamp, string headline, decimal sentiment = 0.3m, DateTimeOffset? receivedAt = null) =>
        new(ticker, timestamp, CatalystType.NewsReport, headline, sentiment, Provider: "test", Source: "unit", Url: null, ReceivedAt: receivedAt);
}

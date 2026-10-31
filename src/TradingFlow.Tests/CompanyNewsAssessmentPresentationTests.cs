using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TradingFlow.Contracts.Presentation;
using TradingFlow.Contracts.V1;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class CompanyNewsAssessmentPresentationTests
{
    /// <summary>Both clients receive the original company-name capture clock, separately from article publication and assessment.</summary>
    [Fact]
    public void SampleRoundTripPreservesRetrospectiveCompanyNameCapture()
    {
        var publication = DateTimeOffset.Parse("2026-04-24T12:00:00Z");
        var captured = DateTimeOffset.Parse("2026-10-19T12:00:00Z");
        var assessed = DateTimeOffset.Parse("2026-10-24T16:00:00Z");
        var sample = new CompanyNewsAssessmentSample("ABC", "Analyst upgrades Example to Buy", publication,
            "AnalystAction", "Positive for this company", "Positive", .9m, "Subject confirmed",
            "Analyst upgrades Example to Buy", "HeadlineOnly", assessed, "revision", new string('a', 64),
            "Example Inc", "finviz", captured);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restored = JsonSerializer.Deserialize<CompanyNewsAssessmentSample>(JsonSerializer.Serialize(sample, options), options)!;
        Assert.Equal(sample, restored);
        Assert.Equal(captured, restored.CompanyNameObservedAtUtc);
        Assert.NotEqual(restored.PublishedAtUtc, restored.CompanyNameObservedAtUtc);
    }

    /// <summary>Missing publications do not trigger inference, create a data root or pretend that no company news exists.</summary>
    [Fact]
    public void EmptyPublicationCatalogIsReadOnlyAndExplicit()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        var dataRoot = Path.Combine(fixture.Root, "operator-data");
        var service = new CompanyNewsAssessmentPreviewService(new(fixture.Root, dataRoot, Path.Combine(dataRoot, "cache")),
            NullLogger<CompanyNewsAssessmentPreviewService>.Instance);
        var result = service.Read();
        Assert.Null(result.Summary);
        Assert.Empty(result.Samples);
        Assert.Contains("No company news assessments", result.Message);
        Assert.False(Directory.Exists(dataRoot));
        Assert.Throws<ArgumentException>(() => service.Read(0));
        Assert.Throws<ArgumentException>(() => service.Read(1, 101));
    }

    /// <summary>Changed publication bytes cannot be displayed as verified assessments or leak source paths to the client.</summary>
    [Fact]
    public void CorruptPublicationIsWithheldWithoutOverwritingIt()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        var dataRoot = Path.Combine(fixture.Root, "data");
        var publication = Path.Combine(dataRoot, "research", "evidence", "company-news-assessments", new string('a', 64), "company-assessments.json");
        Directory.CreateDirectory(Path.GetDirectoryName(publication)!);
        File.WriteAllText(publication, "corrupt synthetic packet");
        var service = new CompanyNewsAssessmentPreviewService(new(fixture.Root, dataRoot, Path.Combine(dataRoot, "cache")),
            NullLogger<CompanyNewsAssessmentPreviewService>.Instance);
        var result = service.Read();
        Assert.Null(result.PublicationId);
        Assert.Empty(result.Samples);
        Assert.Contains("could not be verified", result.Message);
        Assert.DoesNotContain(fixture.Root, result.Message);
        Assert.Equal("corrupt synthetic packet", File.ReadAllText(publication));
    }

    /// <summary>The API/mobile JSON preserves real assessment clocks and an unknown training bound without inventing a historical timestamp.</summary>
    [Fact]
    public void SummaryRoundTripKeepsUnknownAndRealMeasurementTimes()
    {
        var assessed = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
        var summary = new CompanyNewsAssessmentSummary("method", "ProsusAI/finbert", new string('a', 40), new string('b', 64),
            "literal-ticker-subject/v1", null, "Unknown training provenance", assessed, assessed, 2,
            new Dictionary<string, int> { ["Unknown"] = 1, ["Neutral"] = 1 });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restored = JsonSerializer.Deserialize<CompanyNewsAssessmentSummary>(JsonSerializer.Serialize(summary, options), options)!;
        Assert.Null(restored.TrainingDataCutoffUtc);
        Assert.Equal(assessed, restored.AssessedFromUtc);
        Assert.Equal(1, restored.StateCounts["Unknown"]);
        var text = String.Join("\n", CompanyNewsAssessmentPresentation.Lines(restored));
        Assert.Contains("accuracy not yet measured", text);
        Assert.Contains("cutoff bound UTC: Unknown", text);
        Assert.NotEqual(CompanyNewsAssessmentPresentation.DirectionLabel("Unknown"), CompanyNewsAssessmentPresentation.DirectionLabel("Neutral"));
    }
}

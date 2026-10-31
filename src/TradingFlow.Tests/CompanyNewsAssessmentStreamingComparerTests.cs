using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Data.Evidence.Sentiment;
using TradingFlow.Domain.News;

namespace TradingFlow.Tests;

public sealed class CompanyNewsAssessmentStreamingComparerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T13:00:00Z");

    /// <summary>Checks the former default-string and packet-byte comparisons against streamed digests for nested changes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamedComparisonMatchesOriginalBytesAndNestedChanges(bool packetOptions)
    {
        var options = packetOptions ? new JsonSerializerOptions(CompanyNewsAssessmentManifestStore.JsonOptions) { WriteIndented = true } : null;
        var revision = new CompanyNewsRevisionInput("synthetic", "article-α", "revision-1", "ABC raises guidance",
            null, String.Concat(Enumerable.Repeat("ABC raises guidance. café <tag> \"quoted\" \\ 雪 🧪\n", 1200)));
        var extraction = CompanyNewsAssessmentClassifier.Extract(revision, "ABC", Now);
        var changedSpan = extraction.EvidenceSpans[0] with { FullContext = extraction.EvidenceSpans[0].FullContext + "changed" };
        CompanyNewsExtraction[] candidates = [extraction,
            extraction with { Reasons = extraction.Reasons.Add("changed-reason") },
            extraction with { EvidenceSpans = extraction.EvidenceSpans.SetItem(0, changedSpan) },
            extraction with { RuleResult = extraction.RuleResult with { ClassifiedAtUtc = Now.AddTicks(1) } },
            extraction with { Revision = revision with { Summary = "" } },
            extraction with { ModelInputText = extraction.ModelInputText + " " }];
        foreach (var candidate in candidates)
        {
            var oldLeft = JsonSerializer.Serialize(extraction, options);
            var oldRight = JsonSerializer.Serialize(candidate, options);
            var oldBytes = Encoding.UTF8.GetBytes(oldRight);
            var fingerprint = CompanyNewsAssessmentContentComparer.Fingerprint(candidate, options);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(oldBytes)), fingerprint.Sha256);
            Assert.Equal(oldBytes.LongLength, fingerprint.ByteCount);
            Assert.Equal(oldLeft == oldRight, CompanyNewsAssessmentContentComparer.SameContent(extraction, candidate, options));
        }
    }

    /// <summary>The former extraction comparison used default numeric enums, while packet comparison uses string enums.</summary>
    [Fact]
    public void DefaultAndPacketOptionsKeepTheirDistinctWireRepresentations()
    {
        var extraction = CompanyNewsAssessmentClassifier.Extract(new("synthetic", "one", "one", "ABC raises guidance"), "ABC", Now);
        Assert.NotEqual(CompanyNewsAssessmentContentComparer.Fingerprint(extraction),
            CompanyNewsAssessmentContentComparer.Fingerprint(extraction, CompanyNewsAssessmentManifestStore.JsonOptions));
        Assert.True(CompanyNewsAssessmentContentComparer.SameContent<string?>(null, null));
        Assert.False(CompanyNewsAssessmentContentComparer.SameContent<string?>(null, ""));
    }
}

using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Cli;
using TradingFlow.Data.Evidence.Sentiment;
using TradingFlow.Domain.News;

namespace TradingFlow.Tests;

public sealed class CompanyNewsAssessmentStreamingReportTests
{
    /// <summary>Compares full report bytes and hashes with former serializers, covering mapping text, nulls, enums, Unicode and reviewer flags.</summary>
    [Fact]
    public async Task SamplesAndBlindReviewKeepExactOriginalBytes()
    {
        using var fixture = new HistoricalDiagnosticNewsTests.Archive();
        var manifest = await CompanyNewsAssessmentStreamingIoTests.BuildManifest(fixture);
        var original = manifest.Rows[0];
        var mapping = new CompanyNewsIssuerMapping("ABC", "Acme café 雪, Inc.", "synthetic-provider", "name-source",
            new string('a', 64), manifest.CreatedAtUtc.AddMinutes(-1));
        var mapped = original with { Source = original.Source with { WorkItemId = "mapped-雪", AssumedAvailableAtUtc = null,
                Revision = original.Source.Revision with { Headline = "ABC raises guidance\nwith quoted \"text\"" } },
            Assessment = original.Assessment with { Extraction = original.Assessment.Extraction with { IssuerMapping = mapping },
                ModelEvidence = new(manifest.Model, original.Assessment.Extraction.ModelInputSha256, CompanyNewsPolarity.Unknown,
                    0m, manifest.CreatedAtUtc, false, "synthetic failure café 雪"), Reasons = ["unknown", "review required"] } };
        manifest = manifest with { Rows = [original, mapped] };
        using var sampleOutput = new MemoryStream();
        await InvokeReport("WriteSamplesAsync", sampleOutput, manifest, CancellationToken.None);
        var expectedSamples = Encoding.UTF8.GetBytes(FormerSamples(manifest));
        Assert.Equal(expectedSamples, sampleOutput.ToArray());
        Assert.Equal(SHA256.HashData(expectedSamples), SHA256.HashData(sampleOutput.ToArray()));

        var sources = Enumerable.Range(0, 12).Select(index => original.Source with { WorkItemId = "story-" + index,
            Revision = original.Source.Revision with { ProviderArticleId = "article-" + index } }).ToImmutableArray();
        var hash = new string('c', 64);
        var previousBlindBytes = JsonSerializer.SerializeToUtf8Bytes(new { ManifestSha256 = hash, HumanLabelsCompleted = 0,
            Instructions = "Independent humans only; judge exact archived company relevance/direction without model predictions or later prices. Prior category labels are not sentiment labels.",
            Stories = sources.Select(source => new { source.WorkItemId, source.RawRevisionContentSha256,
                source.Revision, source.Symbols, source.ActualReceivedAtUtc,
                ReviewerA = new { CompanyRelevance = (string?)null, Direction = (string?)null, Notes = (string?)null },
                IndependentReviewerBRequired = sources.IndexOf(source) < 10,
                ReviewerB = new { CompanyRelevance = (string?)null, Direction = (string?)null, Notes = (string?)null } }) },
            new JsonSerializerOptions(CompanyNewsAssessmentManifestStore.JsonOptions) { WriteIndented = true });
        using var blindOutput = new MemoryStream();
        await InvokeReport("WriteBlindReviewAsync", blindOutput, hash, sources, CancellationToken.None);
        Assert.Equal(previousBlindBytes, blindOutput.ToArray());
        Assert.Equal(SHA256.HashData(previousBlindBytes), SHA256.HashData(blindOutput.ToArray()));
        using var parsed = JsonDocument.Parse(blindOutput.ToArray());
        Assert.Equal(Enumerable.Range(0, 12).Select(index => index < 10),
            parsed.RootElement.GetProperty("Stories").EnumerateArray().Select(item => item.GetProperty("IndependentReviewerBRequired").GetBoolean()));
    }

    /// <summary>Exercises the command's private stream producers without network calls or expanding its public command API.</summary>
    private static Task InvokeReport(string method, params object[] arguments) =>
        (Task)typeof(CompanyNewsAssessmentCommand).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments)!;

    /// <summary>Retains the former all-row formatter solely as the byte-for-byte regression oracle.</summary>
    private static string FormerSamples(CompanyNewsAssessmentManifest manifest)
    {
        var text = new StringBuilder("# Company news assessment samples\n\nRetrospective development measurements. Human relevance/direction labels and classifier accuracy remain unmeasured. No economic attempt was executed. Confidence describes the model text label, not recovery probability.\n\n");
        text.Append("Model: ").Append(manifest.Model.Model).Append('@').Append(manifest.Model.Version)
            .Append("\n\nTraining provenance: ").Append(manifest.Model.TrainingDataProvenance).Append("\n");
        foreach (var row in manifest.Rows)
        {
            text.Append("\n## ").Append(row.Source.WorkItemId).Append(" / ").Append(row.Symbol)
                .Append("\n\nHeadline: ").Append(row.Source.Revision.Headline.Replace('\n', ' '))
                .Append("\n\nState: ").Append(row.Assessment.State).Append("; category: ").Append(row.Assessment.Category)
                .Append("; asserted direction: ").Append(row.Assessment.AssertedDirection)
                .Append("; textual polarity: ").Append(row.Assessment.ModelEvidence?.TopLabel.ToString() ?? "not inferred")
                .Append("; confidence: ").Append(row.Assessment.ModelEvidence?.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")
                .Append("\n\nReasons: ").Append(String.Join(", ", row.Assessment.Reasons))
                .Append("\n\nActual archive receipt UTC: ").Append(row.Source.ActualReceivedAtUtc.ToString("O"))
                .Append("; actual assessment UTC: ").Append(row.Assessment.AssessedAtUtc.ToString("O"))
                .Append("; separately assumed historical availability UTC: ").Append(row.Source.AssumedAvailableAtUtc?.ToString("O") ?? "none")
                .Append("\n\nInference failure: ").Append(row.Assessment.ModelEvidence?.FailureReason ?? "none").Append('\n');
            if (row.Assessment.Extraction.IssuerMapping is { } mapping)
                text.Append("\n\nCompany mapping: ").Append(mapping.OfficialName).Append(" / ").Append(mapping.Symbol)
                    .Append("; source: ").Append(mapping.SourceProvider).Append("; actual mapping capture UTC: ")
                    .Append(mapping.ObservedAtUtc.ToString("O")).Append(". Present-day captured name; original historical name availability is unproved.\n");
        }
        return text.ToString();
    }
}

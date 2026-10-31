using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingFlow.Data.Evidence.Historical;
using TradingFlow.Data.Evidence.Labeling;
using TradingFlow.Data.Evidence.Sentiment;
using TradingFlow.Domain.News;

namespace TradingFlow.Tests;

/// <summary>Anonymous frozen archive shape: no provider text, article identities or operator files enter a test.</summary>
internal sealed record CompanyNewsSyntheticShape(CompanyNewsSyntheticField Headline, CompanyNewsSyntheticField Summary,
    CompanyNewsSyntheticField Body, ImmutableArray<int> IssuerIds)
{
    internal const string SourceManifestSha256 = "12629c5c642108f665ef4b5af72c7fb4df397cdb79499b3885218f9009f8ba94";
    internal const string ProfileSha256 = "9a06d087cf52b0dc26787e0939c4c9407048aa32b4f6434fafd52a621ab1cada";
    // Anonymous IDs of the original selected 24 stocks; retaining their coverage prevents rescoping away most stories.
    internal static readonly ImmutableArray<int> SelectedIssuerIds = [265, 403, 510, 709, 711, 794, 837, 1128,
        1848, 1902, 1937, 1944, 1958, 2056, 2147, 2166, 2208, 2258, 2381, 2401, 2414, 2582, 2620, 2718];

    /// <summary>Reads only reviewed numeric field sizes and anonymous issuer overlap from the checked-in fixture.</summary>
    internal static ImmutableArray<CompanyNewsSyntheticShape> Read()
    {
        var path = Path.Combine(TestRepository.FindRoot(), "src", "TradingFlow.Tests", "Fixtures", "company-news-archive-shape.csv");
        if (NewsAssessmentWorkflow.FileHash(path) != ProfileSha256)
            throw new InvalidDataException("Frozen anonymous archive shape changed; recapture and review its numeric evidence.");
        return File.ReadLines(path).Skip(1).Select(line => {
            var values = line.Split(',');
            // Parse frozen numeric sizes independently of the machine's display locale.
            int Number(int index) => Int32.Parse(values[index], CultureInfo.InvariantCulture);
            return new CompanyNewsSyntheticShape(new(Number(0), Number(1), Number(2)), new(Number(3), Number(4), Number(5)),
                new(Number(6), Number(7), Number(8)), values[9].Split('|').Select(item => Int32.Parse(item, CultureInfo.InvariantCulture)).ToImmutableArray());
        }).ToImmutableArray();
    }
}

/// <summary>Matches each field's UTF-16 length, UTF-8 length and default JSON escaping expansion without retaining original text.</summary>
internal sealed record CompanyNewsSyntheticField(int Characters, int Utf8Bytes, int EscapedJsonBytes)
{
    /// <summary>Constructs anonymous assertions and filler with the source field's three exact size measurements.</summary>
    internal string? Text(string assertion, bool economicParagraphs = false, bool cueDense = false)
    {
        if (Characters < 0) return null;
        var extraUtf8 = Utf8Bytes - Characters;
        var unicodeThree = extraUtf8 / 2;
        var unicodeTwo = extraUtf8 % 2;
        var expansion = EscapedJsonBytes - Characters - 5 * (unicodeThree + unicodeTwo);
        if (expansion < 0) throw new InvalidDataException("Unsupported numeric Unicode/escaping shape.");
        var escapedFive = expansion / 5;
        var escapedOne = expansion % 5;
        var filler = unicodeThree + unicodeTwo + escapedFive + escapedOne;
        var available = Characters - filler;
        if (available < 0) throw new InvalidDataException("Numeric escaping shape does not fit the original field length.");
        var prefix = assertion.Length <= available ? assertion : new string('x', Math.Min(available, 9));
        var builder = new StringBuilder(Characters);
        builder.Append(prefix);
        var remaining = available - prefix.Length;
        if (economicParagraphs)
        {
            // Sentence boundaries keep cue excerpts/prefixes bounded. A separate stress case increases cue density.
            var sentence = ". Synthetic archived market context records company operations and financial commentary. ";
            var paragraph = sentence + assertion + ". " + String.Concat(Enumerable.Repeat(sentence, cueDense ? 0 : 8));
            while (remaining >= paragraph.Length) { builder.Append(paragraph); remaining -= paragraph.Length; }
        }
        builder.Append(' ', remaining);
        builder.Append('\u4e2d', unicodeThree).Append('\u00e9', unicodeTwo);
        // Apostrophes have the same default JSON expansion as HTML punctuation without creating artificial malformed tags.
        builder.Append('\'', escapedFive).Append('\n', escapedOne);
        return builder.ToString();
    }
}

/// <summary>Owns isolated raw receipts, calendar and mapping evidence and exercises normal immutable production publications.</summary>
internal sealed class CompanyNewsSyntheticWorkloadFixture : IDisposable
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T13:00:00Z", CultureInfo.InvariantCulture);
    // The existing assessment policy pins this model family; only evidence and artifact identities are synthetic.
    internal static readonly CompanyNewsPinnedModel Model = new("synthetic-no-inference", "ProsusAI/finbert",
        new string('a', 40), new string('b', 64), "Synthetic deterministic model evidence; no model was loaded or executed.", null);
    private readonly HistoricalDiagnosticNewsTests.Archive archive = new();
    internal string Root => archive.Root;
    internal CompanyNewsAssessmentSelection Selection { get; private set; } = null!;
    internal ImmutableArray<CompanyNewsAssessmentSource> Sources { get; private set; }
    internal ImmutableArray<CompanyNewsAssessmentDependency> Dependencies { get; private set; }
    internal CompanyNewsIssuerMappingReadResult? Mappings { get; private set; }
    internal HistoricalDiagnosticNewsManifest DiagnosticManifest { get; private set; } = null!;
    internal int FakeInferenceCalls { get; private set; }

    /// <summary>Publishes synthetic news receipts, historical selection and official-name mappings through existing components.</summary>
    internal static CompanyNewsSyntheticWorkloadFixture Create(ImmutableArray<CompanyNewsSyntheticShape> profile, bool cueDense = false)
    {
        var fixture = new CompanyNewsSyntheticWorkloadFixture();
        try
        {
            // Bounded raw pages avoid measuring a giant test-only anonymous object serialization.
            foreach (var page in profile.Select((shape, index) => (shape, index)).Chunk(50))
                fixture.archive.News("synthetic-news-" + page[0].index.ToString("D5", CultureInfo.InvariantCulture),
                    page.Select(item => Story(item.shape, item.index, cueDense)).ToArray());
            fixture.archive.Earnings("synthetic-issuers", profile.SelectMany(item => item.IssuerIds).Distinct().Where(id => id % 2 == 0)
                .Select(id => (object)new { ticker = Symbol(id), company = "Synthetic Issuer " + id.ToString("D4", CultureInfo.InvariantCulture) + " Inc" }).ToArray());
            var historical = HistoricalEvidenceManifestBuilder.BuildFromCollectionRoot(fixture.Root, HistoricalDiagnosticNewsTests.Archive.Capture.AddDays(1));
            var mappingPath = HistoricalEvidenceManifestStore.WriteExclusive(Path.Combine(fixture.Root, "issuer-manifest"), historical);
            var selectedIds = profile.SelectMany(item => item.IssuerIds).Distinct().Where(CompanyNewsSyntheticShape.SelectedIssuerIds.Contains).ToArray();
            if (profile.Any(item => !item.IssuerIds.Any(selectedIds.Contains)))
                throw new InvalidDataException("Synthetic shape must preserve every story's association with the original anonymous selected issuers.");
            var provider = fixture.archive.Provider(selected: selectedIds.Select(Symbol).ToArray());
            fixture.DiagnosticManifest = provider.Manifest;
            var publication = HistoricalDiagnosticNewsManifestStore.PublishExclusive(Path.Combine(fixture.Root, "published"), provider.Manifest);
            fixture.Selection = new("diagnostic", Path.GetRelativePath(fixture.Root, publication.Path), publication.Sha256);
            var selected = CompanyNewsAssessmentBuilder.ReadDiagnostic(fixture.Root, publication.Path, publication.Sha256);
            fixture.Sources = selected.Sources;
            fixture.Dependencies = selected.Dependencies;
            fixture.Mappings = CompanyNewsIssuerMappingReader.ReadVerified(fixture.Root, mappingPath, NewsAssessmentWorkflow.FileHash(mappingPath),
                selected.Sources.SelectMany(item => item.Symbols));
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    /// <summary>Builds real assessments using a local deterministic delegate with no model or network dependency.</summary>
    internal Task<CompanyNewsAssessmentManifest> BuildAsync(CancellationToken cancellationToken = default) =>
        CompanyNewsAssessmentBuilder.BuildAsync(Root, "synthetic-memory-workload", Selection, Sources, Dependencies,
            Model, (extraction, _) => {
                FakeInferenceCalls++;
                // Distinct deterministic evidence remains tied to the complete input and cannot call a sidecar.
                return Task.FromResult(new CompanyNewsModelEvidence(Model, extraction.ModelInputSha256,
                    CompanyNewsPolarity.Positive, .9m, Now));
            }, new FixedClock(), cancellationToken, Mappings);

    /// <summary>Attaches the assessment to the original selection for normal workflow/scope tests.</summary>
    internal HistoricalDiagnosticNewsPublication PublishDiagnostic(HistoricalDiagnosticNewsPublication assessment)
    {
        var mapped = DiagnosticManifest with { CompanyAssessmentManifest = new(Path.GetRelativePath(Root, assessment.Path), assessment.Sha256) };
        return HistoricalDiagnosticNewsManifestStore.PublishExclusive(Path.Combine(Root, "mapped-diagnostic"), mapped);
    }

    /// <summary>Releases the builder selection so the next phase can measure a fresh reader's resident graph.</summary>
    internal void ReleaseSelectionObjects()
    {
        Sources = default;
        Dependencies = default;
        Mappings = null;
        DiagnosticManifest = null!;
    }

    /// <summary>Converts an anonymous tag ID to deterministic supported ticker syntax.</summary>
    internal static string Symbol(int id) => "Z" + id.ToString("D4", CultureInfo.InvariantCulture);

    /// <summary>Creates a synthetic raw row preserving joint field sizes and the archive's cross-story tag associations.</summary>
    private static object Story(CompanyNewsSyntheticShape shape, int index, bool cueDense)
    {
        var assertion = Symbol(shape.IssuerIds[0]) + " raises guidance";
        return new { id = "synthetic-" + index.ToString("D5", CultureInfo.InvariantCulture),
            created_at = "2026-08-17T10:00:00Z", updated_at = "2026-08-17T10:00:00Z",
            symbols = shape.IssuerIds.Select(Symbol).ToArray(), headline = shape.Headline.Text(assertion) ?? "",
            summary = shape.Summary.Text(assertion),
            content = shape.Body.Text(assertion, economicParagraphs: cueDense || index % 8 != 0, cueDense: cueDense) };
    }

    private sealed class FixedClock : TimeProvider
    {
        /// <summary>Keeps actual fake inference and extraction clocks deterministic and later than source receipts.</summary>
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Delegates deletion solely to the existing checked, uniquely owned synthetic fixture root.</summary>
    public void Dispose() => archive.Dispose();
}

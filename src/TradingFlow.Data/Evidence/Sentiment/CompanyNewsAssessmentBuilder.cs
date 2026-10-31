using System.Collections.Immutable;
using System.Text.Json;
using TradingFlow.Data.Evidence.Historical;
using TradingFlow.Data.Evidence.Labeling;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Data.Evidence.Sentiment;

/// <summary>Reuses existing archived revisions and pure subject/rule extraction before invoking a pinned model.</summary>
public static class CompanyNewsAssessmentBuilder
{
    /// <summary>Preserves the exact retained development selection and verifies the original packet and raw source lineage.</summary>
    public static (ImmutableArray<CompanyNewsAssessmentSource> Sources, ImmutableArray<CompanyNewsAssessmentDependency> Dependencies)
        ReadPilot(string researchRoot, string selectionPath, string expectedSha256)
    {
        var selectionBytes = File.ReadAllBytes(selectionPath);
        if (NewsAssessmentWorkflow.Hash(selectionBytes) != expectedSha256) throw new InvalidDataException("Pilot selection SHA256 changed.");
        using var selection = JsonDocument.Parse(selectionBytes);
        var value = selection.RootElement;
        if (value.GetProperty("Version").GetString() != NewsCategoryPilotReview.SelectionVersion ||
            !value.GetProperty("PilotOnly").GetBoolean() || !value.GetProperty("DevelopmentOnly").GetBoolean())
            throw new InvalidDataException("Company samples require the retained development pilot selection.");
        var packetPath = value.GetProperty("SourcePacketPath").GetString()!;
        var packetBytes = File.ReadAllBytes(packetPath);
        var packetHash = value.GetProperty("PacketSha256").GetString()!;
        if (NewsAssessmentWorkflow.Hash(packetBytes) != packetHash) throw new InvalidDataException("Pilot source packet changed.");
        var packet = NewsAssessmentWorkflow.Read<NewsAssessmentPacket>(packetBytes);
        NewsAssessmentWorkflow.ValidatePacket(packet, packetHash);
        var assigned = value.GetProperty("Items").EnumerateArray().ToArray();
        if (assigned.Length == 0 || value.GetProperty("ExpectedCount").GetInt32() != assigned.Length)
            throw new InvalidDataException("The retained pilot must contain its exact declared assignments.");
        var work = packet.WorkItems.ToDictionary(item => item.Item.WorkItemId, StringComparer.Ordinal);
        var sources = ImmutableArray.CreateBuilder<CompanyNewsAssessmentSource>();
        foreach (var item in assigned)
        {
            var id = item.GetProperty("WorkItemId").GetString()!;
            if (!work.TryGetValue(id, out var source) || source.HeldOut ||
                source.Item.ProviderArticleId != item.GetProperty("ArticleId").GetString() ||
                source.Item.NewsRevisionContentSha256 != item.GetProperty("RevisionSha256").GetString())
                throw new InvalidDataException("Pilot assignment differs from its original archived revision.");
            VerifyPilotSource(source);
            sources.Add(new(id, new(source.Item.NewsProvider, source.Item.ProviderArticleId, source.Item.NewsRevisionId,
                source.Item.Headline, source.Item.Summary, source.Body), source.Item.NewsRevisionContentSha256,
                source.Item.Symbols.ToImmutableArray(), source.Item.PublishedAtUtc, source.Item.ProviderUpdatedAtUtc,
                source.ObservedReceiptAtUtc, source.ReconstructedArticleAvailableAtUtc));
        }
        if (sources.Select(item => item.WorkItemId).Distinct(StringComparer.Ordinal).Count() != assigned.Length)
            throw new InvalidDataException("Repeated pilot assignments are not allowed.");
        var paths = packet.SourceFiles.Select(item => (item.Path, item.Sha256))
            .Append((selectionPath, expectedSha256)).Append((packetPath, packetHash));
        return (sources.ToImmutable(), Dependencies(researchRoot, paths));
    }

    /// <summary>Checks packet text against the exact raw response row; no modified packet text can acquire an archive identity.</summary>
    private static void VerifyPilotSource(NewsAssessmentWorkItem source)
    {
        using var payload = JsonDocument.Parse(File.ReadAllBytes(source.PayloadPath));
        var rows = payload.RootElement.GetProperty("news").EnumerateArray().ToArray();
        if (source.SourceRowIndex >= rows.Length) throw new InvalidDataException("Pilot source row no longer exists.");
        var article = rows[source.SourceRowIndex];
        string? Text(string name) => article.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
        if (CompanyNewsAssessmentClassifier.ComputeTextSha256(article.GetRawText()) != source.Item.NewsRevisionContentSha256 ||
            article.GetProperty("id").ToString() != source.Item.ProviderArticleId || Text("headline") != source.Item.Headline ||
            Text("summary") != source.Item.Summary || Text("content") != source.Body)
            throw new InvalidDataException("Pilot archived response differs from its bound revision text.");
    }

    /// <summary>Uses every frozen archived revision; historical revision selection remains in the shared presence provider.</summary>
    public static (ImmutableArray<CompanyNewsAssessmentSource> Sources, ImmutableArray<CompanyNewsAssessmentDependency> Dependencies)
        ReadDiagnostic(string researchRoot, string manifestPath, string expectedSha256)
    {
        var provider = HistoricalDiagnosticNewsProvider.LoadVerified(researchRoot, manifestPath, expectedSha256);
        var rawInputs = ReadRawRevisions(provider.Manifest);
        var sources = provider.Manifest.Revisions.Select(revision =>
        {
            var fact = provider.Manifest.Archives[revision.ArchiveIndex].Evidence.Facts.Single(item => item.FactId == revision.FactId);
            using var metadata = JsonDocument.Parse(fact.ValueJson);
            var article = metadata.RootElement;
            var symbols = article.TryGetProperty("symbols", out var tags) && tags.ValueKind == JsonValueKind.Array
                ? tags.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!.Trim().ToUpperInvariant())
                    .Where(item => !String.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray() : [];
            return new CompanyNewsAssessmentSource(fact.FactId,
                rawInputs[(revision.ArchiveIndex, fact.FactId)],
                fact.ContentSha256, symbols, fact.PublishedAtUtc, fact.UpdatedAtUtc,
                fact.ActualReceivedAtUtc, revision.AssumedAvailableAtUtc);
        }).GroupBy(item => (item.Revision.Provider, item.Revision.ProviderArticleId, item.RawRevisionContentSha256))
            .Select(group => group.OrderBy(item => item.ActualReceivedAtUtc).First()).ToImmutableArray();
        return (sources, Dependencies(researchRoot, provider.Dependencies.Select(item => (item.Path, item.Sha256))));
    }

    /// <summary>Reads each retained payload once and joins full raw fields by the existing canonical raw article hash.</summary>
    internal static Dictionary<(int ArchiveIndex, string FactId), CompanyNewsRevisionInput> ReadRawRevisions(HistoricalDiagnosticNewsManifest manifest)
    {
        var result = new Dictionary<(int, string), CompanyNewsRevisionInput>();
        foreach (var archiveGroup in manifest.Revisions.GroupBy(item => item.ArchiveIndex))
        {
            var archive = manifest.Archives[archiveGroup.Key].Evidence;
            var factsById = archive.Facts.ToDictionary(item => item.FactId, StringComparer.Ordinal);
            var revisionByFactId = archiveGroup.ToDictionary(item => item.FactId, StringComparer.Ordinal);
            var facts = archiveGroup.Select(item => factsById[item.FactId]).ToArray();
            var neededHashes = facts.Select(item => item.ContentSha256).ToHashSet(StringComparer.Ordinal);
            var observations = facts.SelectMany(item => item.SourceObservationIds).ToHashSet(StringComparer.Ordinal);
            var fields = new Dictionary<string, (string Id, string Headline, string? Summary, string? Body)>(StringComparer.Ordinal);
            foreach (var observation in archive.Observations.Where(item => observations.Contains(item.ObservationId))
                .DistinctBy(item => item.PayloadRelativePath))
            {
                using var payload = JsonDocument.Parse(HistoricalEvidenceManifestStore.ReadBound(archive.CollectionRoot, observation.PayloadRelativePath));
                if (!payload.RootElement.TryGetProperty("news", out var news) || news.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Bound news payload does not contain its archived rows.");
                foreach (var article in news.EnumerateArray())
                {
                    var hash = HistoricalEvidenceManifestStore.JsonHash(article);
                    if (!neededHashes.Contains(hash)) continue;
                    string? Text(string name) => article.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    var id = article.TryGetProperty("id", out var suppliedId) ? suppliedId.ToString() : "";
                    fields[hash] = (id, Text("headline") ?? "", Text("summary"), Text("content"));
                }
            }
            foreach (var fact in facts)
            {
                if (!fields.TryGetValue(fact.ContentSha256, out var raw)) throw new InvalidDataException("Exact raw news revision was not found in its bound payload.");
                if (!String.IsNullOrWhiteSpace(raw.Id) && raw.Id.Trim() != fact.SourceRecordId)
                    throw new InvalidDataException("Raw news article ID differs from its archive fact.");
                var provider = revisionByFactId[fact.FactId].Provider;
                result[(archiveGroup.Key, fact.FactId)] = new(provider, fact.SourceRecordId, fact.FactId, raw.Headline, raw.Summary, raw.Body);
            }
        }
        return result;
    }

    /// <summary>Runs inference sequentially once per supported issuer/revision and records failures as unknown, never no-news.</summary>
    public static async Task<CompanyNewsAssessmentManifest> BuildAsync(string researchRoot, string selectionKind,
        CompanyNewsAssessmentSelection selection,
        ImmutableArray<CompanyNewsAssessmentSource> sources, ImmutableArray<CompanyNewsAssessmentDependency> dependencies,
        CompanyNewsPinnedModel model, Func<CompanyNewsExtraction, CancellationToken, Task<CompanyNewsModelEvidence>> infer,
        TimeProvider clock, CancellationToken cancellationToken = default,
        CompanyNewsIssuerMappingReadResult? issuerMappings = null)
    {
        if (issuerMappings is not null)
        {
            var selectedSymbols = CompanyNewsIssuerMappingReader.NormalizeSelectedSymbols(sources.SelectMany(source => source.Symbols));
            if (!issuerMappings.SelectedNewsSymbols.SequenceEqual(selectedSymbols))
                throw new InvalidDataException("Issuer mappings were not checked against the exact frozen news selection's symbol set.");
            dependencies = dependencies.Concat(issuerMappings.Dependencies).GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => group.Select(item => item.Sha256).Distinct(StringComparer.Ordinal).Count() == 1
                    ? group.First() : throw new InvalidDataException("Issuer mapping dependency conflicts with a frozen news source."))
                .ToImmutableArray();
        }
        var rows = ImmutableArray.CreateBuilder<CompanyNewsAssessmentRow>();
        foreach (var source in sources.OrderBy(item => item.WorkItemId, StringComparer.Ordinal))
        foreach (var symbol in source.Symbols.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mapped = issuerMappings?.Mappings.GetValueOrDefault(symbol);
            var extraction = mapped is null
                ? CompanyNewsAssessmentClassifier.Extract(source.Revision, symbol, clock.GetUtcNow())
                : CompanyNewsAssessmentClassifier.Extract(source.Revision, symbol, clock.GetUtcNow(), mapped);
            CompanyNewsModelEvidence? evidence = null;
            if (extraction.CanAssess)
            {
                try { evidence = await infer(extraction, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    evidence = new(model, extraction.ModelInputSha256, CompanyNewsPolarity.Unknown, 0, clock.GetUtcNow(), false,
                        exception.GetType().Name + ": " + exception.Message);
                }
            }
            var assessment = CompanyNewsAssessmentClassifier.Complete(extraction, evidence, model, clock.GetUtcNow());
            rows.Add(new(source, symbol, assessment));
        }
        var policy = new HistoricalCompanyNewsAssessmentPolicy(HistoricalCompanyNewsDiagnosticPolicy.AdmissionPolicyId,
            model.Model, model.ArtifactSha256, model.TrainingDataProvenance,
            issuerMappings is null ? CompanyNewsAssessmentClassifier.MappingVersion : CompanyNewsAssessmentClassifier.MappedMappingVersion,
            issuerMappings is null ? CompanyNewsAssessmentClassifier.ExtractionVersion : CompanyNewsAssessmentClassifier.MappedExtractionVersion,
            EconomicNewsRuleClassifier.Version, HistoricalCompanyNewsDiagnosticPolicy.AssessmentMethodVersion,
            HistoricalCompanyNewsDiagnosticPolicy.HistoricalUseLimitation, model.Provider, model.Version, model.TrainingDataCutoffUtc);
        var manifest = new CompanyNewsAssessmentManifest(issuerMappings is null ? CompanyNewsAssessmentManifest.CurrentSchema : CompanyNewsAssessmentManifest.MappedSchema,
            clock.GetUtcNow(), Path.GetFullPath(researchRoot), policy, model, dependencies, rows.ToImmutable(), selectionKind, selection,
            IssuerMappingSource: issuerMappings?.Source);
        CompanyNewsAssessmentManifestStore.Validate(manifest);
        return manifest;
    }

    /// <summary>Hashes and bounds every source beneath the selected root, rejecting stale declarations before inference.</summary>
    private static ImmutableArray<CompanyNewsAssessmentDependency> Dependencies(string root, IEnumerable<(string Path, string Sha256)> files)
    {
        return files.GroupBy(item => Path.GetFullPath(item.Path), StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var relative = Path.GetRelativePath(root, group.Key);
                var full = HistoricalEvidenceManifestStore.BoundPath(root, relative);
                var hashes = group.Select(item => item.Sha256).Distinct(StringComparer.Ordinal).ToArray();
                if (hashes.Length != 1 || NewsAssessmentWorkflow.FileHash(full) != hashes[0])
                    throw new InvalidDataException("Assessment source differs from retained lineage: " + relative);
                return new CompanyNewsAssessmentDependency(relative, hashes[0]);
            }).ToImmutableArray();
    }
}

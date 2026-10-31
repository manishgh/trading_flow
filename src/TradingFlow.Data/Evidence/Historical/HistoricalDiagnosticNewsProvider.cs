using System.Collections.Immutable;
using System.Text.Json;
using System.Security.Cryptography;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Data.Evidence.Sentiment;
using TradingFlow.Domain.News;

namespace TradingFlow.Data.Evidence.Historical;

/// <summary>Adapts captured raw revisions to research-only presence decisions without backdating actual receipts.</summary>
public sealed class HistoricalDiagnosticNewsProvider : IHistoricalNewsPresenceProvider
{
    private sealed record Version(HistoricalFactVersion Fact, HistoricalDiagnosticNewsRevision Derived,
        ImmutableArray<string> Symbols, ImmutableArray<string> SourceHashes, ImmutableArray<string> UncertainScope);
    private sealed record Article(string Provider, string Id, Version[] Versions);
    private readonly Dictionary<string, Article[]> articlesBySymbol;
    private readonly Article[] unscopedArticles;
    private readonly CompanyNewsAssessmentManifest? companyAssessments;
    private readonly Dictionary<(string Provider, string ArticleId, string RawHash, string Symbol), CompanyNewsAssessmentRow> assessmentsByRevision;
    public HistoricalDiagnosticNewsManifest Manifest { get; }
    public string ManifestSha256 { get; }
    public string DependencyGraphSha256 { get; }
    public IReadOnlyList<HistoricalDiagnosticNewsDependency> Dependencies { get; }
    public CompanyNewsAssessmentManifest? CompanyAssessments => companyAssessments;

    /// <summary>Retains verified graph identity and indexes candidate articles; the symbol union is lookup-only, never decision membership.</summary>
    private HistoricalDiagnosticNewsProvider(HistoricalDiagnosticNewsManifest manifest, string hash,
        IReadOnlyList<HistoricalDiagnosticNewsDependency> dependencies, string graphHash,
        CompanyNewsAssessmentManifest? assessments = null)
    {
        Manifest = manifest;
        ManifestSha256 = hash;
        Dependencies = dependencies;
        DependencyGraphSha256 = graphHash;
        companyAssessments = assessments;
        assessmentsByRevision = assessments?.Rows.ToDictionary(row => (row.Source.Revision.Provider,
            row.Source.Revision.ProviderArticleId, row.Source.RawRevisionContentSha256, row.Symbol)) ?? [];
        var articles = LoadVersions(manifest).GroupBy(item => (item.Derived.Provider, item.Fact.SourceRecordId))
            .Select(group => new Article(group.Key.Provider, group.Key.SourceRecordId,
                group.OrderBy(item => item.Derived.AssumedAvailableAtUtc).ThenBy(item => item.Fact.UpdatedAtUtc).ToArray())).ToArray();
        articlesBySymbol = articles.SelectMany(article => article.Versions.SelectMany(item => item.Symbols.Concat(item.UncertainScope))
                .Distinct(StringComparer.Ordinal).Select(symbol => (symbol, article)))
            .GroupBy(item => item.symbol, StringComparer.Ordinal).ToDictionary(group => group.Key,
                group => group.Select(item => item.article).ToArray(), StringComparer.Ordinal);
        unscopedArticles = articles.Where(article => article.Versions.Any(item =>
            item.Derived.UnknownReason is not null && item.Symbols.IsEmpty && item.UncertainScope.IsEmpty)).ToArray();
    }

    /// <summary>Verifies the exact manifest and all raw/documentation dependencies under the explicitly selected research root.</summary>
    public static HistoricalDiagnosticNewsProvider LoadVerified(string researchRoot, string manifestPath, string? expectedSha256 = null)
    {
        if (!Path.IsPathFullyQualified(researchRoot)) throw new InvalidDataException("Diagnostic research root must be absolute.");
        var root = Path.GetFullPath(researchRoot);
        var dependencies = new Dictionary<string, HistoricalDiagnosticNewsDependency>(StringComparer.OrdinalIgnoreCase);
        // Bind declarations identically for parsed and hash-only sources without retaining whole source payloads.
        string Bound(string path)
        {
            var relative = Path.IsPathFullyQualified(path) ? Path.GetRelativePath(root, path) : path;
            return HistoricalEvidenceManifestStore.BoundPath(root, relative);
        }
        // Preserve every expected hash check and the exact canonical graph registration.
        void Register(string full, string hash, string? expected)
        {
            if (expected is not null && (!HistoricalEvidenceManifestStore.IsHash(expected) || !hash.Equals(expected, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Diagnostic dependency hash differs from its frozen identity: " + Path.GetRelativePath(root, full));
            dependencies[full] = new(full, hash);
        }
        // Only the small diagnostic manifest needs materialized bytes for schema parsing.
        byte[] ReadManifest(string path, string? expected)
        {
            var full = Bound(path);
            var bytes = File.ReadAllBytes(full);
            Register(full, HistoricalEvidenceManifestStore.Hash(bytes), expected);
            return bytes;
        }
        // Hash assessment packets and original receipts/payloads through bounded buffers; their own readers retain all semantic checks.
        void VerifyDependency(string path, string expected)
        {
            var full = Bound(path);
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            Register(full, Convert.ToHexStringLower(SHA256.HashData(stream)), expected);
        }
        var manifestBytes = ReadManifest(manifestPath, expectedSha256);
        var manifest = JsonSerializer.Deserialize<HistoricalDiagnosticNewsManifest>(manifestBytes, HistoricalEvidenceManifestStore.JsonOptions)
            ?? throw new InvalidDataException("Diagnostic manifest is empty.");
        if (manifest.Archives.IsDefault) throw new InvalidDataException("Diagnostic archives are uninitialized.");
        var rebased = manifest.Archives.Select(archive => {
            var archiveRoot = archive.RootRelativePath == "." ? root : HistoricalEvidenceManifestStore.BoundPath(root, archive.RootRelativePath);
            var evidence = archive.Evidence with { CollectionRoot = archiveRoot };
            foreach (var source in evidence.Observations)
            {
                VerifyDependency(Path.Combine(archiveRoot, source.ReceiptRelativePath), source.ReceiptSha256);
                VerifyDependency(Path.Combine(archiveRoot, source.PayloadRelativePath), source.PayloadSha256);
            }
            return archive with { Evidence = evidence };
        }).ToImmutableArray();
        manifest = manifest with { Archives = rebased };
        if (manifest.QuerySemantics is { } semantics) VerifyDependency(semantics.EvidenceRelativePath, semantics.EvidenceSha256);
        HistoricalDiagnosticNewsManifestBuilder.VerifySemantics(root, manifest.QuerySemantics);
        HistoricalDiagnosticNewsManifestStore.Validate(manifest);
        CompanyNewsAssessmentManifest? assessments = null;
        if (manifest.CompanyAssessmentManifest is { } reference)
        {
            VerifyDependency(reference.ResearchRootRelativePath, reference.Sha256);
            assessments = CompanyNewsAssessmentManifestStore.ReadVerified(root, reference.ResearchRootRelativePath, reference.Sha256);
            foreach (var dependency in assessments.Dependencies) VerifyDependency(dependency.Path, dependency.Sha256);
            ValidateAssessmentArchiveBindings(manifest, assessments);
        }
        var ordered = dependencies.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var graph = HistoricalDiagnosticNewsManifestStore.ComputeDependencyGraphSha256(root, ordered);
        return new(manifest, HistoricalEvidenceManifestStore.Hash(manifestBytes), ordered, graph, assessments);
    }

    /// <summary>First selects the latest arrived revision, then its membership and original publication age; both news arms consume this same result.</summary>
    public ValueTask<HistoricalNewsPresenceContext> ResolveAsync(string symbol, DateTimeOffset decisionAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HistoricalEvidenceManifestStore.RequireUtc(decisionAtUtc);
        symbol = symbol.Trim().ToUpperInvariant();
        var unknown = !Manifest.SelectedSymbols.IsEmpty && !Manifest.SelectedSymbols.Contains(symbol, StringComparer.Ordinal)
            ? "symbol_outside_frozen_diagnostic_scope" : CoverageFailure(symbol, decisionAtUtc);
        var selected = ImmutableArray.CreateBuilder<HistoricalNewsPresenceRevision>();
        var candidates = articlesBySymbol.GetValueOrDefault(symbol) ?? [];
        foreach (var article in candidates.Concat(unscopedArticles).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = SelectVersion(article, symbol, decisionAtUtc);
            unknown ??= result.Unknown;
            if (result.Revision is { } revision)
            {
                assessmentsByRevision.TryGetValue((revision.Provider, revision.ArticleId, revision.ContentSha256, symbol), out var assessment);
                selected.Add(assessment is null ? revision : revision with { Assessment = assessment.Assessment });
            }
        }
        return ValueTask.FromResult(new HistoricalNewsPresenceContext(symbol, decisionAtUtc, Manifest.PolicyId,
            Manifest.PolicySha256, ManifestSha256, unknown is null, unknown,
            selected.OrderBy(item => item.Provider, StringComparer.Ordinal).ThenBy(item => item.ArticleId, StringComparer.Ordinal).ToImmutableArray(),
            companyAssessments?.Policy));
    }

    /// <summary>Proves every issuer measurement addresses exact retained raw text, identifiers and actual clocks.</summary>
    private static void ValidateAssessmentArchiveBindings(HistoricalDiagnosticNewsManifest manifest, CompanyNewsAssessmentManifest assessments)
    {
        var rawInputs = CompanyNewsAssessmentBuilder.ReadRawRevisions(manifest);
        var facts = manifest.Revisions.Select(revision => (revision,
            fact: manifest.Archives[revision.ArchiveIndex].Evidence.Facts.Single(item => item.FactId == revision.FactId)))
            .GroupBy(item => (item.revision.Provider, item.fact.SourceRecordId, item.fact.ContentSha256))
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var row in assessments.Rows)
        {
            var source = row.Source;
            if (!facts.TryGetValue((source.Revision.Provider, source.Revision.ProviderArticleId, source.RawRevisionContentSha256), out var matches))
                throw new InvalidDataException("Company assessment does not reference a frozen diagnostic news revision.");
            var match = matches.FirstOrDefault(item => item.fact.FactId == source.Revision.RevisionId);
            if (match.fact is null) throw new InvalidDataException("Company assessment revision ID differs from retained news.");
            var expected = rawInputs[(match.revision.ArchiveIndex, match.fact.FactId)];
            if (source.Revision != expected || row.Assessment.Revision != expected ||
                source.ActualReceivedAtUtc != match.fact.ActualReceivedAtUtc || source.PublishedAtUtc != match.fact.PublishedAtUtc ||
                source.ProviderUpdatedAtUtc != match.fact.UpdatedAtUtc || source.AssumedAvailableAtUtc != match.revision.AssumedAvailableAtUtc)
                throw new InvalidDataException("Company assessment text or source clocks differ from the exact archived revision.");
        }
    }

    /// <summary>Combines only verified adjacent query windows covering the complete publication lookback, including its decision boundary.</summary>
    private string? CoverageFailure(string symbol, DateTimeOffset decision)
    {
        var start = decision.AddHours(-HistoricalNewsDiagnosticPolicy.FreshnessHours);
        var intervals = Manifest.QueryCoverage.Where(item => item.Complete && item.Provider == "alpaca" &&
            (item.Symbols.IsEmpty || item.Symbols.Contains(symbol, StringComparer.Ordinal)) && item.StartUtc <= decision && item.EndUtc >= start)
            .OrderBy(item => item.StartUtc).ToArray();
        var covered = start;
        foreach (var interval in intervals)
        {
            if (interval.StartUtc > covered) break;
            if (interval.EndUtc > covered) covered = interval.EndUtc!.Value;
            // The query end can be exclusive; require evidence beyond the exact decision instant.
            if (covered > decision) return null;
        }
        return "provider_query_window_incomplete_or_unverified";
    }

    /// <summary>Duplicate exact content collapses; equal-update conflicts become unknown only after that revision's assumed arrival.</summary>
    private static (HistoricalNewsPresenceRevision? Revision, string? Unknown) SelectVersion(Article article, string symbol, DateTimeOffset decision)
    {
        var unclocked = article.Versions.FirstOrDefault(item => item.Derived.AssumedAvailableAtUtc is null &&
            (item.Symbols.Contains(symbol, StringComparer.Ordinal) || item.UncertainScope.Contains(symbol, StringComparer.Ordinal) ||
             item.Symbols.IsEmpty && item.UncertainScope.IsEmpty));
        if (unclocked is not null) return (null, unclocked.Derived.UnknownReason ?? "news_revision_availability_unknown");
        var arrived = article.Versions.Where(item => item.Derived.AssumedAvailableAtUtc <= decision).ToArray();
        if (arrived.Length == 0) return (null, null);
        var latest = arrived.Max(item => item.Fact.UpdatedAtUtc);
        var selected = arrived.Where(item => item.Fact.UpdatedAtUtc == latest).ToArray();
        var version = selected[0];
        if (selected.Any(item => item.Fact.ContentSha256 != version.Fact.ContentSha256))
            return (null, "news_same_update_revision_conflict");
        if (version.Derived.UnknownReason is { } defect) return (null, defect);
        if (!version.Symbols.Contains(symbol, StringComparer.Ordinal)) return (null, null);
        if (arrived.Select(item => item.Fact.PublishedAtUtc).Distinct().Count() != 1)
            return (null, "news_original_publication_conflict");
        var published = version.Fact.PublishedAtUtc!.Value;
        var age = decision - published;
        if (age < TimeSpan.Zero || age > TimeSpan.FromHours(HistoricalNewsDiagnosticPolicy.FreshnessHours)) return (null, null);
        var sources = selected.SelectMany(item => item.SourceHashes).Distinct().Order(StringComparer.Ordinal).ToImmutableArray();
        return (new(article.Provider, article.Id, version.Fact.FactId, version.Fact.ContentSha256,
            published, version.Fact.UpdatedAtUtc!.Value, selected.Min(item => item.Fact.ActualReceivedAtUtc),
            version.Derived.AssumedAvailableAtUtc!.Value, version.Symbols, sources), null);
    }

    /// <summary>Uses raw revision tags, keeping malformed scope separate from known membership and retaining all physical source hashes.</summary>
    private static IEnumerable<Version> LoadVersions(HistoricalDiagnosticNewsManifest manifest)
    {
        var facts = manifest.Archives.Select(archive => archive.Evidence.Facts.ToDictionary(item => item.FactId, StringComparer.Ordinal)).ToArray();
        var observations = manifest.Archives.Select(archive => archive.Evidence.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal)).ToArray();
        foreach (var derived in manifest.Revisions)
        {
            var fact = facts[derived.ArchiveIndex][derived.FactId];
            var sources = fact.SourceObservationIds.Select(id => observations[derived.ArchiveIndex][id]).ToArray();
            using var value = JsonDocument.Parse(fact.ValueJson);
            var validSymbols = value.RootElement.TryGetProperty("symbols", out var tags) && tags.ValueKind == JsonValueKind.Array &&
                tags.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && !String.IsNullOrWhiteSpace(item.GetString()));
            var symbols = validSymbols ? tags.EnumerateArray().Select(item => item.GetString()!.Trim().ToUpperInvariant())
                .Distinct().Order(StringComparer.Ordinal).ToImmutableArray() : [];
            var scope = symbols.IsEmpty && derived.UnknownReason is not null ? manifest.QueryCoverage
                .Where(item => item.SourceObservationIds.Any(id => fact.SourceObservationIds.Contains(id)))
                .SelectMany(item => item.Symbols).Distinct().Order(StringComparer.Ordinal).ToImmutableArray() : [];
            yield return new(fact, !validSymbols ? derived with { UnknownReason = derived.UnknownReason ?? "news_symbol_membership_unverified" } : derived,
                symbols, sources.SelectMany(item => new[] { item.ReceiptSha256, item.PayloadSha256 }).Distinct().Order(StringComparer.Ordinal).ToImmutableArray(), scope);
        }
    }
}

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingFlow.Data.Evidence.Historical;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence.Sentiment;

/// <summary>Source bytes and actual clocks remain separate from the historical availability assumption.</summary>
public sealed record CompanyNewsAssessmentSource(string WorkItemId, CompanyNewsRevisionInput Revision,
    string RawRevisionContentSha256, ImmutableArray<string> Symbols, DateTimeOffset? PublishedAtUtc,
    DateTimeOffset? ProviderUpdatedAtUtc, DateTimeOffset ActualReceivedAtUtc, DateTimeOffset? AssumedAvailableAtUtc);

public sealed record CompanyNewsAssessmentRow(CompanyNewsAssessmentSource Source, string Symbol,
    CompanyNewsAssessment Assessment);
public sealed record CompanyNewsAssessmentDependency(string Path, string Sha256);
public sealed record CompanyNewsAssessmentReference(string ResearchRootRelativePath, string Sha256);
public sealed record CompanyNewsAssessmentSelection(string Kind, string ResearchRootRelativePath, string Sha256);

/// <summary>A reusable immutable measurement packet; it never claims human accuracy or original classifier availability.</summary>
public sealed record CompanyNewsAssessmentManifest(string SchemaVersion, DateTimeOffset CreatedAtUtc,
    string ResearchRoot, HistoricalCompanyNewsAssessmentPolicy Policy, CompanyNewsPinnedModel Model,
    ImmutableArray<CompanyNewsAssessmentDependency> Dependencies, ImmutableArray<CompanyNewsAssessmentRow> Rows,
    string SelectionKind, CompanyNewsAssessmentSelection Selection,
    bool ClassifierAccuracyEvaluated = false, bool HistoricalClassifierAvailabilityProven = false,
    bool TradingActivationAllowed = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CompanyNewsIssuerMappingReference? IssuerMappingSource = null)
{
    public const string CurrentSchema = "company-news-assessment-manifest.v1";
    public const string MappedSchema = "company-news-assessment-manifest.v2";
}

/// <summary>Publishes new content addresses and verifies retained inputs without modifying the archives.</summary>
public static class CompanyNewsAssessmentManifestStore
{
    [ThreadStatic] private static HashSet<string>? activeSelections;
    public static JsonSerializerOptions JsonOptions => HistoricalEvidenceManifestStore.JsonOptions;

    /// <summary>Resolves an output beneath the frozen research root and rejects linked ancestors before inference or directory creation.</summary>
    public static string ResolvePublicationRoot(string researchRoot, string publicationRoot)
    {
        var full = Path.GetFullPath(publicationRoot);
        var prospectiveManifest = Path.Combine(full, "manifest.json");
        HistoricalEvidenceManifestStore.BoundPath(researchRoot, Path.GetRelativePath(researchRoot, prospectiveManifest));
        for (var ancestor = full; !String.IsNullOrEmpty(ancestor); ancestor = Path.GetDirectoryName(ancestor))
        {
            if (File.Exists(ancestor)) throw new InvalidDataException("Assessment publication output cannot traverse an existing file.");
            if (Directory.Exists(ancestor)) break;
        }
        return full;
    }

    /// <summary>Discovers only content-addressed JSON packets beneath the configured assessment publication root.</summary>
    public static IReadOnlyList<HistoricalDiagnosticNewsPublication> FindPublished(string publicationRoot) =>
        !Directory.Exists(publicationRoot) ? [] : Directory.EnumerateDirectories(publicationRoot)
            .Where(path => HistoricalEvidenceManifestStore.IsHash(Path.GetFileName(path)))
            .Select(path => new HistoricalDiagnosticNewsPublication(Path.GetFileName(path), Path.Combine(path, "company-assessments.json"), Path.GetFileName(path)))
            .Where(item => File.Exists(item.Path)).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();

    /// <summary>Reads a server-discovered publication and verifies its declared source dependencies before display.</summary>
    public static CompanyNewsAssessmentManifest ReadPublished(HistoricalDiagnosticNewsPublication publication)
    {
        if (publication.Id != publication.Sha256 || Path.GetFileName(Path.GetDirectoryName(publication.Path)) != publication.Sha256 ||
            Path.GetFileName(publication.Path) != "company-assessments.json")
            throw new InvalidDataException("Assessment publication path differs from its content address.");
        var manifest = ReadHashedPacket(publication.Path, publication.Sha256);
        HistoricalEvidenceManifestStore.BoundPath(manifest.ResearchRoot, Path.GetRelativePath(manifest.ResearchRoot, publication.Path));
        VerifyManifest(manifest.ResearchRoot, manifest);
        return manifest;
    }

    /// <summary>Writes one immutable JSON object; changed bytes always obtain a different path.</summary>
    public static HistoricalDiagnosticNewsPublication PublishExclusive(string publicationRoot, CompanyNewsAssessmentManifest manifest) =>
        PublishExclusiveAsync(publicationRoot, manifest).GetAwaiter().GetResult();

    /// <summary>Streams the unchanged packet into private staging and commits its content address only after a durable flush and hash.</summary>
    public static async Task<HistoricalDiagnosticNewsPublication> PublishExclusiveAsync(string publicationRoot,
        CompanyNewsAssessmentManifest manifest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        publicationRoot = ResolvePublicationRoot(manifest.ResearchRoot, publicationRoot);
        VerifyManifest(manifest.ResearchRoot, manifest);
        var staging = Path.Combine(publicationRoot, ".assessment-" + Guid.NewGuid().ToString("N") + ".stage");
        try
        {
            await AtomicFileArtifactWriter.Instance.WriteStreamExclusiveAsync(staging,
                (stream, token) => JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, token), cancellationToken).ConfigureAwait(false);
            var hash = NewsAssessmentHash(staging);
            var path = Path.Combine(publicationRoot, hash, "company-assessments.json");
            HistoricalEvidenceManifestStore.BoundPath(manifest.ResearchRoot, Path.GetRelativePath(manifest.ResearchRoot, path));
            CommitStaged(staging, path, cancellationToken);
            return new(hash, path, hash);
        }
        finally { DeleteOwnedStaging(staging); }
    }

    /// <summary>Rejects changed packets or source bytes before retrospective assessments enter a decision context.</summary>
    public static CompanyNewsAssessmentManifest ReadVerified(string researchRoot, string path, string expectedSha256)
    {
        var full = HistoricalEvidenceManifestStore.BoundPath(researchRoot,
            Path.IsPathFullyQualified(path) ? Path.GetRelativePath(researchRoot, path) : path);
        var manifest = ReadHashedPacket(full, expectedSha256);
        VerifyManifest(researchRoot, manifest);
        return manifest;
    }

    /// <summary>Hashes and rewinds the same read-shared handle so packet bytes need no retained full-document copy.</summary>
    private static CompanyNewsAssessmentManifest ReadHashedPacket(string full, string expectedSha256)
    {
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)) != expectedSha256)
            throw new InvalidDataException("Company assessment manifest differs from its frozen SHA256.");
        stream.Position = 0;
        return JsonSerializer.Deserialize<CompanyNewsAssessmentManifest>(stream, JsonOptions)
            ?? throw new InvalidDataException("Company assessment manifest is empty.");
    }

    /// <summary>Runs every unchanged row, source, mapping and clock check once for the already parsed packet.</summary>
    private static void VerifyManifest(string researchRoot, CompanyNewsAssessmentManifest manifest)
    {
        Validate(manifest);
        VerifyDependencies(researchRoot, manifest);
        VerifySourceSelection(researchRoot, manifest);
    }

    /// <summary>Requires explicit retrospective limitations, one frozen model and unique issuer/revision measurements.</summary>
    public static void Validate(CompanyNewsAssessmentManifest manifest)
    {
        if (manifest.SchemaVersion != (manifest.IssuerMappingSource is null ? CompanyNewsAssessmentManifest.CurrentSchema : CompanyNewsAssessmentManifest.MappedSchema) ||
            !manifest.Policy.IsComplete() ||
            manifest.Dependencies.IsDefaultOrEmpty || manifest.Rows.IsDefaultOrEmpty ||
            manifest.ClassifierAccuracyEvaluated || manifest.HistoricalClassifierAvailabilityProven || manifest.TradingActivationAllowed ||
            !Path.IsPathFullyQualified(manifest.ResearchRoot) || String.IsNullOrWhiteSpace(manifest.SelectionKind))
            throw new InvalidDataException("Company assessment packet requires frozen provenance and explicit diagnostic limitations.");
        if (manifest.Policy.MappingVersion != (manifest.IssuerMappingSource is null ? CompanyNewsAssessmentClassifier.MappingVersion : CompanyNewsAssessmentClassifier.MappedMappingVersion) ||
            manifest.Policy.ExtractionVersion != (manifest.IssuerMappingSource is null ? CompanyNewsAssessmentClassifier.ExtractionVersion : CompanyNewsAssessmentClassifier.MappedExtractionVersion))
            throw new InvalidDataException("Company assessment package method versions differ from its frozen issuer mapping source.");
        if (manifest.Selection is null || manifest.Selection.Kind is not ("pilot" or "diagnostic") ||
            !HistoricalEvidenceManifestStore.IsHash(manifest.Selection.Sha256) ||
            !manifest.Dependencies.Any(item => item.Path == manifest.Selection.ResearchRootRelativePath && item.Sha256 == manifest.Selection.Sha256))
            throw new InvalidDataException("Company assessment requires the exact retained selection reference among its frozen dependencies.");
        HistoricalEvidenceManifestStore.RequireUtc(manifest.CreatedAtUtc);
        if (manifest.Dependencies.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Dependencies.Length)
            throw new InvalidDataException("Repeated assessment dependency paths are not allowed.");
        foreach (var dependency in manifest.Dependencies)
            if (!HistoricalEvidenceManifestStore.IsHash(dependency.Sha256)) throw new InvalidDataException("Invalid assessment dependency hash.");
        var keys = new HashSet<(string, string, string, string)>();
        foreach (var row in manifest.Rows)
        {
            HistoricalEvidenceManifestStore.RequireUtc(row.Source.ActualReceivedAtUtc);
            if (row.Source.PublishedAtUtc is { } published) HistoricalEvidenceManifestStore.RequireUtc(published);
            if (row.Source.ProviderUpdatedAtUtc is { } updated) HistoricalEvidenceManifestStore.RequireUtc(updated);
            if (row.Source.AssumedAvailableAtUtc is { } assumed) HistoricalEvidenceManifestStore.RequireUtc(assumed);
            if (!HistoricalEvidenceManifestStore.IsHash(row.Source.RawRevisionContentSha256) ||
                !row.Source.Symbols.Contains(row.Symbol, StringComparer.Ordinal) ||
                !keys.Add((row.Source.Revision.Provider, row.Source.Revision.ProviderArticleId, row.Source.RawRevisionContentSha256, row.Symbol)))
                throw new InvalidDataException("Assessment issuer/revision binding is missing or duplicated.");
            var assessment = row.Assessment;
            if (!manifest.Policy.AllowsExtraction(assessment.Extraction) || assessment.Revision != row.Source.Revision || assessment.IssuerSymbol != row.Symbol ||
                assessment.RevisionContentSha256 != CompanyNewsAssessmentClassifier.ComputeRevisionContentSha256(row.Source.Revision) ||
                assessment.AssessedAtUtc < row.Source.ActualReceivedAtUtc || assessment.AssessedAtUtc > manifest.CreatedAtUtc ||
                assessment.Extraction.RuleResult.ClassifiedAtUtc < row.Source.ActualReceivedAtUtc ||
                manifest.Policy.ModelIdentity != manifest.Model.Model || manifest.Policy.ModelProvider != manifest.Model.Provider ||
                manifest.Policy.ModelVersion != manifest.Model.Version || manifest.Policy.ModelArtifactSha256 != manifest.Model.ArtifactSha256 ||
                manifest.Policy.TrainingDataProvenance != manifest.Model.TrainingDataProvenance ||
                manifest.Policy.ModelTrainingDataCutoffUtc != manifest.Model.TrainingDataCutoffUtc)
                throw new InvalidDataException("Company assessment model, exact text or actual clocks differ from the packet policy.");
            var recomputed = CompanyNewsAssessmentClassifier.Complete(assessment.Extraction, assessment.ModelEvidence,
                manifest.Model, assessment.AssessedAtUtc);
            if (!CompanyNewsAssessmentContentComparer.SameContent(recomputed, assessment, JsonOptions))
                throw new InvalidDataException("Stored assessment state differs from the frozen extraction/model agreement rule.");
        }
    }

    /// <summary>Verifies each declared original file under the selected root without following links or changing source bytes.</summary>
    private static void VerifyDependencies(string researchRoot, CompanyNewsAssessmentManifest manifest)
    {
        foreach (var dependency in manifest.Dependencies)
        {
            var dependencyPath = HistoricalEvidenceManifestStore.BoundPath(researchRoot, dependency.Path);
            if (NewsAssessmentHash(dependencyPath) != dependency.Sha256)
                throw new InvalidDataException("Company assessment source changed: " + dependency.Path);
        }
    }

    /// <summary>Rederives archived source rows from their original selection so self-consistent invented text cannot pass preview verification.</summary>
    private static void VerifySourceSelection(string root, CompanyNewsAssessmentManifest manifest)
    {
        var reference = manifest.Selection;
        var path = HistoricalEvidenceManifestStore.BoundPath(root, reference.ResearchRootRelativePath);
        activeSelections ??= new(StringComparer.OrdinalIgnoreCase);
        if (activeSelections.Count >= 32 || !activeSelections.Add(path))
            throw new InvalidDataException("Assessment source selections contain a cycle or exceed the bounded provenance depth.");
        try { VerifySourceRows(root, path, manifest); }
        finally { activeSelections.Remove(path); }
    }

    /// <summary>Compares every measured issuer against freshly derived source text, symbol associations and original clocks.</summary>
    private static void VerifySourceRows(string root, string path, CompanyNewsAssessmentManifest manifest)
    {
        var reference = manifest.Selection;
        var selection = reference.Kind == "pilot"
            ? CompanyNewsAssessmentBuilder.ReadPilot(root, path, reference.Sha256)
            : CompanyNewsAssessmentBuilder.ReadDiagnostic(root, path, reference.Sha256);
        RequireFrozenSelectionDependencies(root, manifest, selection.Dependencies);
        var expected = selection.Sources.SelectMany(source => source.Symbols.Distinct(StringComparer.Ordinal)
            .Select(symbol => (Key: (source.Revision.Provider, source.Revision.ProviderArticleId, source.RawRevisionContentSha256, symbol), Source: source)))
            .ToDictionary(item => item.Key, item => item.Source);
        if (expected.Count != manifest.Rows.Length)
            throw new InvalidDataException("Company assessment rows differ from the original selected issuer/revision population.");
        var mappings = ReadIssuerMappings(root, manifest, selection.Sources.SelectMany(source => source.Symbols));
        foreach (var row in manifest.Rows)
        {
            if (!expected.TryGetValue((row.Source.Revision.Provider, row.Source.Revision.ProviderArticleId, row.Source.RawRevisionContentSha256, row.Symbol), out var source) ||
                !CompanyNewsAssessmentContentComparer.SameContent(source, row.Source, JsonOptions))
                throw new InvalidDataException("Company assessment source text, clocks or symbols differ from the exact archived selection.");
            var mapping = mappings?.Mappings.GetValueOrDefault(row.Symbol);
            if (row.Assessment.Extraction.IssuerMapping != mapping)
                throw new InvalidDataException("Company assessment name mapping differs from verified raw source rows or uses a conflicting alias.");
        }
    }

    /// <summary>Requires every source file actually rederived by selection replay to belong to the declared leased graph.</summary>
    private static void RequireFrozenSelectionDependencies(string root, CompanyNewsAssessmentManifest manifest,
        ImmutableArray<CompanyNewsAssessmentDependency> dependencies)
    {
        var frozen = manifest.Dependencies.ToDictionary(item => HistoricalEvidenceManifestStore.BoundPath(root, item.Path),
            item => item.Sha256, StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in dependencies)
        {
            var full = HistoricalEvidenceManifestStore.BoundPath(root, dependency.Path);
            if (!frozen.TryGetValue(full, out var hash) || hash != dependency.Sha256)
                throw new InvalidDataException("Company assessment selection source graph is not fully frozen in the assessment packet.");
        }
    }

    /// <summary>Rederives provider names and the full receipt/payload graph before trusting any mapped extraction or literal fallback.</summary>
    private static CompanyNewsIssuerMappingReadResult? ReadIssuerMappings(string root, CompanyNewsAssessmentManifest manifest,
        IEnumerable<string> selectedNewsSymbols)
    {
        if (manifest.IssuerMappingSource is not { } source) return null;
        var path = HistoricalEvidenceManifestStore.BoundPath(root, source.ResearchRootRelativePath);
        var mappings = CompanyNewsIssuerMappingReader.ReadVerified(root, path, source.Sha256, selectedNewsSymbols);
        var frozen = manifest.Dependencies.ToDictionary(item => item.Path, item => item.Sha256, StringComparer.OrdinalIgnoreCase);
        if (mappings.Dependencies.Any(item => !frozen.TryGetValue(item.Path, out var hash) || hash != item.Sha256))
            throw new InvalidDataException("Issuer mapping source graph is not fully frozen in the assessment packet.");
        return mappings;
    }

    /// <summary>Permits identical retries and never overwrites a previously published file.</summary>
    public static void WriteExclusive(string path, byte[] bytes) =>
        WriteStreamExclusiveAsync(path, (stream, token) => stream.WriteAsync(bytes, token).AsTask()).GetAwaiter().GetResult();

    /// <summary>Publishes reports using the same private atomic staging and identical-retry rules as assessment packets.</summary>
    public static async Task WriteStreamExclusiveAsync(string path, Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        var staging = Path.Combine(Path.GetDirectoryName(full)!, ".assessment-" + Guid.NewGuid().ToString("N") + ".stage");
        try
        {
            await AtomicFileArtifactWriter.Instance.WriteStreamExclusiveAsync(staging, write, cancellationToken).ConfigureAwait(false);
            CommitStaged(staging, full, cancellationToken);
        }
        finally { DeleteOwnedStaging(staging); }
    }

    /// <summary>Moves only complete staging files; concurrent identical publication is reusable and conflicting bytes never overwrite.</summary>
    private static void CommitStaged(string staging, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { File.Move(staging, path, overwrite: false); }
        catch (IOException) when (File.Exists(path))
        {
            if (!SameFileBytes(staging, path, cancellationToken)) throw new IOException("Immutable assessment output already exists: " + path);
        }
    }

    /// <summary>Compares existing committed bytes in fixed buffers rather than allocating a second full packet.</summary>
    private static bool SameFileBytes(string leftPath, string rightPath, CancellationToken cancellationToken)
    {
        using var left = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var right = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (left.Length != right.Length) return false;
        var leftBuffer = new byte[16 * 1024];
        var rightBuffer = new byte[leftBuffer.Length];
        while (left.Position < left.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(leftBuffer.Length, left.Length - left.Position);
            left.ReadExactly(leftBuffer.AsSpan(0, count));
            right.ReadExactly(rightBuffer.AsSpan(0, count));
            if (!leftBuffer.AsSpan(0, count).SequenceEqual(rightBuffer.AsSpan(0, count))) return false;
        }
        return true;
    }

    /// <summary>Removes only this operation's uniquely named staging artifact, including after cancellation or publication failure.</summary>
    private static void DeleteOwnedStaging(string staging)
    {
        if (File.Exists(staging)) File.Delete(staging);
    }

    /// <summary>Hashes a retained file without loading large provider archives into memory.</summary>
    private static string NewsAssessmentHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }
}

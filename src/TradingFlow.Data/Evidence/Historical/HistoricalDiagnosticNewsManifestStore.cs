using System.Collections.Immutable;
using System.Text.Json;
using TradingFlow.Data.Evidence.Sentiment;

namespace TradingFlow.Data.Evidence.Historical;

public sealed record HistoricalDiagnosticNewsPublication(string Id, string Path, string Sha256);

/// <summary>Content-addressed immutable package publication; original archives are never written or expired.</summary>
public static class HistoricalDiagnosticNewsManifestStore
{
    /// <summary>Writes a new package only; identical existing bytes are reusable and changed bytes never overwrite them.</summary>
    public static HistoricalDiagnosticNewsPublication PublishExclusive(string publicationRoot, HistoricalDiagnosticNewsManifest manifest)
    {
        Validate(manifest);
        HistoricalDiagnosticNewsManifestBuilder.VerifySemantics(manifest.ResearchRoot, manifest.QuerySemantics);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, HistoricalEvidenceManifestStore.JsonOptions);
        var hash = HistoricalEvidenceManifestStore.Hash(bytes);
        var directory = Path.Combine(publicationRoot, hash);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "manifest.json");
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Published diagnostic identity has different bytes.");
            return new(hash, path, hash);
        }
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(true);
        return new(hash, path, hash);
    }

    /// <summary>Discovers only content-addressed packages under the server-owned publication root.</summary>
    public static IReadOnlyList<HistoricalDiagnosticNewsPublication> FindPublished(string publicationRoot)
    {
        if (!Directory.Exists(publicationRoot)) return [];
        return Directory.EnumerateDirectories(publicationRoot).Where(path => HistoricalEvidenceManifestStore.IsHash(Path.GetFileName(path)))
            .Select(path => new HistoricalDiagnosticNewsPublication(Path.GetFileName(path), Path.Combine(path, "manifest.json"), Path.GetFileName(path)))
            .Where(item => File.Exists(item.Path)).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Reads only the small immutable manifest for catalog display; source dependency verification remains mandatory on binding.</summary>
    public static HistoricalDiagnosticNewsManifest ReadPublished(HistoricalDiagnosticNewsPublication publication)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(publication.Path))!;
        if (publication.Id != publication.Sha256 || Path.GetFileName(directory) != publication.Sha256 ||
            Path.GetFileName(publication.Path) != "manifest.json" || !HistoricalEvidenceManifestStore.IsHash(publication.Sha256))
            throw new InvalidDataException("Diagnostic publication identity does not match its content-addressed path.");
        var bytes = HistoricalEvidenceManifestStore.ReadBound(directory, "manifest.json");
        if (HistoricalEvidenceManifestStore.Hash(bytes) != publication.Sha256) throw new InvalidDataException("Diagnostic publication bytes changed.");
        var manifest = JsonSerializer.Deserialize<HistoricalDiagnosticNewsManifest>(bytes, HistoricalEvidenceManifestStore.JsonOptions)
            ?? throw new InvalidDataException("Diagnostic publication is empty.");
        if (manifest.SchemaVersion != HistoricalDiagnosticNewsManifest.CurrentSchema || !Path.IsPathFullyQualified(manifest.ResearchRoot) ||
            manifest.QueryCoverage.IsDefault || manifest.Revisions.IsDefault || manifest.Archives.IsDefaultOrEmpty || manifest.SelectedSymbols.IsDefault)
            throw new InvalidDataException("Diagnostic publication metadata is incomplete.");
        return manifest;
    }

    /// <summary>Computes the declared graph for selection display without rereading raw bytes; full verification is still mandatory on binding.</summary>
    public static string DeclaredDependencyGraphSha256(HistoricalDiagnosticNewsPublication publication,
        HistoricalDiagnosticNewsManifest manifest) =>
        ComputeDependencyGraphSha256(Path.GetFullPath(manifest.ResearchRoot), DeclaredDependencies(publication, manifest));

    /// <summary>Discovers declarations only; callers must lease this exact set and run the complete source verifier before reuse.</summary>
    public static IReadOnlyList<HistoricalDiagnosticNewsDependency> DeclaredDependencies(HistoricalDiagnosticNewsPublication publication,
        HistoricalDiagnosticNewsManifest manifest)
    {
        var root = Path.GetFullPath(manifest.ResearchRoot);
        var dependencies = new Dictionary<string, HistoricalDiagnosticNewsDependency>(StringComparer.OrdinalIgnoreCase);
        // Normalize paths and hashes exactly as the verified loader does, rejecting contradictory declarations.
        void Add(string path, string hash)
        {
            if (!HistoricalEvidenceManifestStore.IsHash(hash)) throw new InvalidDataException("Diagnostic dependency SHA-256 is invalid.");
            var relative = Path.IsPathFullyQualified(path) ? Path.GetRelativePath(root, path) : path;
            var full = HistoricalEvidenceManifestStore.BoundPath(root, relative);
            hash = hash.ToLowerInvariant();
            if (dependencies.TryGetValue(full, out var existing) && existing.Sha256 != hash)
                throw new InvalidDataException("Diagnostic dependency path has contradictory declared hashes.");
            dependencies[full] = new(full, hash);
        }
        Add(publication.Path, publication.Sha256);
        foreach (var archive in manifest.Archives)
        {
            var archiveRoot = archive.RootRelativePath == "." ? root : HistoricalEvidenceManifestStore.BoundPath(root, archive.RootRelativePath);
            foreach (var source in archive.Evidence.Observations)
            {
                Add(Path.Combine(archiveRoot, source.ReceiptRelativePath), source.ReceiptSha256);
                Add(Path.Combine(archiveRoot, source.PayloadRelativePath), source.PayloadSha256);
            }
        }
        if (manifest.QuerySemantics is { } semantics) Add(semantics.EvidenceRelativePath, semantics.EvidenceSha256);
        if (manifest.CompanyAssessmentManifest is { } assessmentReference)
        {
            Add(assessmentReference.ResearchRootRelativePath, assessmentReference.Sha256);
            foreach (var dependency in CompanyNewsAssessmentDependencyReader.ReadDeclarations(root,
                assessmentReference.ResearchRootRelativePath, assessmentReference.Sha256)) Add(dependency.Path, dependency.Sha256);
        }
        return dependencies.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Shares one canonical root-relative path ordering for declared and verified dependency graph identities.</summary>
    internal static string ComputeDependencyGraphSha256(string root, IEnumerable<HistoricalDiagnosticNewsDependency> dependencies) =>
        HistoricalEvidenceManifestStore.Hash(JsonSerializer.SerializeToUtf8Bytes(dependencies.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => new { Path = Path.GetRelativePath(root, item.Path).Replace('\\', '/'), item.Sha256 })));

    /// <summary>Validates policy, actual receipts, complete query derivation, and derived assumed clocks before admission.</summary>
    internal static void Validate(HistoricalDiagnosticNewsManifest manifest)
    {
        if (manifest.SchemaVersion != HistoricalDiagnosticNewsManifest.CurrentSchema || !Path.IsPathFullyQualified(manifest.ResearchRoot) || manifest.Archives.IsDefaultOrEmpty ||
            manifest.QueryCoverage.IsDefault || manifest.Revisions.IsDefault || manifest.SelectedSymbols.IsDefault ||
            manifest.PolicyId != HistoricalReconstructionPolicy.ConservativeV1.ContractId ||
            manifest.PolicySha256 != HistoricalEvidenceResolver.PolicySha256(HistoricalReconstructionPolicy.ConservativeV1))
            throw new InvalidDataException("Diagnostic package requires its exact schema, frozen policy and initialized evidence.");
        HistoricalEvidenceManifestStore.RequireUtc(manifest.BuiltAtUtc);
        ValidateSourceCompleteness(manifest);
        foreach (var archive in manifest.Archives)
        {
            HistoricalEvidenceManifestStore.Validate(archive.Evidence);
            if (archive.Evidence.BuiltAtUtc > manifest.BuiltAtUtc) throw new InvalidDataException("Diagnostic package precedes a bound archive.");
        }
        RequireSame(manifest.Revisions, HistoricalDiagnosticNewsManifestBuilder.BuildRevisions(manifest.Archives), "revision availability");
        RequireSame(manifest.QueryCoverage, HistoricalDiagnosticNewsManifestBuilder.BuildCoverage(manifest.Archives, manifest.QuerySemantics), "query coverage");
    }

    /// <summary>Rederives complete scoped articles and captured sessions from retained receipts, so omissions or edited calendar opens cannot certify no news.</summary>
    private static void ValidateSourceCompleteness(HistoricalDiagnosticNewsManifest manifest)
    {
        var originals = manifest.Archives.Select(archive => HistoricalEvidenceManifestBuilder.BuildFromReceiptPaths(
            archive.Evidence.CollectionRoot, archive.Evidence.Observations.Select(source => source.ReceiptRelativePath),
            archive.Evidence.BuiltAtUtc)).ToArray();
        var scoped = HistoricalDiagnosticNewsManifestBuilder.ScopeArticles(originals, manifest.SelectedSymbols).ToArray();
        for (var index = 0; index < scoped.Length; index++)
        {
            var supplied = manifest.Archives[index].Evidence;
            var expected = scoped[index];
            RequireSame(supplied.Observations, expected.Observations, "complete raw observation set");
            RequireSame(supplied.Facts, expected.Facts, "complete selected revision set");
            RequireSame(supplied.Sessions, expected.Sessions, "captured exchange sessions");
            if (supplied.CalendarObservationId != expected.CalendarObservationId)
                throw new InvalidDataException("Diagnostic calendar observation differs from its captured source.");
        }
    }

    /// <summary>Compares exact serialized derived values so a hand-edited passed flag cannot certify evidence.</summary>
    private static void RequireSame<T>(ImmutableArray<T> supplied, ImmutableArray<T> derived, string description)
    {
        if (!JsonSerializer.SerializeToUtf8Bytes(supplied, HistoricalEvidenceManifestStore.JsonOptions).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(derived, HistoricalEvidenceManifestStore.JsonOptions)))
            throw new InvalidDataException("Diagnostic " + description + " differs from its retained source derivation.");
    }
}

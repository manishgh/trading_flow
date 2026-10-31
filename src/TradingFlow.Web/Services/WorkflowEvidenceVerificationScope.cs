using System.Text.Json;
using TradingFlow.Application.Jobs;
using TradingFlow.Backtesting;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Evidence.Historical;
using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Web.Services;

/// <summary>Owns one complete diagnostic replay and its exact read leases for one Start or durable worker attempt.</summary>
internal sealed class WorkflowEvidenceVerificationScope : IWorkflowEvidenceVerificationScope
{
    private readonly object owner;
    private readonly EngineConfig identity;
    private DurableFileReadLease? lease;
    private FrozenBacktestEvidence? evidence;
    internal CompanyNewsAssessmentSummary? AssessmentSummary { get; private set; }
    internal Action<EngineConfig> Guard { get; }

    // Construction is private so an application token or a supplied hash cannot grant verified status.
    private WorkflowEvidenceVerificationScope(object owner, EngineConfig identity, DurableFileReadLease lease,
        FrozenBacktestEvidence evidence)
    {
        this.owner = owner;
        this.identity = identity;
        this.lease = lease;
        this.evidence = evidence;
        Guard = RequireLive;
        evidence.BindVerificationScope(Guard);
        var provider = (HistoricalDiagnosticNewsProvider)evidence.HistoricalNewsProvider!;
        AssessmentSummary = provider.CompanyAssessments is { } assessment ? CompanyNewsAssessmentPreviewService.Summarize(assessment) : null;
    }

    // Strict and unclassified evidence retain their existing fresh-verification path.
    internal static WorkflowEvidenceVerificationScope? Open(object owner, EngineConfig config)
    {
        if (String.IsNullOrWhiteSpace(config.EvidenceManifestPath)) return null;
        var root = RequireRoot(config.EvidenceResearchRoot);
        var path = BoundPath(root, config.EvidenceManifestPath);
        using (var stream = File.OpenRead(path))
        using (var document = JsonDocument.Parse(stream))
        {
            var schemas = document.RootElement.EnumerateObject().Where(property =>
                property.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (schemas.Length != 1 || schemas[0].Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Evidence manifest requires one explicit schema version.");
            if (schemas[0].Value.GetString() != HistoricalDiagnosticNewsManifest.CurrentSchema) return null;
        }
        var hash = config.EvidenceManifestSha256 ?? throw new InvalidDataException("Diagnostic scope requires a frozen manifest hash.");
        var publication = new HistoricalDiagnosticNewsPublication(hash, path, hash);
        var manifest = HistoricalDiagnosticNewsManifestStore.ReadPublished(publication);
        if (manifest.CompanyAssessmentManifest is null) return null;
        if (!RequireRoot(manifest.ResearchRoot).Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Diagnostic manifest differs from its explicit research root.");
        var declared = HistoricalDiagnosticNewsManifestStore.DeclaredDependencies(publication, manifest);
        var expected = declared.Select(item => new DurableFileEvidence(BoundPath(root, item.Path),
            new FileInfo(item.Path).Length, item.Sha256)).ToArray();
        var held = new DurableFileJobRequest(1, DateTimeOffset.UtcNow, expected).AcquireVerifiedReadLease();
        try
        {
            foreach (var dependency in declared) BoundPath(root, dependency.Path);
            var verified = FrozenBacktestEvidence.FromConfig(config)
                ?? throw new InvalidDataException("Diagnostic scope has no verified evidence.");
            var actual = verified.Dependencies.ToDictionary(item => Path.GetFullPath(item.Path), item => item.Sha256,
                StringComparer.OrdinalIgnoreCase);
            if (actual.Count != expected.Length || expected.Any(item => !actual.TryGetValue(item.Path, out var actualHash) ||
                    !actualHash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Verified diagnostic graph differs from the exact leased declarations.");
            var identity = config with { EvidenceResearchRoot = root, EvidenceManifestPath = path,
                EvidenceDependencyGraphSha256 = verified.DependencyGraphSha256 };
            return new(owner, identity, held, verified);
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    // Foreign configuration owners cannot borrow another operation's opaque token.
    internal void RequireOwner(object expectedOwner)
    {
        if (!ReferenceEquals(owner, expectedOwner)) throw new InvalidOperationException("Evidence verification scope belongs to another operation owner.");
        RequireLive(identity);
    }

    // Return the existing provider only after the same guard used by resolver and nested runner boundaries.
    internal FrozenBacktestEvidence Resolve(EngineConfig config)
    {
        RequireLive(config);
        return evidence!;
    }

    // Leases establish byte identity; every reuse also rechecks current path/link restrictions and full configuration identity.
    internal void RequireLive(EngineConfig config)
    {
        ObjectDisposedException.ThrowIf(lease is null || evidence is null, this);
        evidence.RequireConfiguredIdentity(config);
        var root = RequireRoot(config.EvidenceResearchRoot);
        foreach (var dependency in evidence.Dependencies) BoundPath(root, dependency.Path);
    }

    // The factory supplies the pinned graph to subsequent generated configurations.
    internal EngineConfig Bind(EngineConfig execution)
    {
        var configured = execution with { EvidenceDependencyGraphSha256 = identity.EvidenceDependencyGraphSha256 };
        RequireLive(configured);
        return configured;
    }

    // Require explicit absolute roots; relative defaults cannot identify an operation's retained graph.
    private static string RequireRoot(string? root) => !String.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root)
        ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
        : throw new InvalidDataException("Evidence verification scope requires an explicit absolute research root.");

    // Match the existing evidence reader's textual and reparse restrictions without reparsing retained packet rows.
    private static string BoundPath(string root, string path)
    {
        var full = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Evidence scope dependency escapes its explicit research root.");
        for (var current = full; current.Length >= root.Length; current = Path.GetDirectoryName(current) ?? "")
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Evidence scope paths cannot traverse symbolic links or reparse points.");
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return full;
    }

    // Disposal occurs after operation children drain and releases both the retained packet and original read leases.
    public void Dispose()
    {
        var held = lease;
        lease = null;
        evidence = null;
        AssessmentSummary = null;
        held?.Dispose();
    }
}

using System.Text.Json;
using TradingFlow.Data.Evidence.Historical;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;

namespace TradingFlow.Backtesting;

/// <summary>Dispatches immutable saved-plan evidence without allowing a diagnostic manifest into the strict provider.</summary>
public sealed class FrozenBacktestEvidence
{
    public IStrategyDecisionContextProvider? StrictContextProvider { get; }
    public IHistoricalNewsPresenceProvider? HistoricalNewsProvider { get; }
    public IReadOnlyList<FrozenDecisionContextDependency> Dependencies { get; }
    public string ManifestSha256 { get; }
    public string DependencyGraphSha256 { get; }
    public DecisionContextEvidenceProvenance Provenance { get; }
    private readonly string researchRoot;
    private readonly string manifestPath;
    private readonly string schema;
    private Action<EngineConfig>? verificationScopeGuard;

    // Only a complete provider load can create reusable evidence; caller hashes cannot manufacture this proof.
    private FrozenBacktestEvidence(IStrategyDecisionContextProvider? strict, IHistoricalNewsPresenceProvider? historical,
        IReadOnlyList<FrozenDecisionContextDependency> dependencies, string hash, string graph,
        DecisionContextEvidenceProvenance provenance, EngineConfig config, string schema)
    {
        StrictContextProvider = strict;
        HistoricalNewsProvider = historical;
        Dependencies = Array.AsReadOnly(dependencies.ToArray());
        ManifestSha256 = hash;
        DependencyGraphSha256 = graph;
        Provenance = provenance;
        researchRoot = CanonicalRoot(config.EvidenceResearchRoot!);
        manifestPath = CanonicalManifest(config, researchRoot);
        this.schema = schema;
    }

    // Reuse only an exact configured identity while its Web owner proves the original graph lease is still alive.
    public static FrozenBacktestEvidence FromConfig(EngineConfig config, FrozenBacktestEvidence verified,
        Action<EngineConfig> requireLiveScope)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(requireLiveScope);
        if (!ReferenceEquals(verified.verificationScopeGuard, requireLiveScope))
            throw new InvalidOperationException("Evidence reuse requires the exact guard bound by its server verification scope.");
        requireLiveScope(config);
        verified.RequireConfiguredIdentity(config);
        return verified;
    }

    // Only the Web scope factory may bind one lifetime guard after complete verification under its original graph lease.
    internal void BindVerificationScope(Action<EngineConfig> requireLiveScope)
    {
        ArgumentNullException.ThrowIfNull(requireLiveScope);
        if (Interlocked.CompareExchange(ref verificationScopeGuard, requireLiveScope, null) is not null)
            throw new InvalidOperationException("Verified evidence already belongs to a verification scope.");
    }

    // Configuration identity includes root and path, not merely matching content in another location.
    public void RequireConfiguredIdentity(EngineConfig config)
    {
        if (String.IsNullOrWhiteSpace(config.EvidenceResearchRoot) || !Path.IsPathFullyQualified(config.EvidenceResearchRoot) ||
            String.IsNullOrWhiteSpace(config.EvidenceManifestPath) ||
            !CanonicalRoot(config.EvidenceResearchRoot).Equals(researchRoot, StringComparison.OrdinalIgnoreCase) ||
            !CanonicalManifest(config, researchRoot).Equals(manifestPath, StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(config.EvidenceManifestSha256, ManifestSha256, StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(config.EvidenceDependencyGraphSha256, DependencyGraphSha256, StringComparison.OrdinalIgnoreCase) ||
            schema != HistoricalDiagnosticNewsManifest.CurrentSchema ||
            HistoricalNewsProvider is not HistoricalDiagnosticNewsProvider provider || provider.Manifest.SchemaVersion != schema)
            throw new InvalidDataException("Reusable evidence differs from the complete configured diagnostic identity.");
    }

    // Normalize equivalent explicit root spellings without allowing another directory to borrow this verification.
    private static string CanonicalRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    // Resolve the configured manifest against its own explicit root before matching the verified path.
    private static string CanonicalManifest(EngineConfig config, string root) => Path.GetFullPath(
        Path.IsPathFullyQualified(config.EvidenceManifestPath!) ? config.EvidenceManifestPath! : Path.Combine(root, config.EvidenceManifestPath!));

    // Load the selected schema under the same explicit root and immutable hashes used by durable capture.
    public static FrozenBacktestEvidence? FromConfig(EngineConfig config)
    {
        if (String.IsNullOrWhiteSpace(config.EvidenceManifestPath))
        {
            _ = FrozenManifestDecisionContextProvider.FromConfig(config);
            return null;
        }
        if (String.IsNullOrWhiteSpace(config.EvidenceResearchRoot) || !Path.IsPathFullyQualified(config.EvidenceResearchRoot))
            throw new InvalidDataException("Evidence manifest requires an explicit absolute research root.");
        var schema = ReadSchema(config.EvidenceResearchRoot, config.EvidenceManifestPath);
        if (schema == FrozenManifestDecisionContextProvider.SchemaVersion)
        {
            var strict = (FrozenManifestDecisionContextProvider)FrozenManifestDecisionContextProvider.FromConfig(config)!;
            return new(strict, null, strict.Dependencies, strict.ManifestSha256,
                strict.DependencyGraphSha256, strict.Provenance, config, schema);
        }
        if (schema != HistoricalDiagnosticNewsManifest.CurrentSchema)
            throw new InvalidDataException("Unsupported frozen backtest evidence schema.");
        return LoadDiagnostic(config);
    }

    // Inspect only the schema through the existing bounded reader; the selected provider verifies all parsed bytes.
    private static string ReadSchema(string researchRoot, string manifestPath)
    {
        using var document = JsonDocument.Parse(FrozenManifestDecisionContextProvider.ReadBoundFile(
            Path.GetFullPath(researchRoot), manifestPath));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Evidence manifest must be an object with an explicit schema version.");
        var schema = document.RootElement.EnumerateObject()
            .Where(item => item.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (schema.Length != 1 || schema[0].Value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Evidence manifest requires one explicit schema version.");
        return schema[0].Value.GetString()!;
    }

    // Bind the diagnostic dependency graph and annotate that strict provider/classifier evidence was not consumed.
    private static FrozenBacktestEvidence LoadDiagnostic(EngineConfig config)
    {
        if (config.EvidenceManifestSha256 is not { } hash)
            throw new InvalidDataException("Historical news diagnostic requires the selected immutable manifest SHA-256.");
        var diagnostic = HistoricalDiagnosticNewsProvider.LoadVerified(config.EvidenceResearchRoot!,
            config.EvidenceManifestPath!, hash);
        if (config.EvidenceDependencyGraphSha256 is { } graph && !graph.Equals(diagnostic.DependencyGraphSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Historical news diagnostic dependency graph changed after selection.");
        var dependencies = diagnostic.Dependencies.Select(item => new FrozenDecisionContextDependency(item.Path, item.Sha256)).ToArray();
        var provenance = new DecisionContextEvidenceProvenance(diagnostic.ManifestSha256,
            diagnostic.DependencyGraphSha256, "alpaca", HistoricalNewsDiagnosticPolicy.PolicyId,
            dependencies.Where(item => item.Sha256 != diagnostic.ManifestSha256).Select(item => item.Sha256)
                .Distinct().Order(StringComparer.Ordinal).ToArray(), HistoricalNewsDiagnosticPolicy.PolicyId,
            HistoricalNewsDiagnosticPolicy.PolicySha256, StrictCatalystProviderUsed: false);
        return new(null, diagnostic, dependencies, diagnostic.ManifestSha256, diagnostic.DependencyGraphSha256, provenance,
            config, HistoricalDiagnosticNewsManifest.CurrentSchema);
    }
}

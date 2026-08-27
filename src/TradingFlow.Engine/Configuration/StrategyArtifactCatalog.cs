using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Configuration;

/// <summary>
/// Loads the explicit strategy bootstrap manifest and produces immutable,
/// content-addressed artifacts. Directory location is never lifecycle authority.
/// </summary>
public sealed class StrategyArtifactCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string repositoryRoot;
    private readonly string manifestPath;
    private readonly SimpleYamlReader yamlReader;
    private readonly Lazy<StrategyCatalogSnapshot> snapshot;

    public StrategyArtifactCatalog(
        string repositoryRoot,
        string manifestPath,
        SimpleYamlReader yamlReader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        this.yamlReader = yamlReader ?? throw new ArgumentNullException(nameof(yamlReader));
        this.repositoryRoot = Path.GetFullPath(repositoryRoot);
        this.manifestPath = Path.GetFullPath(manifestPath);
        snapshot = new Lazy<StrategyCatalogSnapshot>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public StrategyCatalogSnapshot GetSnapshot() => snapshot.Value;

    public string ResolveSourcePath(StrategyArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return ResolveInsideRepository(artifact.SourcePath);
    }

    public IReadOnlyList<StrategyArtifact> GetSelectable(
        StrategySelectionMode mode,
        IReadOnlyCollection<StrategyArtifactIdentity>? authorizedPaperShadow = null,
        IReadOnlyCollection<StrategyArtifactIdentity>? authorizedValidated = null,
        IReadOnlyCollection<StrategyArtifactIdentity>? suspended = null)
    {
        var blocked = (suspended ?? [])
            .Select(IdentityKey)
            .ToHashSet(StringComparer.Ordinal);
        var artifacts = GetSnapshot().ExecutableArtifacts;
        return mode switch
        {
            StrategySelectionMode.Backtest or
            StrategySelectionMode.CreatePaperExperiment or
            StrategySelectionMode.DiagnosticReplay => artifacts
                .Where(artifact => artifact.Disposition == StrategyArtifactDisposition.Research)
                .OrderBy(artifact => artifact.Identity.StrategyId, StringComparer.Ordinal)
                .ThenBy(artifact => artifact.Identity.SemanticVersion, StringComparer.Ordinal)
                .ToArray(),
            StrategySelectionMode.RunPaperExperiment => [],
            StrategySelectionMode.RunPaperShadow => MatchAuthorized(
                artifacts,
                authorizedPaperShadow,
                authorizedValidated,
                blocked),
            StrategySelectionMode.RunLive => MatchAuthorized(
                artifacts,
                authorizedValidated,
                null,
                blocked),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    public StrategyArtifact RequireExact(StrategyArtifactIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return GetSnapshot().ExecutableArtifacts.SingleOrDefault(artifact =>
                   IdentityKey(artifact.Identity).Equals(IdentityKey(identity), StringComparison.Ordinal))
               ?? throw new KeyNotFoundException(
                   $"Strategy artifact '{identity.StrategyId}@{identity.SemanticVersion}/{identity.ContentSha256}' was not found.");
    }

    public StrategyArtifact RequireSourcePath(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        return GetSnapshot().ExecutableArtifacts.SingleOrDefault(artifact =>
                   ResolveInsideRepository(artifact.SourcePath).Equals(fullPath, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException(
                   $"Strategy source '{sourcePath}' is not an executable research artifact in the lifecycle catalog.");
    }

    public StrategyArtifact CreateDerivedArtifact(
        StrategyArtifact source,
        StrategyDefinition resolvedStrategy,
        string semanticVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(resolvedStrategy);
        if (source.Disposition != StrategyArtifactDisposition.Research)
        {
            throw new InvalidOperationException(
                "Archived strategies cannot produce executable run snapshots.");
        }

        var canonical = new CanonicalStrategyDocument(
            source.Identity.StrategyId,
            semanticVersion,
            resolvedStrategy,
            source.AdmissionProfile);
        var identity = new StrategyArtifactIdentity(
            source.Identity.StrategyId,
            semanticVersion,
            EvidenceCanonicalJson.ComputeSha256(canonical));
        var occupiedBootstrapIdentity = GetSnapshot().ExecutableArtifacts
            .Select(artifact => artifact.Identity)
            .Concat(GetSnapshot().ArchivedArtifacts.Select(artifact => artifact.Identity))
            .SingleOrDefault(existing =>
                existing.StrategyId.Equals(identity.StrategyId, StringComparison.Ordinal) &&
                existing.SemanticVersion.Equals(identity.SemanticVersion, StringComparison.Ordinal));
        if (occupiedBootstrapIdentity is not null &&
            !occupiedBootstrapIdentity.ContentSha256.Equals(identity.ContentSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Strategy '{identity.StrategyId}@{identity.SemanticVersion}' is already occupied by different canonical bootstrap content.");
        }

        return new StrategyArtifact(
            identity,
            StrategyArtifactDisposition.Research,
            StrategyLifecycleState.Research,
            source.SourcePath,
            source.SourceSha256,
            resolvedStrategy,
            source.AdmissionProfile,
            source.Identity.ContentSha256);
    }

    private StrategyCatalogSnapshot Load()
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("The strategy catalog manifest was not found.", manifestPath);
        }

        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(manifestPath), JsonOptions)
            ?? throw new InvalidDataException("The strategy catalog manifest is empty.");
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Strategy catalog schema '{manifest.SchemaVersion}' is unsupported.");
        }

        var profiles = manifest.AdmissionProfiles.ToDictionary(
            profile => Required(profile.ProfileId, nameof(profile.ProfileId)),
            ToDomain,
            StringComparer.Ordinal);
        var duplicatePath = manifest.Strategies
            .GroupBy(item => NormalizeRelativePath(item.SourcePath), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicatePath is not null)
        {
            throw new InvalidDataException(
                $"Strategy catalog path '{duplicatePath.Key}' is classified more than once.");
        }

        EnsureManifestCompleteness(manifest.Strategies);
        var executable = new List<StrategyArtifact>();
        var archived = new List<ArchivedStrategyArtifact>();
        var identityHashes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in manifest.Strategies)
        {
            var stableId = Required(item.StrategyId, nameof(item.StrategyId));
            var semanticVersion = Required(item.SemanticVersion, nameof(item.SemanticVersion));
            var relativePath = NormalizeRelativePath(item.SourcePath);
            var sourcePath = ResolveInsideRepository(relativePath);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    $"Strategy catalog artifact '{relativePath}' was not found.",
                    sourcePath);
            }

            var sourceSha = Hash(File.ReadAllBytes(sourcePath));
            var lifecycle = ParseLifecycle(item.Lifecycle);
            if (!profiles.TryGetValue(Required(item.AdmissionProfileId, nameof(item.AdmissionProfileId)), out var profile))
            {
                throw new InvalidDataException(
                    $"Strategy artifact '{relativePath}' references unknown admission profile '{item.AdmissionProfileId}'.");
            }

            if (lifecycle == StrategyLifecycleState.Archived)
            {
                var archivedCanonical = new CanonicalArchivedStrategyDocument(
                    stableId,
                    semanticVersion,
                    sourceSha,
                    profile);
                var archivedHash = EvidenceCanonicalJson.ComputeSha256(archivedCanonical);
                EnsureUniqueFamilyVersion(identityHashes, stableId, semanticVersion, archivedHash);
                archived.Add(new ArchivedStrategyArtifact(
                    new StrategyArtifactIdentity(stableId, semanticVersion, archivedHash),
                    relativePath,
                    sourceSha,
                    profile,
                    Required(item.Reason, nameof(item.Reason))));
                continue;
            }

            var definition = yamlReader.ReadStrategy(sourcePath);
            var canonical = new CanonicalStrategyDocument(
                stableId,
                semanticVersion,
                definition,
                profile);
            var contentHash = EvidenceCanonicalJson.ComputeSha256(canonical);
            EnsureUniqueFamilyVersion(identityHashes, stableId, semanticVersion, contentHash);
            var identity = new StrategyArtifactIdentity(stableId, semanticVersion, contentHash);
            if (lifecycle != StrategyLifecycleState.Research)
            {
                throw new InvalidDataException(
                    $"Bootstrap artifact '{relativePath}' cannot start in lifecycle '{lifecycle}'.");
            }

            executable.Add(new StrategyArtifact(
                identity,
                StrategyArtifactDisposition.Research,
                StrategyLifecycleState.Research,
                relativePath,
                sourceSha,
                definition,
                profile));
        }

        return new StrategyCatalogSnapshot(executable, archived);
    }

    private static void EnsureUniqueFamilyVersion(
        IDictionary<string, string> identityHashes,
        string strategyId,
        string semanticVersion,
        string contentHash)
    {
        var familyVersion = $"{strategyId}\u001f{semanticVersion}";
        if (identityHashes.TryGetValue(familyVersion, out var existingHash) &&
            !existingHash.Equals(contentHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Strategy identity '{strategyId}@{semanticVersion}' has conflicting content hashes.");
        }

        identityHashes[familyVersion] = contentHash;
    }

    private void EnsureManifestCompleteness(IReadOnlyCollection<ManifestStrategy> entries)
    {
        var classified = entries
            .Select(item => NormalizeRelativePath(item.SourcePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativeRoot in new[] { "configs/strategies", "configs/backtest/strategies" })
        {
            var root = ResolveInsideRepository(relativeRoot);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories))
            {
                discovered.Add(NormalizeRelativePath(Path.GetRelativePath(repositoryRoot, path)));
            }
        }

        var missing = discovered.Except(classified, StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var stale = classified.Except(discovered, StringComparer.OrdinalIgnoreCase).Order().ToArray();
        if (missing.Length != 0 || stale.Length != 0)
        {
            throw new InvalidDataException(
                $"Strategy catalog classification is incomplete. Missing=[{String.Join(", ", missing)}]; " +
                $"stale=[{String.Join(", ", stale)}].");
        }
    }

    private string ResolveInsideRepository(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath));
        var prefix = repositoryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? repositoryRoot
            : repositoryRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Strategy catalog path '{relativePath}' escapes the repository root.");
        }

        return fullPath;
    }

    private static IReadOnlyList<StrategyArtifact> MatchAuthorized(
        IReadOnlyList<StrategyArtifact> artifacts,
        IReadOnlyCollection<StrategyArtifactIdentity>? primary,
        IReadOnlyCollection<StrategyArtifactIdentity>? additional,
        IReadOnlySet<string> blocked)
    {
        var allowed = (primary ?? [])
            .Concat(additional ?? [])
            .Select(IdentityKey)
            .ToHashSet(StringComparer.Ordinal);
        return artifacts
            .Where(artifact => allowed.Contains(IdentityKey(artifact.Identity)))
            .Where(artifact => !blocked.Contains(IdentityKey(artifact.Identity)))
            .OrderBy(artifact => artifact.Identity.StrategyId, StringComparer.Ordinal)
            .ThenBy(artifact => artifact.Identity.SemanticVersion, StringComparer.Ordinal)
            .ToArray();
    }

    private static StrategyAdmissionProfile ToDomain(ManifestAdmissionProfile profile) =>
        new(
            Required(profile.ProfileId, nameof(profile.ProfileId)),
            Required(profile.ProfileVersion, nameof(profile.ProfileVersion)),
            Required(profile.ParserProfile, nameof(profile.ParserProfile)),
            new StrategyIndicatorProfile(
                Required(profile.StandardIndicatorLibrary, nameof(profile.StandardIndicatorLibrary)),
                Required(profile.StandardIndicatorLibraryVersion, nameof(profile.StandardIndicatorLibraryVersion)),
                Required(profile.DerivedIndicatorProfile, nameof(profile.DerivedIndicatorProfile)),
                Required(profile.DerivedIndicatorProfileVersion, nameof(profile.DerivedIndicatorProfileVersion))),
            Required(profile.RiskPolicy, nameof(profile.RiskPolicy)),
            Required(profile.ExecutionTimingPolicy, nameof(profile.ExecutionTimingPolicy)));

    private static StrategyLifecycleState ParseLifecycle(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "research" => StrategyLifecycleState.Research,
            "archived" => StrategyLifecycleState.Archived,
            _ => throw new InvalidDataException(
                $"Bootstrap lifecycle '{value}' must be research or archived.")
        };

    private static string IdentityKey(StrategyArtifactIdentity identity) =>
        $"{identity.StrategyId}\u001f{identity.SemanticVersion}\u001f{identity.ContentSha256}";

    private static string NormalizeRelativePath(string path) =>
        Required(path, nameof(path)).Replace('\\', '/').TrimStart('/');

    private static string Required(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value.Trim();
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class Manifest
    {
        public int SchemaVersion { get; init; }

        public IReadOnlyList<ManifestAdmissionProfile> AdmissionProfiles { get; init; } = [];

        public IReadOnlyList<ManifestStrategy> Strategies { get; init; } = [];
    }

    private sealed class ManifestAdmissionProfile
    {
        public string ProfileId { get; init; } = String.Empty;
        public string ProfileVersion { get; init; } = String.Empty;
        public string ParserProfile { get; init; } = String.Empty;
        public string StandardIndicatorLibrary { get; init; } = String.Empty;
        public string StandardIndicatorLibraryVersion { get; init; } = String.Empty;
        public string DerivedIndicatorProfile { get; init; } = String.Empty;
        public string DerivedIndicatorProfileVersion { get; init; } = String.Empty;
        public string RiskPolicy { get; init; } = String.Empty;
        public string ExecutionTimingPolicy { get; init; } = String.Empty;
    }

    private sealed class ManifestStrategy
    {
        public string SourcePath { get; init; } = String.Empty;
        public string StrategyId { get; init; } = String.Empty;
        public string SemanticVersion { get; init; } = String.Empty;
        public string Lifecycle { get; init; } = String.Empty;
        public string AdmissionProfileId { get; init; } = String.Empty;
        public string Reason { get; init; } = String.Empty;
    }
}

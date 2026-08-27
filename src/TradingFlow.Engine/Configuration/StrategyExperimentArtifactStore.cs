using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Engine.Configuration;

/// <summary>
/// Registers immutable paper-experiment artifacts before any execution run may
/// reference them. A content-addressed directory is the durable catalog entry;
/// per-run snapshots are separate and cannot create lifecycle authority.
/// </summary>
public sealed class StrategyExperimentArtifactStore : IStrategyExperimentArtifactCatalog
{
    private const string ManifestFileName = "artifact.json";
    private const string StrategyFileName = "strategy.yaml";
    private static readonly TimeSpan StoreLockTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string root;
    private readonly StrategyArtifactCatalog sourceCatalog;
    private readonly SimpleYamlReader yamlReader;
    private readonly IArtifactWriter artifactWriter;
    private readonly object writeGate = new();

    public StrategyExperimentArtifactStore(
        string root,
        StrategyArtifactCatalog sourceCatalog,
        SimpleYamlReader yamlReader,
        IArtifactWriter artifactWriter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.GetFullPath(root);
        this.sourceCatalog = sourceCatalog ?? throw new ArgumentNullException(nameof(sourceCatalog));
        this.yamlReader = yamlReader ?? throw new ArgumentNullException(nameof(yamlReader));
        this.artifactWriter = artifactWriter ?? throw new ArgumentNullException(nameof(artifactWriter));
    }

    public IReadOnlyList<StrategyArtifact> List()
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, ManifestFileName, SearchOption.AllDirectories)
            .Where(path => !IsStagingPath(path))
            .Select(Load)
            .OrderBy(artifact => artifact.Identity.StrategyId, StringComparer.Ordinal)
            .ThenBy(artifact => artifact.Identity.SemanticVersion, StringComparer.Ordinal)
            .ThenBy(artifact => artifact.Identity.ContentSha256, StringComparer.Ordinal)
            .ToArray();
    }

    public StrategyArtifact Register(
        StrategyArtifact source,
        StrategyDefinition resolvedStrategy,
        string semanticVersion,
        string registeredBy,
        IReadOnlyDictionary<string, string> overrideDiff,
        string resolvedYaml)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(resolvedStrategy);
        ArgumentException.ThrowIfNullOrWhiteSpace(registeredBy);
        ArgumentNullException.ThrowIfNull(overrideDiff);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedYaml);
        _ = sourceCatalog.RequireExact(source.Identity);

        var artifact = sourceCatalog.CreateDerivedArtifact(
            source,
            resolvedStrategy,
            semanticVersion);
        lock (writeGate)
        {
            using var storeLock = AcquireStoreLock();
            var sameVersion = List().Where(existing =>
                existing.Identity.StrategyId.Equals(artifact.Identity.StrategyId, StringComparison.Ordinal) &&
                existing.Identity.SemanticVersion.Equals(artifact.Identity.SemanticVersion, StringComparison.Ordinal)).ToArray();
            var exact = sameVersion.SingleOrDefault(existing => existing.Identity == artifact.Identity);
            if (exact is not null)
            {
                return exact;
            }

            if (sameVersion.Length != 0)
            {
                throw new InvalidOperationException(
                    $"Paper experiment '{artifact.Identity.StrategyId}@{artifact.Identity.SemanticVersion}' already has different registered content.");
            }

            return WriteNewArtifact(
                artifact,
                source,
                registeredBy.Trim(),
                overrideDiff
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
                resolvedYaml);
        }
    }

    public StrategyArtifact RequireExact(StrategyArtifactIdentity identity) =>
        List().SingleOrDefault(artifact => artifact.Identity == identity)
        ?? throw new KeyNotFoundException(
            $"Paper experiment '{identity.StrategyId}@{identity.SemanticVersion}/{identity.ContentSha256}' was not found.");

    public bool IsRegistered(StrategyArtifactIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return List().Any(artifact => artifact.Identity == identity);
    }

    private StrategyArtifact WriteNewArtifact(
        StrategyArtifact artifact,
        StrategyArtifact source,
        string registeredBy,
        IReadOnlyDictionary<string, string> overrideDiff,
        string resolvedYaml)
    {
        var target = ResolveArtifactDirectory(artifact.Identity);
        var stagingRoot = ResolveInsideRoot(".staging");
        var staging = ResolveInsideRoot(Path.Combine(".staging", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(staging);
        try
        {
            var strategyPath = Path.Combine(staging, StrategyFileName);
            artifactWriter.WriteText(strategyPath, resolvedYaml);
            var parsed = yamlReader.ReadStrategy(strategyPath);
            var parsedHash = EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
                artifact.Identity.StrategyId,
                artifact.Identity.SemanticVersion,
                parsed,
                artifact.AdmissionProfile));
            if (!parsedHash.Equals(artifact.Identity.ContentSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Serialized paper-experiment YAML does not round-trip to its canonical identity.");
            }

            var manifest = new StrategyCatalogArtifactManifest(
                1,
                artifact.Identity,
                StrategyArtifactDisposition.Research,
                source.Identity,
                source.SourcePath,
                artifact.AdmissionProfile,
                DateTimeOffset.UtcNow,
                registeredBy,
                overrideDiff);
            artifactWriter.WriteText(
                Path.Combine(staging, ManifestFileName),
                Encoding.UTF8.GetString(EvidenceCanonicalJson.SerializeToUtf8Bytes(manifest)));

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(staging, target);
            return Load(Path.Combine(target, ManifestFileName));
        }
        finally
        {
            if (Directory.Exists(staging) && IsInsideRoot(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private StrategyArtifact Load(string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize<StrategyCatalogArtifactManifest>(
                           File.ReadAllBytes(manifestPath),
                           JsonOptions)
                       ?? throw new InvalidDataException($"Paper-experiment manifest '{manifestPath}' is empty.");
        if (manifest.SchemaVersion != 1 ||
            manifest.Disposition != StrategyArtifactDisposition.Research ||
            manifest.RegisteredAtUtc == default ||
            manifest.RegisteredAtUtc.Offset != TimeSpan.Zero ||
            String.IsNullOrWhiteSpace(manifest.RegisteredBy) ||
            String.IsNullOrWhiteSpace(manifest.SourcePath))
        {
            throw new InvalidDataException($"Paper-experiment manifest '{manifestPath}' has invalid provenance.");
        }

        _ = sourceCatalog.RequireExact(manifest.SourceIdentity);
        var strategyPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, StrategyFileName);
        if (!File.Exists(strategyPath))
        {
            throw new InvalidDataException($"Paper-experiment manifest '{manifestPath}' has no strategy document.");
        }

        var resolved = yamlReader.ReadStrategy(strategyPath);
        var actualHash = EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
            manifest.Identity.StrategyId,
            manifest.Identity.SemanticVersion,
            resolved,
            manifest.AdmissionProfile));
        if (!actualHash.Equals(manifest.Identity.ContentSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Paper-experiment artifact '{strategyPath}' failed content validation.");
        }

        return new StrategyArtifact(
            manifest.Identity,
            StrategyArtifactDisposition.Research,
            StrategyLifecycleState.Research,
            strategyPath,
            Hash(File.ReadAllBytes(strategyPath)),
            resolved,
            manifest.AdmissionProfile,
            manifest.SourceIdentity.ContentSha256);
    }

    private string ResolveArtifactDirectory(StrategyArtifactIdentity identity) => ResolveInsideRoot(Path.Combine(
        SafeSegment(identity.StrategyId),
        SafeSegment(identity.SemanticVersion),
        identity.ContentSha256));

    private FileStream AcquireStoreLock()
    {
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, ".catalog.lock");
        var deadline = DateTime.UtcNow + StoreLockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        $"Timed out acquiring the paper-experiment catalog lock at '{lockPath}'.",
                        exception);
                }

                Thread.Sleep(25);
            }
        }
    }

    private string ResolveInsideRoot(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsInsideRoot(fullPath))
        {
            throw new InvalidDataException($"Paper-experiment path '{relativePath}' escapes the artifact root.");
        }

        return fullPath;
    }

    private bool IsInsideRoot(string path)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsStagingPath(string path) => Path.GetRelativePath(root, path)
        .StartsWith($".staging{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string SafeSegment(string value) => String.Concat(value.Select(character =>
        Char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-'));

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.SnakeCaseLower,
            allowIntegerValues: false));
        return options;
    }
}

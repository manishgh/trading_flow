using System.Text.Json;
using System.Text.Json.Serialization;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Configuration;

/// <summary>
/// Validates generated strategy snapshots before execution. Canonical catalog
/// sources may be read directly for research tooling; every generated copy must
/// carry a sidecar whose exact identity hashes the resolved strategy and admission
/// profile.
/// </summary>
public sealed class StrategyRunArtifactValidator(
    SimpleYamlReader yamlReader,
    StrategyArtifactCatalog strategyArtifacts)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    static StrategyRunArtifactValidator()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.SnakeCaseLower,
            allowIntegerValues: false));
    }

    public StrategyDefinition ReadAndValidatePaper(string strategyPath)
        => ReadAndValidatePaperArtifact(strategyPath).Definition;

    public ValidatedStrategyRunArtifact ReadAndValidatePaperArtifact(string strategyPath)
    {
        var manifest = ValidatePaperSnapshot(strategyPath);
        return new ValidatedStrategyRunArtifact(
            manifest,
            ReadAndValidate(strategyPath, manifest.SelectionMode));
    }

    public StrategyRunArtifactManifest ValidatePaperSnapshot(string strategyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyPath);
        var manifestPath = Path.GetFullPath(strategyPath) + ".artifact.json";
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException(
                $"Paper strategy snapshot '{strategyPath}' has no immutable artifact manifest.");
        }

        var manifest = DeserializeManifest(manifestPath);
        if (manifest.SelectionMode is not (StrategySelectionMode.RunPaperExperiment or StrategySelectionMode.RunPaperShadow))
        {
            throw new InvalidDataException(
                $"Paper strategy snapshot '{strategyPath}' has non-paper selection mode '{manifest.SelectionMode}'.");
        }

        _ = ReadAndValidate(strategyPath, manifest.SelectionMode);
        return manifest;
    }

    public StrategyDefinition ReadAndValidate(
        string strategyPath,
        StrategySelectionMode requiredMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyPath);
        var fullPath = Path.GetFullPath(strategyPath);
        var manifestPath = fullPath + ".artifact.json";
        if (!File.Exists(manifestPath))
        {
            if (requiredMode is StrategySelectionMode.Backtest or
                StrategySelectionMode.CreatePaperExperiment or
                StrategySelectionMode.DiagnosticReplay)
            {
                var artifact = strategyArtifacts.RequireSourcePath(fullPath);
                if (artifact.Disposition != StrategyArtifactDisposition.Research)
                {
                    throw new InvalidDataException(
                        $"Canonical strategy '{fullPath}' is not a research artifact.");
                }

                return artifact.ResolvedStrategy;
            }

            throw new InvalidDataException(
                $"Generated strategy snapshot '{fullPath}' has no immutable artifact manifest.");
        }

        var manifest = DeserializeManifest(manifestPath);
        if (manifest.SchemaVersion != 1 ||
            manifest.SelectionMode != requiredMode ||
            manifest.CreatedAtUtc == default ||
            manifest.CreatedAtUtc.Offset != TimeSpan.Zero ||
            String.IsNullOrWhiteSpace(manifest.CreatedBy) ||
            String.IsNullOrWhiteSpace(manifest.SourcePath))
        {
            throw new InvalidDataException(
                $"Strategy artifact manifest '{manifestPath}' has invalid provenance metadata.");
        }

        if (requiredMode is StrategySelectionMode.RunPaperShadow or StrategySelectionMode.RunLive &&
            manifest.Identity != manifest.SourceIdentity)
        {
            throw new InvalidDataException(
                $"Authorized strategy snapshot '{fullPath}' does not preserve its promoted identity.");
        }

        var strategy = yamlReader.ReadStrategy(fullPath);
        var canonical = new CanonicalStrategyDocument(
            manifest.Identity.StrategyId,
            manifest.Identity.SemanticVersion,
            strategy,
            manifest.AdmissionProfile);
        var actualHash = EvidenceCanonicalJson.ComputeSha256(canonical);
        if (!actualHash.Equals(manifest.Identity.ContentSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Strategy snapshot '{fullPath}' does not match immutable identity {manifest.Identity.ContentSha256}.");
        }

        return strategy;
    }

    private static StrategyRunArtifactManifest DeserializeManifest(string manifestPath) =>
        JsonSerializer.Deserialize<StrategyRunArtifactManifest>(
            File.ReadAllBytes(manifestPath),
            JsonOptions)
        ?? throw new InvalidDataException(
            $"Strategy artifact manifest '{manifestPath}' is empty.");

}

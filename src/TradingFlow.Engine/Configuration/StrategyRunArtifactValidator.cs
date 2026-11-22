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

    // Checks current source availability before a new backtest or diagnostic consumes an immutable strategy.
    public AuthorizedRuntimeStrategy ReadAndAuthorizeRuntimeArtifact(string strategyPath, StrategySelectionMode requiredMode)
    {
        RequireEnabledForExecution(strategyPath);
        return ReadAndValidateRuntimeArtifact(strategyPath, requiredMode);
    }

    // Preflights declared catalog lineage before creating jobs or preparing data. Returns false for ad hoc YAML.
    public bool RequireEnabledForExecution(string strategyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyPath);
        var fullPath = Path.GetFullPath(strategyPath);
        var canonical = strategyArtifacts.FindSourcePath(fullPath);
        if (canonical is not null)
            strategyArtifacts.RequireEnabled(canonical);

        var manifestPath = fullPath + ".artifact.json";
        if (File.Exists(manifestPath))
        {
            var manifest = DeserializeManifest(manifestPath);
            strategyArtifacts.RequireEnabledIfKnown(manifest.Identity);
            var source = strategyArtifacts.RequireEnabled(strategyArtifacts.RequireExact(manifest.SourceIdentity));
            if (manifest.Identity.StrategyId != source.Identity.StrategyId)
                throw new InvalidDataException($"Strategy snapshot '{fullPath}' does not preserve its catalog source strategy family.");
            return true;
        }

        if (StrategyExperimentArtifactStore.FindManifestForStrategyDocument(fullPath) is { } registrationPath)
        {
            var registration = StrategyExperimentArtifactStore.ReadManifest(registrationPath);
            strategyArtifacts.RequireEnabledIfKnown(registration.Identity);
            strategyArtifacts.RequireEnabled(strategyArtifacts.RequireExact(registration.SourceIdentity));
            return true;
        }
        return canonical is not null;
    }

    // Reads the legacy research tools' raw inputs while honoring declared lineage and exact known catalog content.
    public StrategyDefinition ReadResearchDefinition(string strategyPath)
    {
        var declaredSource = RequireEnabledForExecution(strategyPath);
        var definition = yamlReader.ReadStrategy(strategyPath);
        if (!declaredSource)
            strategyArtifacts.RequireEnabledIfKnown(definition);
        return definition;
    }

    // Reads immutable strategy evidence for historical displays and audits independently of current availability.
    public AuthorizedRuntimeStrategy ReadAndValidateRuntimeArtifact(
        string strategyPath,
        StrategySelectionMode requiredMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyPath);
        var fullPath = Path.GetFullPath(strategyPath);
        var manifestPath = fullPath + ".artifact.json";
        if (File.Exists(manifestPath))
        {
            var manifest = DeserializeManifest(manifestPath);
            var definition = ReadAndValidate(fullPath, requiredMode);
            return new AuthorizedRuntimeStrategy(
                manifest.Identity,
                manifest.SelectionMode,
                definition,
                manifest.AdmissionProfile);
        }

        var artifact = strategyArtifacts.RequireSourcePath(fullPath);
        var resolved = ReadAndValidate(fullPath, requiredMode);
        return new AuthorizedRuntimeStrategy(
            artifact.Identity,
            requiredMode,
            resolved,
            artifact.AdmissionProfile);
    }

    public static AuthorizedRuntimeStrategy CreateDiagnosticRuntimeStrategy(
        StrategyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        const string semanticVersion = "0.0.0-diagnostic";
        var profile = StrategyAdmissionProfiles.DeterministicV1;
        var identity = new StrategyArtifactIdentity(
            definition.StrategyId,
            semanticVersion,
            EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(
                definition.StrategyId,
                semanticVersion,
                definition,
                profile)));
        return new AuthorizedRuntimeStrategy(
            identity,
            StrategySelectionMode.DiagnosticReplay,
            definition,
            profile);
    }

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

    // Validate the immutable source/snapshot and prevent Research diagnostic policy from crossing into paper or live modes.
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

                RequireDiagnosticBacktest(artifact.ResolvedStrategy, artifact.AdmissionProfile, requiredMode, artifact.Identity);
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
        RequireDiagnosticBacktest(strategy, manifest.AdmissionProfile, requiredMode, manifest.SourceIdentity);
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

    // A diagnostic recipe cannot become a paper/live snapshot even if an ordinary grant is later created.
    private void RequireDiagnosticBacktest(StrategyDefinition strategy, StrategyAdmissionProfile profile,
        StrategySelectionMode mode, StrategyArtifactIdentity sourceIdentity)
    {
        if (strategy.EntryRules.HistoricalNewsDiagnostic is null && profile.HistoricalNewsDiagnosticPolicyId is null) return;
        if (mode != StrategySelectionMode.Backtest ||
            !HistoricalCompanyNewsDiagnosticPolicy.IsBoundTreatment(strategy.EntryRules.HistoricalNewsDiagnostic,
                profile.HistoricalNewsDiagnosticPolicyId))
            throw new InvalidDataException("Historical news diagnostic recipes are bound exclusively to their Research backtest policy.");
        var source = strategyArtifacts.RequireExact(sourceIdentity);
        if (source.Disposition != StrategyArtifactDisposition.Research || source.EffectiveLifecycle != StrategyLifecycleState.Research ||
            source.AdmissionProfile != profile || source.ResolvedStrategy.EntryRules.HistoricalNewsDiagnostic != strategy.EntryRules.HistoricalNewsDiagnostic)
            throw new InvalidDataException("Historical news diagnostic snapshot requires its exact registered Research source and unchanged admission policy.");
    }

    private static StrategyRunArtifactManifest DeserializeManifest(string manifestPath) =>
        JsonSerializer.Deserialize<StrategyRunArtifactManifest>(
            File.ReadAllBytes(manifestPath),
            JsonOptions)
        ?? throw new InvalidDataException(
            $"Strategy artifact manifest '{manifestPath}' is empty.");
}

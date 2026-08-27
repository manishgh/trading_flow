using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Research;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

/// <summary>
/// Adapts the immutable domain strategy catalog for current Web consumers.
/// Selection mode and exact promotion identity are enforced before a strategy
/// option reaches a page or API.
/// </summary>
public sealed class ConfigCatalogService
{
    private readonly ProjectPaths paths;
    private readonly SimpleYamlReader yamlReader;
    private readonly StrategyArtifactCatalog strategyArtifacts;
    private readonly StrategyExperimentArtifactStore experimentArtifacts;
    private readonly IStrategyAuthorizationPolicy authorizationPolicy;
    private readonly ILogger<ConfigCatalogService> logger;

    public ConfigCatalogService(
        ProjectPaths paths,
        SimpleYamlReader yamlReader,
        StrategyArtifactCatalog strategyArtifacts,
        StrategyExperimentArtifactStore experimentArtifacts,
        IStrategyAuthorizationPolicy authorizationPolicy,
        ILogger<ConfigCatalogService>? logger = null)
    {
        this.paths = paths;
        this.yamlReader = yamlReader;
        this.strategyArtifacts = strategyArtifacts ?? throw new ArgumentNullException(nameof(strategyArtifacts));
        this.experimentArtifacts = experimentArtifacts ?? throw new ArgumentNullException(nameof(experimentArtifacts));
        this.authorizationPolicy = authorizationPolicy
            ?? throw new ArgumentNullException(nameof(authorizationPolicy));
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigCatalogService>.Instance;
    }

    public IReadOnlyList<RunConfigSummary> GetBacktestConfigs() =>
        GetRunConfigs(paths.BacktestConfigsRoot);

    public IReadOnlyList<RunConfigSummary> GetPaperConfigs() =>
        GetRunConfigs(paths.PaperConfigsRoot);

    public async Task<IReadOnlyList<StrategyOption>> GetStrategiesAsync(
        StrategySelectionMode mode,
        CancellationToken cancellationToken = default)
    {
        var activeGrants = await authorizationPolicy.GetActiveGrantsAsync(cancellationToken);
        var paperExperiment = activeGrants
            .Where(grant => grant.Authorization == StrategyExecutionAuthorization.PaperExperiment)
            .Select(grant => grant.Identity)
            .ToArray();
        var paperShadow = activeGrants
            .Where(grant => grant.Authorization == StrategyExecutionAuthorization.PaperShadow)
            .Select(grant => grant.Identity)
            .ToArray();
        var validated = activeGrants
            .Where(grant => grant.Authorization == StrategyExecutionAuthorization.Validated)
            .Select(grant => grant.Identity)
            .ToArray();
        var registeredExperiments = experimentArtifacts.List();
        var selected = mode == StrategySelectionMode.RunPaperExperiment
            ? registeredExperiments
                .Where(artifact => paperExperiment.Contains(artifact.Identity))
                .ToArray()
            : strategyArtifacts.GetSelectable(mode, paperShadow, validated)
                .Concat(mode switch
                {
                    StrategySelectionMode.RunPaperShadow => registeredExperiments
                        .Where(artifact => paperShadow.Contains(artifact.Identity)),
                    StrategySelectionMode.RunLive => registeredExperiments
                        .Where(artifact => validated.Contains(artifact.Identity)),
                    _ => []
                })
                .GroupBy(artifact => artifact.Identity)
                .Select(group => group
                    .OrderByDescending(artifact => Path.IsPathRooted(artifact.SourcePath))
                    .First())
                .ToArray();
        var validatedKeys = validated.Select(IdentityKey).ToHashSet(StringComparer.Ordinal);
        return selected
            .Select(artifact => ToStrategyOption(
                artifact,
                mode switch
                {
                    StrategySelectionMode.RunPaperExperiment => StrategyLifecycleState.PaperExperiment,
                    StrategySelectionMode.RunPaperShadow when validatedKeys.Contains(IdentityKey(artifact.Identity)) =>
                        StrategyLifecycleState.Validated,
                    StrategySelectionMode.RunPaperShadow => StrategyLifecycleState.PaperShadow,
                    StrategySelectionMode.RunLive => StrategyLifecycleState.Validated,
                    _ => artifact.EffectiveLifecycle
                }))
            .ToArray();
    }

    public async Task<StrategyOption> RequireSelectableAsync(
        StrategySelectionMode mode,
        string strategyPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyPath);
        var fullPath = Path.GetFullPath(strategyPath);
        return (await GetStrategiesAsync(mode, cancellationToken))
            .SingleOrDefault(strategy => Path.GetFullPath(strategy.Path).Equals(
                fullPath,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Strategy '{strategyPath}' is not selectable for {mode}.");
    }

    public async Task RequireAuthorizedPaperRunSnapshotAsync(
        string strategyPath,
        CancellationToken cancellationToken = default)
    {
        var manifest = new StrategyRunArtifactValidator(yamlReader, strategyArtifacts)
            .ValidatePaperSnapshot(strategyPath);
        var authorized = await GetStrategiesAsync(manifest.SelectionMode, cancellationToken);
        if (!authorized.Any(option => option.Identity == manifest.Identity))
        {
            throw new InvalidOperationException(
                $"Strategy '{manifest.Identity.StrategyId}@{manifest.Identity.SemanticVersion}' " +
                $"is no longer authorized for {manifest.SelectionMode}.");
        }
    }

    public ValidatedStrategyRunArtifact ReadAndValidatePaperSnapshot(string strategyPath) =>
        new StrategyRunArtifactValidator(yamlReader, strategyArtifacts)
            .ReadAndValidatePaperArtifact(strategyPath);

    public RunConfigSummary GetConfig(string configPath)
    {
        var fullPath = Path.GetFullPath(configPath);
        var config = yamlReader.ReadBacktestRun(fullPath);
        var artifactsByPath = strategyArtifacts.GetSnapshot().ExecutableArtifacts
            .ToDictionary(
                strategyArtifacts.ResolveSourcePath,
                StringComparer.OrdinalIgnoreCase);
        var strategyOptions = config.Strategies
            .Select(Path.GetFullPath)
            .Where(artifactsByPath.ContainsKey)
            .Select(path => ToStrategyOption(artifactsByPath[path], artifactsByPath[path].EffectiveLifecycle))
            .ToArray();
        return new RunConfigSummary(fullPath, Path.GetFileName(fullPath), config, strategyOptions);
    }

    private IReadOnlyList<RunConfigSummary> GetRunConfigs(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.GetFiles(root, "*.yaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}archive{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}temp{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}ui-runs{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}strategies{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(TryGetConfig)
            .OfType<RunConfigSummary>()
            .ToArray();
    }

    private RunConfigSummary? TryGetConfig(string path)
    {
        try
        {
            return GetConfig(path);
        }
        catch (Exception exception) when (IsCatalogRecoverable(exception))
        {
            logger.LogWarning(exception, "Skipping invalid run config {ConfigPath} while building catalog.", path);
            return null;
        }
    }

    private StrategyOption ToStrategyOption(
        StrategyArtifact artifact,
        StrategyLifecycleState lifecycle)
    {
        var effectiveArtifact = artifact.EffectiveLifecycle == lifecycle
            ? artifact
            : artifact with { EffectiveLifecycle = lifecycle };
        var path = Path.IsPathRooted(artifact.SourcePath)
            ? Path.GetFullPath(artifact.SourcePath)
            : strategyArtifacts.ResolveSourcePath(artifact);
        return new StrategyOption(
            path,
            Path.GetFileName(path),
            artifact.ResolvedStrategy,
            effectiveArtifact.Identity,
            lifecycle,
            null,
            effectiveArtifact);
    }

    private static bool IsCatalogRecoverable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException;

    private static string IdentityKey(StrategyArtifactIdentity identity) =>
        $"{identity.StrategyId}\u001f{identity.SemanticVersion}\u001f{identity.ContentSha256}";
}

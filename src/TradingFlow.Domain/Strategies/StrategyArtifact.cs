using System.Text.RegularExpressions;

namespace TradingFlow.Domain.Strategies;

public enum StrategyLifecycleState
{
    Research = 1,
    PaperExperiment = 2,
    PaperShadow = 3,
    Validated = 4,
    Archived = 5
}

public enum StrategyArtifactDisposition
{
    Research = 1,
    Archived = 2
}

public enum StrategyExecutionAuthorization
{
    PaperExperiment = 1,
    PaperShadow = 2,
    Validated = 3
}

public enum StrategySelectionMode
{
    Backtest = 1,
    CreatePaperExperiment = 2,
    RunPaperExperiment = 3,
    RunPaperShadow = 4,
    RunLive = 5,
    DiagnosticReplay = 6
}

public sealed record StrategyArtifactIdentity
{
    private static readonly Regex SemanticVersionPattern = new(
        @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public StrategyArtifactIdentity(
        string strategyId,
        string semanticVersion,
        string contentSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        if (!SemanticVersionPattern.IsMatch(semanticVersion.Trim()))
        {
            throw new ArgumentException(
                "Strategy semantic version must use MAJOR.MINOR.PATCH form.",
                nameof(semanticVersion));
        }

        var normalizedHash = contentSha256.Trim().ToLowerInvariant();
        if (normalizedHash.Length != 64 || normalizedHash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Strategy content hash must contain exactly 64 hexadecimal characters.",
                nameof(contentSha256));
        }

        StrategyId = strategyId.Trim();
        SemanticVersion = semanticVersion.Trim();
        ContentSha256 = normalizedHash;
    }

    public string StrategyId { get; }

    public string SemanticVersion { get; }

    public string ContentSha256 { get; }
}

public sealed record StrategyIndicatorProfile(
    string StandardLibrary,
    string StandardLibraryVersion,
    string DerivedIndicatorProfile,
    string DerivedIndicatorProfileVersion);

public sealed record StrategyAdmissionProfile(
    string ProfileId,
    string ProfileVersion,
    string ParserProfile,
    StrategyIndicatorProfile Indicators,
    string RiskPolicy,
    string ExecutionTimingPolicy);

public static class StrategyAdmissionProfiles
{
    public static StrategyAdmissionProfile DeterministicV1 { get; } = new(
        "default-deterministic-v1",
        "1.0.0",
        "simple-yaml-strict-v1",
        new StrategyIndicatorProfile(
            "Skender.Stock.Indicators",
            "2.7.1",
            "TradingFlow.DerivedIndicators",
            "1.0.0"),
        "account-risk-budget-v1",
        "completed-bar-next-executable-bar-v1");
}

public sealed record CanonicalStrategyDocument(
    string StrategyId,
    string SemanticVersion,
    StrategyDefinition ResolvedStrategy,
    StrategyAdmissionProfile AdmissionProfile);

/// <summary>
/// Canonical provenance for terminal, view-only artifacts whose historical YAML
/// is intentionally outside the current executable parser contract. This identity
/// can never receive an execution grant; executable artifacts always use the fully
/// resolved <see cref="CanonicalStrategyDocument"/> representation.
/// </summary>
public sealed record CanonicalArchivedStrategyDocument(
    string StrategyId,
    string SemanticVersion,
    string SourceSha256,
    StrategyAdmissionProfile AdmissionProfile);

public sealed record StrategyArtifact(
    StrategyArtifactIdentity Identity,
    StrategyArtifactDisposition Disposition,
    StrategyLifecycleState EffectiveLifecycle,
    string SourcePath,
    string SourceSha256,
    StrategyDefinition ResolvedStrategy,
    StrategyAdmissionProfile AdmissionProfile,
    string? DerivedFromContentSha256 = null);

public sealed record ArchivedStrategyArtifact(
    StrategyArtifactIdentity Identity,
    string SourcePath,
    string SourceSha256,
    StrategyAdmissionProfile AdmissionProfile,
    string Reason);

public sealed record StrategyCatalogSnapshot(
    IReadOnlyList<StrategyArtifact> ExecutableArtifacts,
    IReadOnlyList<ArchivedStrategyArtifact> ArchivedArtifacts);

public interface IStrategyExperimentArtifactCatalog
{
    bool IsRegistered(StrategyArtifactIdentity identity);
}

public sealed record StrategyAuthorizationGrant(
    StrategyArtifactIdentity Identity,
    StrategyExecutionAuthorization Authorization,
    string GrantDecisionId,
    DateTimeOffset GrantedAtUtc);

public sealed record StrategyEntryAdmission(
    Guid IntentId,
    StrategyArtifactIdentity Identity,
    StrategySelectionMode SelectionMode,
    string GrantDecisionId,
    long RegistrySequence,
    DateTimeOffset AdmittedAtUtc);

public interface IStrategyAuthorizationPolicy
{
    Task<IReadOnlyList<StrategyAuthorizationGrant>> GetActiveGrantsAsync(
        CancellationToken cancellationToken = default);

    Task<StrategyEntryAdmission> AdmitNewEntryAsync(
        StrategyArtifactIdentity identity,
        StrategySelectionMode selectionMode,
        Guid intentId,
        DateTimeOffset admittedAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IStrategyExperimentAuthorizationCommands
{
    Task GrantPaperExperimentAsync(
        StrategyArtifactIdentity identity,
        string decisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken = default);

    Task RevokePaperExperimentAsync(
        StrategyArtifactIdentity identity,
        string decisionId,
        string targetDecisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken = default);

    Task SuspendPaperExperimentAsync(
        StrategyArtifactIdentity identity,
        string decisionId,
        string targetDecisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken = default);

    Task ResumePaperExperimentAsync(
        StrategyArtifactIdentity identity,
        string decisionId,
        string targetDecisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken = default);

    Task SupersedePaperExperimentAsync(
        StrategyArtifactIdentity existingIdentity,
        string supersessionDecisionId,
        string targetDecisionId,
        StrategyArtifactIdentity replacementIdentity,
        string replacementDecisionId,
        string actor,
        DateTimeOffset decidedAtUtc,
        string reason,
        CancellationToken cancellationToken = default);
}

public sealed record ValidatedStrategyRunArtifact(
    StrategyRunArtifactManifest Manifest,
    StrategyDefinition Definition);

public sealed record AuthorizedRuntimeStrategy(
    StrategyArtifactIdentity Identity,
    StrategySelectionMode SelectionMode,
    StrategyDefinition Definition,
    StrategyAdmissionProfile AdmissionProfile);

public sealed record StrategyRunArtifactManifest(
    int SchemaVersion,
    StrategyArtifactIdentity Identity,
    StrategySelectionMode SelectionMode,
    StrategyArtifactIdentity SourceIdentity,
    string SourcePath,
    StrategyAdmissionProfile AdmissionProfile,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy,
    IReadOnlyDictionary<string, string> OverrideDiff);

public sealed record StrategyCatalogArtifactManifest(
    int SchemaVersion,
    StrategyArtifactIdentity Identity,
    StrategyArtifactDisposition Disposition,
    StrategyArtifactIdentity SourceIdentity,
    string SourcePath,
    StrategyAdmissionProfile AdmissionProfile,
    DateTimeOffset RegisteredAtUtc,
    string RegisteredBy,
    IReadOnlyDictionary<string, string> OverrideDiff);

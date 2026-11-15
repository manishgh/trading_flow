using TradingFlow.Domain.Jobs;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace TradingFlow.Domain.Research;

public enum ResearchStartState
{
    Pending,
    Admitted,
    Refused,
    Interrupted
}

public enum ResearchStartKind
{
    SignalStudy,
    CatalogStudy,
    Portfolio,
    Optimization
}

/// <summary>A variant's scientific inputs before a registry transaction allocates its family ordinal.</summary>
public sealed record FrozenResearchVariant(
    string VariantKey,
    string FamilyId,
    string Hypothesis,
    string RuleSha256,
    IReadOnlyList<string> DatasetIds,
    IReadOnlyDictionary<string, string> Clocks,
    IReadOnlyDictionary<string, string> Formulas,
    IReadOnlyDictionary<string, decimal> Thresholds,
    IReadOnlyDictionary<string, string> Costs,
    IReadOnlyList<ResearchPartitionDefinition> Partitions,
    string PrimaryMetric,
    IReadOnlyList<string> RejectionCriteria,
    bool DatasetEvidenceFrozen = true);

/// <summary>The intended executions are fixed independently from generated per-attempt paths and enqueue time.</summary>
public sealed record FrozenResearchStart(
    string StartId,
    ResearchStartKind Kind,
    string RequestSha256,
    string CodeSha256,
    IReadOnlyList<FrozenResearchVariant> Variants,
    IReadOnlyDictionary<string, string[]> Executions);

public sealed record ResearchStartTrial(string VariantKey, string ExperimentId, string DefinitionSha256, int FamilyOrdinal);

public sealed record ResearchStartBinding(
    FrozenResearchStart Request,
    IReadOnlyList<ResearchStartTrial> Trials,
    string? ReviewEvidence,
    bool DiagnosticReplay,
    DateTimeOffset? ProtectedObservationStartUtc = null);

public sealed record ResearchStart(
    ResearchStartBinding Binding,
    ResearchStartState State,
    string? Reason,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ResearchExecutionResult(
    string ExecutionId,
    string State,
    DateTimeOffset AtUtc,
    string? ArtifactReference = null,
    string? ArtifactSha256 = null,
    string? Detail = null,
    ResearchPublicationEvidence? Publication = null);

public sealed record ResearchPublicationEvidence(string ManifestReference, string ManifestSha256,
    IReadOnlyList<ResearchVariantOutcome>? VariantOutcomes = null);

public sealed record ResearchVariantOutcome(string VariantKey, string Metric, decimal Value, string ResultSha256, string ManifestSha256);

/// <summary>Deployment-owned authorization for a specific frozen portfolio request and exact successful study.</summary>
public sealed record ReviewedSignalStudy(
    string PortfolioRequestSha256, string FamilyId, string RuleSha256, string ExperimentId,
    string TrialDefinitionSha256, string ResultId, string ResultSha256, string StudyManifestSha256,
    string ReviewReference, string? StudyRuleSha256 = null);

/// <summary>Deployment-owned authorization to replay an exact reviewed historical request and its original evidence.</summary>
public sealed record ReviewedResearchReplay(
    string RequestSha256, string OriginalArtifactSha256, string ReviewReference,
    string OriginalArtifactPath, DateTimeOffset ObservationCutoffUtc);

public sealed record ResearchAdmissionReviews(
    IReadOnlyList<ReviewedSignalStudy> SignalStudies,
    IReadOnlyList<ReviewedResearchReplay> DiagnosticReplays)
{
    public static ResearchAdmissionReviews Empty { get; } = new([], []);
}

public sealed record ResearchLedgerTrial(
    ResearchTrialDefinition Trial,
    string TrialSha256,
    ResearchHoldoutState? HoldoutState,
    IReadOnlyList<CanonicalResearchResultManifest> Results);

public sealed record ResearchLedgerStart(ResearchStart Start, IReadOnlyList<ResearchExecutionResult> Executions);

public sealed record ResearchLedger(
    IReadOnlyList<ResearchLedgerTrial> Trials,
    IReadOnlyList<ResearchLedgerStart> Starts);

public static class ResearchStartIdentity
{
    /// <summary>Validates a SHA-256 from a frozen request, external review, or artifact receipt.</summary>
    public static string RequireSha256(string value) => EvidenceValue.NormalizeSha256(value);

    /// <summary>Computes the stable request namespace without storing an operator's raw retry key.</summary>
    public static string StartId(string route, string requestKey) => route + "-" + HashText(requestKey.Trim());

    /// <summary>Uses the same binary identity for admission and worker verification, including engine dependencies.</summary>
    public static string CodeSha256(params Assembly[] assemblies) => EvidenceCanonicalJson.ComputeSha256(
        assemblies.Distinct().OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)
            .Select(assembly => new { Name = assembly.GetName().Name, Sha256 = HashFile(assembly.Location) }).ToArray());

    /// <summary>Hashes exact file bytes without changing or copying the source.</summary>
    public static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    /// <summary>Hashes the UTF-8 bytes used in evidence and retry identifiers.</summary>
    public static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public interface IResearchStartRegistry
{
    Task<ResearchStart> ReserveStartAsync(FrozenResearchStart request, CancellationToken cancellationToken);
    Task<IReadOnlyList<PersistedJob>> FreezeQueueJobsAsync(string startId, IReadOnlyList<PersistedJob> jobs, CancellationToken cancellationToken);
    Task<IReadOnlyList<PersistedJob>> ReadQueueJobsAsync(string startId, CancellationToken cancellationToken);
    Task<bool> OwnsQueueInputsAsync(PersistedJob job, CancellationToken cancellationToken);
    Task SetStartStateAsync(string startId, ResearchStartState state, string? reason, CancellationToken cancellationToken);
    Task<DateTimeOffset?> RequireQueueJobAsync(PersistedJob job, string codeSha256, CancellationToken cancellationToken);
    Task RecordQueueResultAsync(PersistedJob job, Func<CancellationToken, Task<ResearchPublicationEvidence?>>? verifyPublication,
        CancellationToken cancellationToken);
    Task RecordExecutionResultAsync(string startId, ResearchExecutionResult outcome, CancellationToken cancellationToken);
    Task<ResearchExecutionResult?> FindExecutionResultAsync(string startId, string executionId, CancellationToken cancellationToken);
    Task<ResearchLedger> ReadLedgerAsync(CancellationToken cancellationToken);
}

using System.Collections.ObjectModel;

namespace TradingFlow.Domain.Research;

public sealed class EvidenceHoldoutIdentity
{
    public EvidenceHoldoutIdentity(
        string studyFamily,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        IReadOnlyList<EvidenceDatasetReference> datasets,
        string universeLedgerId,
        EvidenceArtifactReference universeLedgerArtifact,
        EvidenceArtifactReference partitionDefinitionArtifact)
    {
        StudyFamily = EvidenceValue.NormalizeRequired(studyFamily, nameof(studyFamily));
        EvidenceValue.EnsureUtc(startUtc, nameof(startUtc));
        EvidenceValue.EnsureUtc(endUtc, nameof(endUtc));
        if (endUtc <= startUtc)
        {
            throw new ArgumentException("Holdout end must follow its start.");
        }

        StartUtc = startUtc;
        EndUtc = endUtc;
        Datasets = CopyDatasetReferences(datasets, nameof(datasets));
        UniverseLedgerId = EvidenceValue.NormalizeRequired(universeLedgerId, nameof(universeLedgerId));
        UniverseLedgerArtifact = universeLedgerArtifact
            ?? throw new ArgumentNullException(nameof(universeLedgerArtifact));
        PartitionDefinitionArtifact = partitionDefinitionArtifact
            ?? throw new ArgumentNullException(nameof(partitionDefinitionArtifact));
        HoldoutId = EvidenceValue.ComputeSha256(String.Join(
            "\n",
            new[]
            {
                StudyFamily,
                StartUtc.ToString("O"),
                EndUtc.ToString("O"),
                UniverseLedgerId,
                UniverseLedgerArtifact.ObjectNamespace.Value,
                UniverseLedgerArtifact.Content.Sha256,
                PartitionDefinitionArtifact.ObjectNamespace.Value,
                PartitionDefinitionArtifact.Content.Sha256,
                String.Join(",", Datasets.Select(dataset =>
                    $"{dataset.DatasetId}:{dataset.ManifestArtifact.ObjectNamespace.Value}:{dataset.ManifestSha256}"))
            }));
    }

    public string HoldoutId { get; }

    public string StudyFamily { get; }

    public DateTimeOffset StartUtc { get; }

    public DateTimeOffset EndUtc { get; }

    public IReadOnlyList<EvidenceDatasetReference> Datasets { get; }

    public string UniverseLedgerId { get; }

    public EvidenceArtifactReference UniverseLedgerArtifact { get; }

    public EvidenceArtifactReference PartitionDefinitionArtifact { get; }

    internal static IReadOnlyList<EvidenceDatasetReference> CopyDatasetReferences(
        IEnumerable<EvidenceDatasetReference> datasets,
        string parameterName)
    {
        var values = (datasets ?? throw new ArgumentNullException(parameterName))
            .Select(dataset => dataset ?? throw new ArgumentException("Dataset references cannot contain null.", parameterName))
            .OrderBy(dataset => dataset.DatasetId, StringComparer.Ordinal)
            .ThenBy(dataset => dataset.ManifestArtifact.ObjectNamespace.Value, StringComparer.Ordinal)
            .ThenBy(dataset => dataset.ManifestSha256, StringComparer.Ordinal)
            .ToArray();
        if (values.Length == 0)
        {
            throw new ArgumentException("At least one dataset reference is required.", parameterName);
        }

        if (values.Select(dataset => dataset.DatasetId).Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new ArgumentException("Dataset identifiers must be unique.", parameterName);
        }

        return new ReadOnlyCollection<EvidenceDatasetReference>(values);
    }
}

public sealed record EvidenceHoldoutConsumption
{
    public EvidenceHoldoutConsumption(
        string holdoutId,
        string researchRunId,
        DateTimeOffset consumedAtUtc)
    {
        HoldoutId = EvidenceValue.NormalizeSha256(holdoutId);
        ResearchRunId = EvidenceValue.NormalizeRequired(researchRunId, nameof(researchRunId));
        EvidenceValue.EnsureUtc(consumedAtUtc, nameof(consumedAtUtc));
        ConsumedAtUtc = consumedAtUtc;
    }

    public string HoldoutId { get; }

    public string ResearchRunId { get; }

    public DateTimeOffset ConsumedAtUtc { get; }
}

public sealed record EvidenceStudyWindow
{
    public EvidenceStudyWindow(string name, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        Name = EvidenceValue.NormalizeRequired(name, nameof(name));
        EvidenceValue.EnsureUtc(startUtc, nameof(startUtc));
        EvidenceValue.EnsureUtc(endUtc, nameof(endUtc));
        if (endUtc <= startUtc)
        {
            throw new ArgumentException("Study-window end must follow its start.");
        }

        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    public string Name { get; }

    public DateTimeOffset StartUtc { get; }

    public DateTimeOffset EndUtc { get; }
}

public sealed class EvidenceStudyPartitions
{
    public EvidenceStudyPartitions(
        EvidenceStudyWindow development,
        EvidenceStudyWindow validation,
        EvidenceStudyWindow holdout)
    {
        Development = development ?? throw new ArgumentNullException(nameof(development));
        Validation = validation ?? throw new ArgumentNullException(nameof(validation));
        Holdout = holdout ?? throw new ArgumentNullException(nameof(holdout));
        if (Development.EndUtc != Validation.StartUtc ||
            Validation.EndUtc != Holdout.StartUtc)
        {
            throw new ArgumentException(
                "Development, validation, and holdout windows must be contiguous and chronological.");
        }
    }

    public EvidenceStudyWindow Development { get; }

    public EvidenceStudyWindow Validation { get; }

    public EvidenceStudyWindow Holdout { get; }
}

public sealed class EvidenceSimulationAssumptions
{
    public EvidenceSimulationAssumptions(
        EvidenceArtifactReference costModel,
        EvidenceArtifactReference spreadModel,
        EvidenceArtifactReference slippageModel,
        EvidenceArtifactReference borrowModel,
        EvidenceArtifactReference benchmarkDefinition)
    {
        CostModel = costModel ?? throw new ArgumentNullException(nameof(costModel));
        SpreadModel = spreadModel ?? throw new ArgumentNullException(nameof(spreadModel));
        SlippageModel = slippageModel ?? throw new ArgumentNullException(nameof(slippageModel));
        BorrowModel = borrowModel ?? throw new ArgumentNullException(nameof(borrowModel));
        BenchmarkDefinition = benchmarkDefinition ?? throw new ArgumentNullException(nameof(benchmarkDefinition));
    }

    public EvidenceArtifactReference CostModel { get; }

    public EvidenceArtifactReference SpreadModel { get; }

    public EvidenceArtifactReference SlippageModel { get; }

    public EvidenceArtifactReference BorrowModel { get; }

    public EvidenceArtifactReference BenchmarkDefinition { get; }
}

public sealed class EvidenceResearchRunManifest
{
    public EvidenceResearchRunManifest(
        string researchRunId,
        string studyId,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<EvidenceDatasetReference> inputDatasets,
        string universeLedgerId,
        EvidenceArtifactReference universeLedgerArtifact,
        EvidenceArtifactReference studyConfigArtifact,
        string codeVersion,
        EvidenceStudyPartitions studyPartitions,
        EvidenceSimulationAssumptions assumptions,
        EvidenceHoldoutIdentity? holdout,
        bool evidenceReady,
        IReadOnlyList<string>? readinessFailures,
        IReadOnlyList<EvidenceArtifactReference>? outputs)
    {
        ResearchRunId = EvidenceValue.NormalizeRequired(researchRunId, nameof(researchRunId));
        StudyId = EvidenceValue.NormalizeRequired(studyId, nameof(studyId));
        EvidenceValue.EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        CreatedAtUtc = createdAtUtc;
        InputDatasets = EvidenceHoldoutIdentity.CopyDatasetReferences(inputDatasets, nameof(inputDatasets));
        UniverseLedgerId = EvidenceValue.NormalizeRequired(universeLedgerId, nameof(universeLedgerId));
        UniverseLedgerArtifact = universeLedgerArtifact
            ?? throw new ArgumentNullException(nameof(universeLedgerArtifact));
        StudyConfigArtifact = studyConfigArtifact
            ?? throw new ArgumentNullException(nameof(studyConfigArtifact));
        CodeVersion = EvidenceValue.NormalizeRequired(codeVersion, nameof(codeVersion));
        StudyPartitions = studyPartitions ?? throw new ArgumentNullException(nameof(studyPartitions));
        Assumptions = assumptions ?? throw new ArgumentNullException(nameof(assumptions));
        Holdout = holdout;
        if (Holdout is not null &&
            (!Holdout.UniverseLedgerId.Equals(UniverseLedgerId, StringComparison.Ordinal) ||
             Holdout.UniverseLedgerArtifact != UniverseLedgerArtifact ||
             !Holdout.Datasets.SequenceEqual(InputDatasets) ||
             Holdout.StartUtc != StudyPartitions.Holdout.StartUtc ||
             Holdout.EndUtc != StudyPartitions.Holdout.EndUtc))
        {
            throw new ArgumentException(
                "The holdout must reference the run datasets, universe ledger, and holdout window.",
                nameof(holdout));
        }

        EvidenceReady = evidenceReady;
        ReadinessFailures = new ReadOnlyCollection<string>(
            (readinessFailures ?? Array.Empty<string>())
            .Select(failure => EvidenceValue.NormalizeRequired(failure, nameof(readinessFailures)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(failure => failure, StringComparer.Ordinal)
            .ToArray());
        Outputs = new ReadOnlyCollection<EvidenceArtifactReference>(
            (outputs ?? Array.Empty<EvidenceArtifactReference>())
            .Select(output => output ?? throw new ArgumentException("Outputs cannot contain null.", nameof(outputs)))
            .OrderBy(output => output.ObjectNamespace.Value, StringComparer.Ordinal)
            .ThenBy(output => output.Content.Sha256, StringComparer.Ordinal)
            .ToArray());

        if (EvidenceReady && ReadinessFailures.Count > 0)
        {
            throw new ArgumentException("An evidence-ready run cannot contain readiness failures.");
        }

        if (!EvidenceReady && ReadinessFailures.Count == 0)
        {
            throw new ArgumentException("A non-ready run requires at least one readiness failure.");
        }

        if (EvidenceReady && Holdout is null)
        {
            throw new ArgumentException("An evidence-ready run requires an immutable holdout.");
        }
    }

    public string ResearchRunId { get; }

    public string StudyId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public IReadOnlyList<EvidenceDatasetReference> InputDatasets { get; }

    public string UniverseLedgerId { get; }

    public EvidenceArtifactReference UniverseLedgerArtifact { get; }

    public EvidenceArtifactReference StudyConfigArtifact { get; }

    public string CodeVersion { get; }

    public EvidenceStudyPartitions StudyPartitions { get; }

    public EvidenceSimulationAssumptions Assumptions { get; }

    public EvidenceHoldoutIdentity? Holdout { get; }

    public bool EvidenceReady { get; }

    public IReadOnlyList<string> ReadinessFailures { get; }

    public IReadOnlyList<EvidenceArtifactReference> Outputs { get; }
}

public enum EvidencePinSubjectKind
{
    Dataset = 1,
    ResearchRun = 2,
    PromotionDecision = 3,
    AuditCase = 4,
    LegalHold = 5
}

public sealed record EvidencePinSubject
{
    public EvidencePinSubject(EvidencePinSubjectKind kind, string subjectId)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        SubjectId = EvidenceValue.NormalizeRequired(subjectId, nameof(subjectId));
    }

    public EvidencePinSubjectKind Kind { get; }

    public string SubjectId { get; }
}

public sealed record EvidenceRetentionPin
{
    public EvidenceRetentionPin(
        string pinId,
        EvidencePinSubject subject,
        string reason,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<EvidenceArtifactReference> referencedArtifacts)
    {
        PinId = EvidenceValue.NormalizeRequired(pinId, nameof(pinId));
        Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        Reason = EvidenceValue.NormalizeRequired(reason, nameof(reason));
        EvidenceValue.EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        CreatedAtUtc = createdAtUtc;
        ReferencedArtifacts = new ReadOnlyCollection<EvidenceArtifactReference>(
            (referencedArtifacts ?? throw new ArgumentNullException(nameof(referencedArtifacts)))
            .Select(reference => reference
                ?? throw new ArgumentException("Referenced artifacts cannot contain null.", nameof(referencedArtifacts)))
            .Distinct()
            .OrderBy(reference => reference.ObjectNamespace.Value, StringComparer.Ordinal)
            .ThenBy(reference => reference.Content.Sha256, StringComparer.Ordinal)
            .ToArray());
        if (ReferencedArtifacts.Count == 0)
        {
            throw new ArgumentException("A retention pin requires a non-empty reference closure.", nameof(referencedArtifacts));
        }
    }

    public string PinId { get; }

    public EvidencePinSubject Subject { get; }

    public string Reason { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public IReadOnlyList<EvidenceArtifactReference> ReferencedArtifacts { get; }
}

public sealed class EvidenceReferenceSubjectManifest
{
    public EvidenceReferenceSubjectManifest(
        EvidencePinSubject subject,
        DateTimeOffset registeredAtUtc,
        IReadOnlyList<EvidenceArtifactReference> referencedArtifacts)
    {
        Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        if (Subject.Kind is EvidencePinSubjectKind.Dataset or EvidencePinSubjectKind.ResearchRun)
        {
            throw new ArgumentException(
                "Datasets and research runs are registered through the evidence catalog.",
                nameof(subject));
        }

        EvidenceValue.EnsureUtc(registeredAtUtc, nameof(registeredAtUtc));
        RegisteredAtUtc = registeredAtUtc;
        ReferencedArtifacts = new ReadOnlyCollection<EvidenceArtifactReference>(
            (referencedArtifacts ?? throw new ArgumentNullException(nameof(referencedArtifacts)))
            .Select(reference => reference
                ?? throw new ArgumentException("Referenced artifacts cannot contain null.", nameof(referencedArtifacts)))
            .Distinct()
            .OrderBy(reference => reference.ObjectNamespace.Value, StringComparer.Ordinal)
            .ThenBy(reference => reference.Content.Sha256, StringComparer.Ordinal)
            .ToArray());
        if (ReferencedArtifacts.Count == 0)
        {
            throw new ArgumentException(
                "An external reference subject requires at least one artifact.",
                nameof(referencedArtifacts));
        }
    }

    public EvidencePinSubject Subject { get; }

    public DateTimeOffset RegisteredAtUtc { get; }

    public IReadOnlyList<EvidenceArtifactReference> ReferencedArtifacts { get; }
}

public sealed record EvidenceRetentionPolicy
{
    public EvidenceRetentionPolicy(
        int rawObservationMonths,
        int normalizedDatasetMonths)
    {
        if (rawObservationMonths < 24)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rawObservationMonths),
                "Raw evidence retention cannot be shorter than 24 calendar months.");
        }

        if (normalizedDatasetMonths < 24)
        {
            throw new ArgumentOutOfRangeException(
                nameof(normalizedDatasetMonths),
                "Normalized evidence retention cannot be shorter than 24 calendar months.");
        }

        RawObservationMonths = rawObservationMonths;
        NormalizedDatasetMonths = normalizedDatasetMonths;
    }

    public int RawObservationMonths { get; }

    public int NormalizedDatasetMonths { get; }
}

public enum StrategyPromotionDecisionStatus
{
    Accepted = 1,
    Rejected = 2,
    Revoked = 3,
    Superseded = 4
}

public sealed record EvidenceResearchRunReference
{
    public EvidenceResearchRunReference(string researchRunId, string manifestSha256)
    {
        ResearchRunId = EvidenceValue.NormalizeRequired(researchRunId, nameof(researchRunId));
        ManifestSha256 = EvidenceValue.NormalizeSha256(manifestSha256);
    }

    public string ResearchRunId { get; }

    public string ManifestSha256 { get; }
}

public sealed class StrategyPromotionEvidence
{
    public StrategyPromotionEvidence(
        EvidenceArtifactReference developmentReport,
        EvidenceArtifactReference validationReport,
        EvidenceArtifactReference holdoutReport,
        EvidenceArtifactReference costModel,
        EvidenceArtifactReference concentrationReport)
    {
        DevelopmentReport = developmentReport ?? throw new ArgumentNullException(nameof(developmentReport));
        ValidationReport = validationReport ?? throw new ArgumentNullException(nameof(validationReport));
        HoldoutReport = holdoutReport ?? throw new ArgumentNullException(nameof(holdoutReport));
        CostModel = costModel ?? throw new ArgumentNullException(nameof(costModel));
        ConcentrationReport = concentrationReport ?? throw new ArgumentNullException(nameof(concentrationReport));
    }

    public EvidenceArtifactReference DevelopmentReport { get; }

    public EvidenceArtifactReference ValidationReport { get; }

    public EvidenceArtifactReference HoldoutReport { get; }

    public EvidenceArtifactReference CostModel { get; }

    public EvidenceArtifactReference ConcentrationReport { get; }
}

public sealed class StrategyPromotionDecision
{
    public StrategyPromotionDecision(
        string decisionId,
        StrategyPromotionDecisionStatus status,
        EvidenceResearchRunReference researchRun,
        string holdoutId,
        string strategyId,
        string strategyConfigHash,
        string codeVersion,
        IReadOnlyList<EvidenceDatasetReference> datasets,
        string universeLedgerId,
        EvidenceArtifactReference universeLedgerArtifact,
        StrategyPromotionEvidence evidence,
        string approvedBy,
        DateTimeOffset decidedAtUtc,
        string reason,
        string? targetDecisionId = null)
    {
        DecisionId = EvidenceValue.NormalizeRequired(decisionId, nameof(decisionId));
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
        ResearchRun = researchRun ?? throw new ArgumentNullException(nameof(researchRun));
        HoldoutId = EvidenceValue.NormalizeSha256(holdoutId);
        StrategyId = EvidenceValue.NormalizeRequired(strategyId, nameof(strategyId));
        StrategyConfigHash = EvidenceValue.NormalizeSha256(strategyConfigHash);
        CodeVersion = EvidenceValue.NormalizeRequired(codeVersion, nameof(codeVersion));
        Datasets = EvidenceHoldoutIdentity.CopyDatasetReferences(datasets, nameof(datasets));
        UniverseLedgerId = EvidenceValue.NormalizeRequired(universeLedgerId, nameof(universeLedgerId));
        UniverseLedgerArtifact = universeLedgerArtifact
            ?? throw new ArgumentNullException(nameof(universeLedgerArtifact));
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        ApprovedBy = EvidenceValue.NormalizeRequired(approvedBy, nameof(approvedBy));
        EvidenceValue.EnsureUtc(decidedAtUtc, nameof(decidedAtUtc));
        DecidedAtUtc = decidedAtUtc;
        Reason = EvidenceValue.NormalizeRequired(reason, nameof(reason));
        TargetDecisionId = String.IsNullOrWhiteSpace(targetDecisionId) ? null : targetDecisionId.Trim();
        var requiresTarget = Status is StrategyPromotionDecisionStatus.Revoked or
            StrategyPromotionDecisionStatus.Superseded;
        if (requiresTarget != (TargetDecisionId is not null))
        {
            throw new ArgumentException(
                "Revoked/superseded decisions require a target; accepted/rejected decisions cannot have one.",
                nameof(targetDecisionId));
        }
    }

    public string DecisionId { get; }

    public StrategyPromotionDecisionStatus Status { get; }

    public EvidenceResearchRunReference ResearchRun { get; }

    public string HoldoutId { get; }

    public string StrategyId { get; }

    public string StrategyConfigHash { get; }

    public string CodeVersion { get; }

    public IReadOnlyList<EvidenceDatasetReference> Datasets { get; }

    public string UniverseLedgerId { get; }

    public EvidenceArtifactReference UniverseLedgerArtifact { get; }

    public StrategyPromotionEvidence Evidence { get; }

    public string ApprovedBy { get; }

    public DateTimeOffset DecidedAtUtc { get; }

    public string Reason { get; }

    public string? TargetDecisionId { get; }
}

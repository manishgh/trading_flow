using System.Collections.ObjectModel;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Research;

/// <summary>
/// A JSON value whose source bytes are copied at construction so callers cannot mutate a
/// research package while it is being prepared or published.
/// </summary>
public sealed class EvidenceResearchJsonPayload
{
    public EvidenceResearchJsonPayload(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
        {
            throw new ArgumentException("A research JSON payload cannot be empty.", nameof(utf8Json));
        }

        Utf8Json = utf8Json.ToArray();
    }

    public ReadOnlyMemory<byte> Utf8Json { get; }
}

public sealed class EvidenceResearchAssumptionPayloads
{
    public EvidenceResearchAssumptionPayloads(
        EvidenceResearchJsonPayload costModel,
        EvidenceResearchJsonPayload spreadModel,
        EvidenceResearchJsonPayload slippageModel,
        EvidenceResearchJsonPayload borrowModel,
        EvidenceResearchJsonPayload benchmarkDefinition)
    {
        CostModel = costModel ?? throw new ArgumentNullException(nameof(costModel));
        SpreadModel = spreadModel ?? throw new ArgumentNullException(nameof(spreadModel));
        SlippageModel = slippageModel ?? throw new ArgumentNullException(nameof(slippageModel));
        BorrowModel = borrowModel ?? throw new ArgumentNullException(nameof(borrowModel));
        BenchmarkDefinition = benchmarkDefinition
            ?? throw new ArgumentNullException(nameof(benchmarkDefinition));
    }

    public EvidenceResearchJsonPayload CostModel { get; }

    public EvidenceResearchJsonPayload SpreadModel { get; }

    public EvidenceResearchJsonPayload SlippageModel { get; }

    public EvidenceResearchJsonPayload BorrowModel { get; }

    public EvidenceResearchJsonPayload BenchmarkDefinition { get; }
}

public sealed class EvidenceResearchNamedOutput
{
    public EvidenceResearchNamedOutput(
        string name,
        EvidenceResearchJsonPayload payload)
    {
        Name = EvidenceResearchPackagingValue.NormalizeRequired(name, nameof(name));
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    public string Name { get; }

    public EvidenceResearchJsonPayload Payload { get; }
}

public sealed class EvidenceResearchRunPackageRequest
{
    public EvidenceResearchRunPackageRequest(
        string researchRunId,
        string studyId,
        DateTimeOffset createdAtUtc,
        string codeVersion,
        IReadOnlyList<EvidenceDatasetReference> inputDatasets,
        string universeLedgerId,
        EvidenceStudyPartitions studyPartitions,
        EvidenceResearchJsonPayload studyConfig,
        EvidenceResearchJsonPayload universeLedger,
        EvidenceResearchJsonPayload partitionDefinition,
        EvidenceResearchAssumptionPayloads assumptions,
        IReadOnlyList<EvidenceResearchNamedOutput> deterministicOutputs,
        bool evidenceReady,
        IReadOnlyList<string>? readinessFailures)
    {
        ResearchRunId = EvidenceResearchPackagingValue.NormalizeRequired(
            researchRunId,
            nameof(researchRunId));
        StudyId = EvidenceResearchPackagingValue.NormalizeRequired(studyId, nameof(studyId));
        EvidenceResearchPackagingValue.EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        CreatedAtUtc = createdAtUtc;
        CodeVersion = EvidenceResearchPackagingValue.NormalizeRequired(
            codeVersion,
            nameof(codeVersion));
        InputDatasets = new ReadOnlyCollection<EvidenceDatasetReference>(
            (inputDatasets ?? throw new ArgumentNullException(nameof(inputDatasets)))
            .Select(dataset => dataset
                ?? throw new ArgumentException(
                    "Input datasets cannot contain null.",
                    nameof(inputDatasets)))
            .ToArray());
        UniverseLedgerId = EvidenceResearchPackagingValue.NormalizeRequired(
            universeLedgerId,
            nameof(universeLedgerId));
        StudyPartitions = studyPartitions ?? throw new ArgumentNullException(nameof(studyPartitions));
        StudyConfig = studyConfig ?? throw new ArgumentNullException(nameof(studyConfig));
        UniverseLedger = universeLedger ?? throw new ArgumentNullException(nameof(universeLedger));
        PartitionDefinition = partitionDefinition
            ?? throw new ArgumentNullException(nameof(partitionDefinition));
        Assumptions = assumptions ?? throw new ArgumentNullException(nameof(assumptions));
        DeterministicOutputs = new ReadOnlyCollection<EvidenceResearchNamedOutput>(
            (deterministicOutputs ?? throw new ArgumentNullException(nameof(deterministicOutputs)))
            .Select(output => output
                ?? throw new ArgumentException(
                    "Deterministic outputs cannot contain null.",
                    nameof(deterministicOutputs)))
            .ToArray());
        EvidenceReady = evidenceReady;
        ReadinessFailures = new ReadOnlyCollection<string>(
            (readinessFailures ?? Array.Empty<string>()).ToArray());
    }

    public string ResearchRunId { get; }

    public string StudyId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public string CodeVersion { get; }

    public IReadOnlyList<EvidenceDatasetReference> InputDatasets { get; }

    public string UniverseLedgerId { get; }

    public EvidenceStudyPartitions StudyPartitions { get; }

    public EvidenceResearchJsonPayload StudyConfig { get; }

    public EvidenceResearchJsonPayload UniverseLedger { get; }

    public EvidenceResearchJsonPayload PartitionDefinition { get; }

    public EvidenceResearchAssumptionPayloads Assumptions { get; }

    public IReadOnlyList<EvidenceResearchNamedOutput> DeterministicOutputs { get; }

    public bool EvidenceReady { get; }

    public IReadOnlyList<string> ReadinessFailures { get; }
}

public sealed class EvidenceResearchRunArtifactGraph
{
    public EvidenceResearchRunArtifactGraph(
        EvidenceArtifactReference studyConfig,
        EvidenceArtifactReference universeLedger,
        EvidenceArtifactReference partitionDefinition,
        EvidenceSimulationAssumptions assumptions,
        IReadOnlyDictionary<string, EvidenceArtifactReference> outputs)
    {
        StudyConfig = studyConfig ?? throw new ArgumentNullException(nameof(studyConfig));
        UniverseLedger = universeLedger ?? throw new ArgumentNullException(nameof(universeLedger));
        PartitionDefinition = partitionDefinition
            ?? throw new ArgumentNullException(nameof(partitionDefinition));
        Assumptions = assumptions ?? throw new ArgumentNullException(nameof(assumptions));
        var sortedOutputs =
            new SortedDictionary<string, EvidenceArtifactReference>(StringComparer.Ordinal);
        foreach (var output in outputs ?? throw new ArgumentNullException(nameof(outputs)))
        {
            sortedOutputs.Add(output.Key, output.Value);
        }

        Outputs = new ReadOnlyDictionary<string, EvidenceArtifactReference>(sortedOutputs);
    }

    public EvidenceArtifactReference StudyConfig { get; }

    public EvidenceArtifactReference UniverseLedger { get; }

    public EvidenceArtifactReference PartitionDefinition { get; }

    public EvidenceSimulationAssumptions Assumptions { get; }

    public IReadOnlyDictionary<string, EvidenceArtifactReference> Outputs { get; }

    public IReadOnlyList<EvidenceArtifactReference> AllArtifacts =>
    [
        StudyConfig,
        UniverseLedger,
        PartitionDefinition,
        Assumptions.CostModel,
        Assumptions.SpreadModel,
        Assumptions.SlippageModel,
        Assumptions.BorrowModel,
        Assumptions.BenchmarkDefinition,
        .. Outputs.Values
    ];
}

public sealed record EvidenceResearchRunPackage(
    EvidenceResearchRunArtifactGraph Artifacts,
    EvidenceResearchRunManifest ManifestDraft);

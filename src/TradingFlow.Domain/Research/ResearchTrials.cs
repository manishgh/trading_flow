using System.Collections.ObjectModel;

namespace TradingFlow.Domain.Research;

public enum ResearchHoldoutState
{
    Unopened = 1,
    Consumed = 2,
    Completed = 3
}

public sealed record ResearchPartitionDefinition
{
    public ResearchPartitionDefinition(
        string name,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        TimeSpan embargo)
    {
        Name = EvidenceValue.NormalizeRequired(name, nameof(name));
        EvidenceValue.EnsureUtc(startUtc, nameof(startUtc));
        EvidenceValue.EnsureUtc(endUtc, nameof(endUtc));
        if (endUtc <= startUtc)
        {
            throw new ArgumentException("Partition end must follow its start.");
        }

        if (embargo < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(embargo));
        }

        StartUtc = startUtc;
        EndUtc = endUtc;
        Embargo = embargo;
    }

    public string Name { get; }

    public DateTimeOffset StartUtc { get; }

    public DateTimeOffset EndUtc { get; }

    public TimeSpan Embargo { get; }
}

public sealed class ResearchTrialDefinition
{
    public ResearchTrialDefinition(
        string experimentId,
        string familyId,
        string? parentExperimentId,
        int familyTrialCount,
        string hypothesis,
        IReadOnlyList<string> datasetIds,
        IReadOnlyDictionary<string, string> clocks,
        IReadOnlyDictionary<string, string> formulas,
        IReadOnlyDictionary<string, decimal> thresholds,
        IReadOnlyDictionary<string, string> costAssumptions,
        IReadOnlyList<ResearchPartitionDefinition> partitions,
        string primaryMetric,
        IReadOnlyList<string> rejectionCriteria,
        ResearchHoldoutState holdoutState,
        DateTimeOffset registeredAtUtc)
    {
        ExperimentId = EvidenceValue.NormalizeRequired(experimentId, nameof(experimentId));
        FamilyId = EvidenceValue.NormalizeRequired(familyId, nameof(familyId));
        ParentExperimentId = String.IsNullOrWhiteSpace(parentExperimentId)
            ? null
            : EvidenceValue.NormalizeRequired(parentExperimentId, nameof(parentExperimentId));
        if (ParentExperimentId == ExperimentId)
        {
            throw new ArgumentException("A trial cannot be its own parent.", nameof(parentExperimentId));
        }

        if (familyTrialCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(familyTrialCount));
        }

        FamilyTrialCount = familyTrialCount;
        Hypothesis = EvidenceValue.NormalizeRequired(hypothesis, nameof(hypothesis));
        DatasetIds = CopyDistinct(datasetIds, nameof(datasetIds));
        Clocks = CopyMap(clocks, nameof(clocks));
        Formulas = CopyMap(formulas, nameof(formulas));
        Thresholds = CopyMap(thresholds, nameof(thresholds));
        CostAssumptions = CopyMap(costAssumptions, nameof(costAssumptions));
        Partitions = new ReadOnlyCollection<ResearchPartitionDefinition>(
            (partitions ?? throw new ArgumentNullException(nameof(partitions)))
            .Select(partition => partition
                ?? throw new ArgumentException("Partitions cannot contain null.", nameof(partitions)))
            .OrderBy(partition => partition.StartUtc)
            .ThenBy(partition => partition.Name, StringComparer.Ordinal)
            .ToArray());
        if (Partitions.Count == 0)
        {
            throw new ArgumentException("At least one partition is required.", nameof(partitions));
        }

        for (var index = 1; index < Partitions.Count; index++)
        {
            if (Partitions[index].StartUtc < Partitions[index - 1].EndUtc)
            {
                throw new ArgumentException("Research partitions cannot overlap.", nameof(partitions));
            }
        }

        PrimaryMetric = EvidenceValue.NormalizeRequired(primaryMetric, nameof(primaryMetric));
        RejectionCriteria = CopyDistinct(rejectionCriteria, nameof(rejectionCriteria));
        if (!Enum.IsDefined(holdoutState))
        {
            throw new ArgumentOutOfRangeException(nameof(holdoutState));
        }

        HoldoutState = holdoutState;
        EvidenceValue.EnsureUtc(registeredAtUtc, nameof(registeredAtUtc));
        RegisteredAtUtc = registeredAtUtc;
    }

    public string ExperimentId { get; }

    public string FamilyId { get; }

    public string? ParentExperimentId { get; }

    public int FamilyTrialCount { get; }

    public string Hypothesis { get; }

    public IReadOnlyList<string> DatasetIds { get; }

    public IReadOnlyDictionary<string, string> Clocks { get; }

    public IReadOnlyDictionary<string, string> Formulas { get; }

    public IReadOnlyDictionary<string, decimal> Thresholds { get; }

    public IReadOnlyDictionary<string, string> CostAssumptions { get; }

    public IReadOnlyList<ResearchPartitionDefinition> Partitions { get; }

    public string PrimaryMetric { get; }

    public IReadOnlyList<string> RejectionCriteria { get; }

    public ResearchHoldoutState HoldoutState { get; }

    public DateTimeOffset RegisteredAtUtc { get; }

    private static IReadOnlyList<string> CopyDistinct(
        IEnumerable<string> values,
        string parameterName)
    {
        var result = (values ?? throw new ArgumentNullException(parameterName))
            .Select(value => EvidenceValue.NormalizeRequired(value, parameterName))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (result.Length == 0)
        {
            throw new ArgumentException("At least one value is required.", parameterName);
        }

        return new ReadOnlyCollection<string>(result);
    }

    private static IReadOnlyDictionary<string, TValue> CopyMap<TValue>(
        IReadOnlyDictionary<string, TValue> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count == 0)
        {
            throw new ArgumentException("At least one value is required.", parameterName);
        }

        return new ReadOnlyDictionary<string, TValue>(
            values.OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(
                    item => EvidenceValue.NormalizeRequired(item.Key, parameterName),
                    item => item.Value,
                    StringComparer.Ordinal));
    }
}

public sealed record ResearchHoldoutConsumptionRecord
{
    public ResearchHoldoutConsumptionRecord(
        string experimentId,
        string researchRunId,
        DateTimeOffset consumedAtUtc)
    {
        ExperimentId = EvidenceValue.NormalizeRequired(experimentId, nameof(experimentId));
        ResearchRunId = EvidenceValue.NormalizeRequired(researchRunId, nameof(researchRunId));
        EvidenceValue.EnsureUtc(consumedAtUtc, nameof(consumedAtUtc));
        ConsumedAtUtc = consumedAtUtc;
    }

    public string ExperimentId { get; }

    public string ResearchRunId { get; }

    public DateTimeOffset ConsumedAtUtc { get; }
}

public sealed class CanonicalResearchResultManifest
{
    public CanonicalResearchResultManifest(
        string resultId,
        string experimentId,
        string trialDefinitionSha256,
        string researchRunId,
        string codeVersion,
        string partition,
        string primaryMetric,
        IReadOnlyDictionary<string, decimal> metrics,
        IReadOnlyList<string> outputArtifactSha256,
        DateTimeOffset createdAtUtc)
    {
        ResultId = EvidenceValue.NormalizeRequired(resultId, nameof(resultId));
        ExperimentId = EvidenceValue.NormalizeRequired(experimentId, nameof(experimentId));
        TrialDefinitionSha256 = EvidenceValue.NormalizeSha256(trialDefinitionSha256);
        ResearchRunId = EvidenceValue.NormalizeRequired(researchRunId, nameof(researchRunId));
        CodeVersion = EvidenceValue.NormalizeRequired(codeVersion, nameof(codeVersion));
        Partition = EvidenceValue.NormalizeRequired(partition, nameof(partition));
        PrimaryMetric = EvidenceValue.NormalizeRequired(primaryMetric, nameof(primaryMetric));
        Metrics = new ReadOnlyDictionary<string, decimal>(
            (metrics ?? throw new ArgumentNullException(nameof(metrics)))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => EvidenceValue.NormalizeRequired(item.Key, nameof(metrics)),
                item => item.Value,
                StringComparer.Ordinal));
        if (Metrics.Count == 0)
        {
            throw new ArgumentException("At least one result metric is required.", nameof(metrics));
        }

        OutputArtifactSha256 = new ReadOnlyCollection<string>(
            (outputArtifactSha256 ?? throw new ArgumentNullException(nameof(outputArtifactSha256)))
            .Select(EvidenceValue.NormalizeSha256)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray());
        if (OutputArtifactSha256.Count == 0)
        {
            throw new ArgumentException("At least one output artifact is required.", nameof(outputArtifactSha256));
        }

        EvidenceValue.EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        CreatedAtUtc = createdAtUtc;
    }

    public string ResultId { get; }

    public string ExperimentId { get; }

    public string TrialDefinitionSha256 { get; }

    public string ResearchRunId { get; }

    public string CodeVersion { get; }

    public string Partition { get; }

    public string PrimaryMetric { get; }

    public IReadOnlyDictionary<string, decimal> Metrics { get; }

    public IReadOnlyList<string> OutputArtifactSha256 { get; }

    public DateTimeOffset CreatedAtUtc { get; }
}

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence.Research;

/// <summary>
/// Produces a deterministic immutable artifact closure and an unregistered research-run
/// manifest. This boundary does not acquire data, choose study windows, consume holdouts,
/// or make promotion decisions.
/// </summary>
public sealed class EvidenceResearchRunArtifactPackager(
    IImmutableArtifactStore artifactStore)
{
    private const string JsonMediaType = "application/json";

    /// <summary>
    /// Publishes the immutable identity inputs and returns the exact holdout identity
    /// that must be reserved before any holdout calculation is allowed to run.
    /// </summary>
    public async Task<EvidenceHoldoutIdentity> PrepareHoldoutAsync(
        string studyId,
        IReadOnlyList<EvidenceDatasetReference> inputDatasets,
        string universeLedgerId,
        EvidenceStudyPartitions studyPartitions,
        EvidenceResearchJsonPayload universeLedger,
        EvidenceResearchJsonPayload partitionDefinition,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(studyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(universeLedgerId);
        ArgumentNullException.ThrowIfNull(studyPartitions);
        ArgumentNullException.ThrowIfNull(universeLedger);
        ArgumentNullException.ThrowIfNull(partitionDefinition);
        var universe = PrepareArtifact(
            "research/universe-ledgers",
            universeLedger);
        var partitions = PrepareArtifact(
            "research/partition-definitions",
            partitionDefinition);
        foreach (var artifact in new[] { universe, partitions })
        {
            cancellationToken.ThrowIfCancellationRequested();
            await artifactStore.PutIfAbsentAsync(
                new ImmutableArtifactWriteRequest(artifact.Reference),
                artifact.Bytes,
                cancellationToken);
            var verification = await artifactStore.VerifyAsync(
                artifact.Reference,
                cancellationToken);
            if (!verification.IsValid)
            {
                throw new InvalidDataException(
                    "Immutable holdout identity artifact verification failed: " +
                    verification.FailureReason);
            }
        }

        return new EvidenceHoldoutIdentity(
            studyId,
            studyPartitions.Holdout.StartUtc,
            studyPartitions.Holdout.EndUtc,
            NormalizeDatasets(inputDatasets),
            universeLedgerId,
            universe.Reference,
            partitions.Reference);
    }

    public async Task<EvidenceResearchRunPackage> PackageAsync(
        EvidenceResearchRunPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var prepared = Prepare(request);
        foreach (var artifact in prepared.Artifacts
                     .OrderBy(value => value.Reference.ObjectNamespace.Value, StringComparer.Ordinal)
                     .ThenBy(value => value.Reference.Content.Sha256, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = await artifactStore.PutIfAbsentAsync(
                new ImmutableArtifactWriteRequest(artifact.Reference),
                artifact.Bytes,
                cancellationToken);
            if (receipt.Artifact != artifact.Reference)
            {
                throw new InvalidDataException(
                    "The immutable store returned a receipt for a different evidence artifact.");
            }

            var verification = await artifactStore.VerifyAsync(
                artifact.Reference,
                cancellationToken);
            if (!verification.IsValid ||
                verification.Expected != artifact.Reference ||
                verification.ActualByteLength != artifact.Reference.Content.ByteLength ||
                !String.Equals(
                    verification.ActualSha256,
                    artifact.Reference.Content.Sha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Immutable research artifact verification failed for " +
                    $"'{artifact.Reference.ObjectNamespace.Value}/" +
                    $"{artifact.Reference.Content.Sha256}': " +
                    $"{verification.FailureReason ?? "stored bytes did not match the content address"}.");
            }
        }

        return new EvidenceResearchRunPackage(prepared.Graph, prepared.Manifest);
    }

    private static PreparedPackage Prepare(EvidenceResearchRunPackageRequest request)
    {
        var studyConfig = PrepareArtifact(
            "research/study-configs",
            request.StudyConfig);
        var universeLedger = PrepareArtifact(
            "research/universe-ledgers",
            request.UniverseLedger);
        var partitionDefinition = PrepareArtifact(
            "research/partition-definitions",
            request.PartitionDefinition);
        var costModel = PrepareArtifact(
            "research/assumptions/cost",
            request.Assumptions.CostModel);
        var spreadModel = PrepareArtifact(
            "research/assumptions/spread",
            request.Assumptions.SpreadModel);
        var slippageModel = PrepareArtifact(
            "research/assumptions/slippage",
            request.Assumptions.SlippageModel);
        var borrowModel = PrepareArtifact(
            "research/assumptions/borrow",
            request.Assumptions.BorrowModel);
        var benchmarkDefinition = PrepareArtifact(
            "research/assumptions/benchmark",
            request.Assumptions.BenchmarkDefinition);

        var outputs = new SortedDictionary<string, PreparedArtifact>(StringComparer.Ordinal);
        foreach (var output in request.DeterministicOutputs)
        {
            var name = NormalizeOutputName(output.Name);
            if (!outputs.TryAdd(
                    name,
                    PrepareArtifact($"research/reports/{name}", output.Payload)))
            {
                throw new ArgumentException(
                    $"Deterministic output name '{name}' is duplicated.",
                    nameof(request));
            }
        }

        var simulationAssumptions = new EvidenceSimulationAssumptions(
            costModel.Reference,
            spreadModel.Reference,
            slippageModel.Reference,
            borrowModel.Reference,
            benchmarkDefinition.Reference);
        var outputReferences = new ReadOnlyDictionary<string, EvidenceArtifactReference>(
            outputs.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Reference,
                StringComparer.Ordinal));
        var graph = new EvidenceResearchRunArtifactGraph(
            studyConfig.Reference,
            universeLedger.Reference,
            partitionDefinition.Reference,
            simulationAssumptions,
            outputReferences);

        var readinessFailures = request.ReadinessFailures
            .Select(failure => EvidenceResearchPackagingValue.NormalizeRequired(
                failure,
                nameof(request.ReadinessFailures)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(failure => failure, StringComparer.Ordinal)
            .ToArray();
        if (request.EvidenceReady && readinessFailures.Length > 0)
        {
            throw new ArgumentException(
                "An evidence-ready package cannot contain readiness failures.",
                nameof(request));
        }

        if (!request.EvidenceReady && readinessFailures.Length == 0)
        {
            throw new ArgumentException(
                "A non-ready package requires at least one readiness failure.",
                nameof(request));
        }

        var datasets = NormalizeDatasets(request.InputDatasets);
        var holdout = new EvidenceHoldoutIdentity(
            request.StudyId,
            request.StudyPartitions.Holdout.StartUtc,
            request.StudyPartitions.Holdout.EndUtc,
            datasets,
            request.UniverseLedgerId,
            universeLedger.Reference,
            partitionDefinition.Reference);
        var manifest = new EvidenceResearchRunManifest(
            request.ResearchRunId,
            request.StudyId,
            request.CreatedAtUtc,
            datasets,
            request.UniverseLedgerId,
            universeLedger.Reference,
            studyConfig.Reference,
            request.CodeVersion,
            request.StudyPartitions,
            simulationAssumptions,
            holdout,
            request.EvidenceReady,
            readinessFailures,
            outputReferences.Values.ToArray());

        var artifacts = new[]
            {
                studyConfig,
                universeLedger,
                partitionDefinition,
                costModel,
                spreadModel,
                slippageModel,
                borrowModel,
                benchmarkDefinition
            }
            .Concat(outputs.Values)
            .GroupBy(
                artifact => (
                    artifact.Reference.ObjectNamespace.Value,
                    artifact.Reference.Content.Sha256))
            .Select(group =>
            {
                var values = group.ToArray();
                if (values.Select(value => value.Reference).Distinct().Count() != 1 ||
                    values.Skip(1).Any(value =>
                        !value.Bytes.Span.SequenceEqual(values[0].Bytes.Span)))
                {
                    throw new InvalidDataException(
                        "Research artifacts collided within the prepared package.");
                }

                return values[0];
            })
            .ToArray();

        return new PreparedPackage(artifacts, graph, manifest);
    }

    private static IReadOnlyList<EvidenceDatasetReference> NormalizeDatasets(
        IReadOnlyList<EvidenceDatasetReference> datasets)
    {
        var normalized = (datasets ?? throw new ArgumentNullException(nameof(datasets)))
            .Select(dataset => dataset
                ?? throw new ArgumentException(
                    "Input datasets cannot contain null.",
                    nameof(datasets)))
            .OrderBy(dataset => dataset.DatasetId, StringComparer.Ordinal)
            .ThenBy(
                dataset => dataset.ManifestArtifact.ObjectNamespace.Value,
                StringComparer.Ordinal)
            .ThenBy(dataset => dataset.ManifestSha256, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "At least one input dataset is required.",
                nameof(datasets));
        }

        if (normalized.Select(dataset => dataset.DatasetId)
            .Distinct(StringComparer.Ordinal)
            .Count() != normalized.Length)
        {
            throw new ArgumentException(
                "Input dataset identifiers must be unique.",
                nameof(datasets));
        }

        return normalized;
    }

    private static PreparedArtifact PrepareArtifact(
        string objectNamespace,
        EvidenceResearchJsonPayload payload)
    {
        var bytes = CanonicalResearchJson.Canonicalize(payload.Utf8Json);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var reference = new EvidenceArtifactReference(
            new EvidenceContentAddress(hash, bytes.LongLength, JsonMediaType),
            new EvidenceObjectNamespace(objectNamespace));
        return new PreparedArtifact(reference, bytes);
    }

    private static string NormalizeOutputName(string value)
    {
        var normalized = EvidenceResearchPackagingValue.NormalizeRequired(value, nameof(value));
        if (!normalized.Equals(normalized.ToLowerInvariant(), StringComparison.Ordinal) ||
            normalized.Any(character =>
                !(Char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException(
                "A deterministic output name must use lower-case ASCII letters, digits, '-' or '_'.",
                nameof(value));
        }

        return normalized;
    }

    private sealed record PreparedArtifact(
        EvidenceArtifactReference Reference,
        ReadOnlyMemory<byte> Bytes);

    private sealed record PreparedPackage(
        IReadOnlyList<PreparedArtifact> Artifacts,
        EvidenceResearchRunArtifactGraph Graph,
        EvidenceResearchRunManifest Manifest);
}

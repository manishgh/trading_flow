using System.Collections.Concurrent;
using System.Security.Cryptography;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Data.Evidence.Labeling;

public sealed record CatalystGroundTruthBuildRequest(
    string SourceNewsDatasetId,
    ReadOnlyMemory<byte> HumanLabelDocument,
    string RunId,
    string ConfigHash,
    string CodeVersion,
    string BuilderVersion,
    DateTimeOffset CreatedAtUtc,
    CatalystGroundTruthReadinessThresholds? ReadinessThresholds = null,
    CatalystLabelingSampleDefinition? Sampling = null);

public sealed record CatalystGroundTruthBuildResult(
    string DatasetId,
    bool AlreadyCommitted,
    long RowCount,
    EvidenceArtifactReference WorkItemExportArtifact,
    EvidenceArtifactReference HumanLabelDocumentArtifact,
    CatalystGroundTruthReadinessReport Readiness);

/// <summary>
/// Publishes human ground truth from exact committed news revisions. Human exchange artifacts
/// and the resulting Parquet partition are content addressed and verified before catalog commit.
/// Incomplete NWS-11 evidence remains publishable, but readiness and promotion stay fail-closed.
/// </summary>
public sealed class CatalystGroundTruthDatasetBuilder(
    IEvidenceCatalog catalog,
    IEvidencePartitionDataReader partitionReader,
    EvidenceParquetPartitionPublisher partitionPublisher,
    IImmutableArtifactStore artifactStore)
{
    private static readonly ConcurrentDictionary<string, BuildGate> BuildGates =
        new(StringComparer.Ordinal);

    private static readonly EvidenceObjectNamespace WorkItemsNamespace =
        new("raw/human-labeling/work-items");
    private static readonly EvidenceObjectNamespace LabelsNamespace =
        new("raw/human-labeling/labels");
    private static readonly EvidenceObjectNamespace GroundTruthNamespace =
        new("normalized/classifier-ground-truth");

    public async Task<CatalystGroundTruthBuildResult> BuildAsync(
        CatalystGroundTruthBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var source = await catalog.GetDatasetAsync(
            request.SourceNewsDatasetId,
            cancellationToken) ?? throw new InvalidOperationException(
                $"Committed news dataset '{request.SourceNewsDatasetId}' was not found.");
        ValidateSource(source, request.SourceNewsDatasetId);

        var labelDocumentHash = Hash(request.HumanLabelDocument.Span);
        var sampling =
            (request.Sampling ?? CatalystLabelingSampleDefinition.FullDataset).Validate();
        var gateKey = EvidenceCanonicalJson.ComputeSha256(new
        {
            source.DatasetId,
            labelDocumentHash,
            request.RunId,
            request.ConfigHash,
            request.CodeVersion,
            request.BuilderVersion,
            request.CreatedAtUtc,
            Thresholds = request.ReadinessThresholds ??
                new CatalystGroundTruthReadinessThresholds(),
            Sampling = sampling
        });
        using var gate = await AcquireGateAsync(gateKey, cancellationToken);
        return await BuildUnderGateAsync(request, source, cancellationToken);
    }

    private async Task<CatalystGroundTruthBuildResult> BuildUnderGateAsync(
        CatalystGroundTruthBuildRequest request,
        EvidenceDatasetManifest source,
        CancellationToken cancellationToken)
    {
        var news = await partitionReader.ReadNewsRevisionsAsync(source, cancellationToken);
        var sampling =
            (request.Sampling ?? CatalystLabelingSampleDefinition.FullDataset).Validate();
        var export = CatalystLabelingWorkItemExporter.Export(source, news, sampling);
        var imported = CatalystHumanLabelImporter.Import(
            export,
            request.HumanLabelDocument,
            request.CreatedAtUtc);
        if (imported.ResolvedLabels.Count == 0)
        {
            throw new InvalidDataException(
                "No uncontested or adjudicated labels are available for publication.");
        }

        var thresholds = (request.ReadinessThresholds ??
            new CatalystGroundTruthReadinessThresholds()).Validate();
        var readiness = new CatalystGroundTruthReadinessReport(
            thresholds,
            imported.ResolvedLabels.Count,
            imported.DoubleLabeledArticleCount,
            imported.AdjudicatedArticleCount,
            imported.UnresolvedConflictCount,
            imported.ResolvedLabels
                .GroupBy(value => value.Category)
                .ToDictionary(group => group.Key, group => group.Count()));

        var symbols = news
            .SelectMany(value => value.Symbols)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var collectionPlan = BuildCollectionPlan(
            request,
            source,
            export.Sha256,
            Hash(request.HumanLabelDocument.Span),
            symbols);
        await catalog.RegisterCollectionPlanAsync(collectionPlan, cancellationToken);

        var workItemArtifact = await PublishAndVerifyAsync(
            export.Content,
            "application/x-ndjson",
            WorkItemsNamespace,
            cancellationToken);
        var labelArtifact = await PublishAndVerifyAsync(
            request.HumanLabelDocument,
            "application/json",
            LabelsNamespace,
            cancellationToken);

        var workItemObservationId = "label-work-items-" + workItemArtifact.Content.Sha256;
        var labelObservationId = "human-labels-" + labelArtifact.Content.Sha256;
        var workItemObservation = BuildSourceObservation(
            collectionPlan,
            request,
            "human-label-work-items",
            workItemObservationId,
            workItemArtifact,
            symbols,
            source.DatasetId);
        var labelObservation = BuildSourceObservation(
            collectionPlan,
            request,
            "human-label-document",
            labelObservationId,
            labelArtifact,
            symbols,
            source.DatasetId);
        await catalog.RegisterSourceObservationAsync(
            workItemObservation,
            cancellationToken);
        await catalog.RegisterSourceObservationAsync(
            labelObservation,
            cancellationToken);

        var rows = imported.ResolvedLabels
            .Select(label => CreateRow(
                request,
                source.DatasetId,
                label,
                workItemObservationId,
                workItemArtifact.Content.Sha256,
                labelObservationId,
                labelArtifact.Content.Sha256))
            .OrderBy(row => row.GroundTruthId, StringComparer.Ordinal)
            .ToArray();
        var sourceReferences = ResolveSourceReferences(
            rows,
            workItemObservation.ToReference(),
            labelObservation.ToReference());

        var maximumResolvedAt = rows.Max(row => row.ResolvedAtUtc);
        if (maximumResolvedAt == DateTimeOffset.MaxValue)
        {
            throw new InvalidDataException(
                "Ground-truth resolution time cannot be represented as an exclusive range.");
        }

        var quality = BuildQuality(readiness);
        var partitionId = "ground-truth-" + EvidenceCanonicalJson.ComputeSha256(new
        {
            source.DatasetId,
            WorkItemExportSha256 = workItemArtifact.Content.Sha256,
            HumanLabelDocumentSha256 = labelArtifact.Content.Sha256,
            request.ConfigHash,
            request.BuilderVersion
        });
        var partition = await partitionPublisher.PublishClassifierGroundTruthAsync(
            new EvidenceParquetPartitionRequest(
                partitionId,
                EvidenceDatasetKind.ClassifierGroundTruth,
                1,
                new EvidencePartitionProvenance(
                    "human",
                    "derived/classifier-ground-truth",
                    "human-labels",
                    "none",
                    "N/A",
                    null,
                    [],
                    [],
                    symbols,
                    "event",
                    rows.Min(row => row.ResolvedAtUtc),
                    maximumResolvedAt.AddTicks(1)),
                sourceReferences,
                request.RunId,
                request.ConfigHash,
                request.BuilderVersion,
                request.CodeVersion,
                quality,
                GroundTruthNamespace,
                Dimensions: new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source_news_dataset_id"] = source.DatasetId,
                    ["work_item_export_sha256"] = workItemArtifact.Content.Sha256,
                    ["human_label_document_sha256"] = labelArtifact.Content.Sha256,
                    ["label_sampling_version"] = sampling.SamplingVersion,
                    ["label_sampling_target"] =
                        sampling.TargetWorkItemCount?.ToString(
                            System.Globalization.CultureInfo.InvariantCulture) ?? "all",
                    ["label_sampling_eligible_candidates"] =
                        export.EligibleCandidateCount.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                    ["nws11_ready"] = readiness.IsReady.ToString().ToLowerInvariant()
                }),
            rows,
            cancellationToken);

        var attributes = BuildAttributes(
            source,
            workItemArtifact,
            labelArtifact,
            readiness,
            export);
        var manifest = EvidenceParquetPartitionPublisher.BuildDatasetManifest(
            EvidenceDatasetKind.ClassifierGroundTruth,
            1,
            request.CreatedAtUtc,
            collectionPlan.JobId,
            collectionPlan.LogicalPlanHash,
            request.ConfigHash,
            request.CodeVersion,
            request.BuilderVersion,
            "human-labels",
            [partition],
            quality,
            attributes);
        var commit = await catalog.CommitDatasetAsync(manifest, cancellationToken);
        if (commit.Id.Equals(manifest.DatasetId, StringComparison.Ordinal))
        {
            return new(
                commit.Id,
                commit.AlreadyCommitted,
                rows.LongLength,
                workItemArtifact,
                labelArtifact,
                readiness);
        }

        var authoritative = await catalog.GetDatasetAsync(commit.Id, cancellationToken)
            ?? throw new InvalidDataException(
                $"Catalog returned missing ground-truth dataset '{commit.Id}'.");
        if (!authoritative.LogicalDatasetKey.Equals(
                manifest.LogicalDatasetKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Catalog resolved a conflicting ground-truth dataset logical identity.");
        }

        return new(
            commit.Id,
            true,
            authoritative.Partitions.Sum(value => value.RowCount),
            workItemArtifact,
            labelArtifact,
            readiness);
    }

    private async Task<EvidenceArtifactReference> PublishAndVerifyAsync(
        ReadOnlyMemory<byte> content,
        string mediaType,
        EvidenceObjectNamespace objectNamespace,
        CancellationToken cancellationToken)
    {
        var artifact = new EvidenceArtifactReference(
            new EvidenceContentAddress(
                Hash(content.Span),
                content.Length,
                mediaType),
            objectNamespace);
        await artifactStore.PutIfAbsentAsync(
            new ImmutableArtifactWriteRequest(artifact),
            content,
            cancellationToken);
        var verification = await artifactStore.VerifyAsync(artifact, cancellationToken);
        if (!verification.IsValid)
        {
            throw new InvalidDataException(
                $"Published human-label artifact failed verification: " +
                $"{verification.FailureReason ?? "unknown verification failure"}.");
        }

        return artifact;
    }

    private static ClassifierGroundTruthEvidenceRow CreateRow(
        CatalystGroundTruthBuildRequest request,
        string sourceNewsDatasetId,
        CatalystResolvedGroundTruthLabel label,
        string workItemObservationId,
        string workItemArtifactHash,
        string labelObservationId,
        string labelArtifactHash)
    {
        var groundTruthId = "ground-truth-" + EvidenceCanonicalJson.ComputeSha256(new
        {
            sourceNewsDatasetId,
            label.WorkItem.WorkItemId,
            label.WorkItem.NewsRevisionContentSha256,
            label.Category,
            label.Direction,
            label.DirectionIsClear,
            label.Materiality,
            label.Resolution,
            Annotations = label.Annotations.Select(value => new
            {
                value.AnnotatorId,
                value.LabeledAtUtc,
                value.Category,
                value.Direction,
                value.DirectionIsClear,
                value.Materiality
            }),
            label.Adjudication
        });
        var sources = new[]
            {
                new EvidenceRowSourceAddress(
                workItemObservationId,
                workItemArtifactHash),
                new EvidenceRowSourceAddress(
                labelObservationId,
                labelArtifactHash)
            };
        return new(
            1,
            request.RunId,
            request.ConfigHash,
            request.CodeVersion,
            "human-labels",
            groundTruthId,
            sourceNewsDatasetId,
            label.WorkItem.WorkItemId,
            label.WorkItem.NewsProvider,
            label.WorkItem.ProviderArticleId,
            label.WorkItem.NewsRevisionId,
            label.WorkItem.NewsRevisionContentSha256,
            label.Category,
            label.Direction,
            label.DirectionIsClear,
            label.Materiality,
            label.Resolution,
            label.Annotations.Select(value => value.AnnotatorId).ToArray(),
            label.Annotations.Select(value => value.LabeledAtUtc).ToArray(),
            label.Adjudication?.AdjudicatorId,
            label.Adjudication?.AdjudicatedAtUtc,
            label.Adjudication?.Rationale,
            label.WorkItem.AvailableAtUtc,
            label.ResolvedAtUtc,
            sources);
    }

    private static IReadOnlyList<EvidenceSourceReference> ResolveSourceReferences(
        IReadOnlyList<ClassifierGroundTruthEvidenceRow> rows,
        params EvidenceSourceReference[] labelSources)
    {
        var references = labelSources
            .GroupBy(value => value.ObservationId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var values = group.Distinct().ToArray();
                    if (values.Length != 1)
                    {
                        throw new InvalidDataException(
                            $"Source observation '{group.Key}' has conflicting references.");
                    }

                    return values[0];
                },
                StringComparer.Ordinal);
        return rows
            .SelectMany(row => row.Sources)
            .Select(sourceAddress =>
            {
                if (!references.TryGetValue(sourceAddress.ObservationId, out var reference) ||
                    !reference.Artifact.Content.Sha256.Equals(
                        sourceAddress.ObservationSha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Ground-truth lineage '{sourceAddress.ObservationId}' is absent or conflicting.");
                }

                return reference;
            })
            .Distinct()
            .OrderBy(value => value.ObservationId, StringComparer.Ordinal)
            .ToArray();
    }

    private static EvidenceQualityReport BuildQuality(
        CatalystGroundTruthReadinessReport readiness)
    {
        var issues = readiness.Failures
            .Select((failure, index) => new EvidenceQualityIssue(
                $"nws11_not_ready_{index + 1:D2}",
                EvidenceQualitySeverity.Warning,
                failure))
            .ToArray();
        return new EvidenceQualityReport(issues);
    }

    private static IReadOnlyDictionary<string, string> BuildAttributes(
        EvidenceDatasetManifest source,
        EvidenceArtifactReference workItems,
        EvidenceArtifactReference labels,
        CatalystGroundTruthReadinessReport readiness,
        CatalystLabelingWorkItemExport export)
    {
        var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["source_news_dataset_id"] = source.DatasetId,
            ["source_news_logical_dataset_key"] = source.LogicalDatasetKey,
            ["work_item_format_version"] = CatalystLabelingWorkItemExporter.FormatVersion,
            ["human_label_format_version"] = CatalystHumanLabelImporter.FormatVersion,
            ["work_item_export_sha256"] = workItems.Content.Sha256,
            ["work_item_export_namespace"] = workItems.ObjectNamespace.Value,
            ["label_sampling_version"] = export.Sampling.SamplingVersion,
            ["label_sampling_target"] =
                export.Sampling.TargetWorkItemCount?.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ?? "all",
            ["label_sampling_earliest_revision_per_article"] =
                export.Sampling.EarliestRevisionPerArticle.ToString().ToLowerInvariant(),
            ["label_sampling_eligible_candidates"] =
                export.EligibleCandidateCount.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ["label_sampling_selected_count"] =
                export.WorkItems.Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ["human_label_document_sha256"] = labels.Content.Sha256,
            ["human_label_document_namespace"] = labels.ObjectNamespace.Value,
            ["nws11_ready"] = readiness.IsReady.ToString().ToLowerInvariant(),
            ["nws11_valid_labels"] = readiness.ValidLabelCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["nws11_double_labeled_articles"] = readiness.DoubleLabeledArticleCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["nws11_adjudicated_articles"] = readiness.AdjudicatedArticleCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["nws11_unresolved_conflicts"] = readiness.UnresolvedConflictCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["nws11_minimum_valid_labels"] = readiness.Thresholds.MinimumValidLabels.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["nws11_minimum_double_labeled_articles"] =
                readiness.Thresholds.MinimumDoubleLabeledArticles.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ["nws11_require_every_category"] =
                readiness.Thresholds.RequireEveryCategory.ToString().ToLowerInvariant()
        };
        foreach (var pair in readiness.CategoryCounts.OrderBy(value => value.Key))
        {
            attributes[$"nws11_category_{ToSnakeCase(pair.Key)}"] =
                pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach (var failure in readiness.Failures.Select((value, index) => (value, index)))
        {
            attributes[$"nws11_failure_{failure.index + 1:D2}"] = failure.value;
        }

        return attributes;
    }

    private static EvidenceCollectionPlan BuildCollectionPlan(
        CatalystGroundTruthBuildRequest request,
        EvidenceDatasetManifest source,
        string workItemExportSha256,
        string humanLabelDocumentSha256,
        IReadOnlyList<string> symbols) =>
        new(
            request.CreatedAtUtc,
            request.RunId,
            request.ConfigHash,
            request.CodeVersion,
            request.BuilderVersion,
            "classifier-ground-truth-single-partition-v1",
            [
                new EvidenceCollectionRequest(
                    "human-label-document",
                    "human",
                    "file-import/human-label-document",
                    symbols,
                    null,
                    null,
                    "human-labels",
                    "none",
                    "N/A",
                    null,
                    new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["content_sha256"] = humanLabelDocumentSha256,
                        ["format_version"] = CatalystHumanLabelImporter.FormatVersion,
                        ["source_news_dataset_id"] = source.DatasetId
                    }),
                new EvidenceCollectionRequest(
                    "human-label-work-items",
                    "human",
                    "derived/human-label-work-items",
                    symbols,
                    null,
                    null,
                    "human-labels",
                    "none",
                    "N/A",
                    null,
                    new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["content_sha256"] = workItemExportSha256,
                        ["format_version"] = CatalystLabelingWorkItemExporter.FormatVersion,
                        ["source_news_dataset_id"] = source.DatasetId,
                        ["sampling_version"] =
                            (request.Sampling ??
                                CatalystLabelingSampleDefinition.FullDataset).SamplingVersion,
                        ["sampling_target"] =
                            (request.Sampling ??
                                CatalystLabelingSampleDefinition.FullDataset)
                            .TargetWorkItemCount?.ToString(
                                System.Globalization.CultureInfo.InvariantCulture) ?? "all"
                    })
            ]);

    private static EvidenceSourceObservation BuildSourceObservation(
        EvidenceCollectionPlan plan,
        CatalystGroundTruthBuildRequest request,
        string requestId,
        string observationId,
        EvidenceArtifactReference artifact,
        IReadOnlyList<string> symbols,
        string sourceNewsDatasetId) =>
        new(
            observationId,
            plan.JobId,
            plan.LogicalPlanHash,
            plan.CollectionAttemptHash,
            requestId,
            artifact.Content.Sha256,
            "human",
            requestId.Equals("human-label-document", StringComparison.Ordinal)
                ? "file-import/human-label-document"
                : "derived/human-label-work-items",
            EvidenceTransportKind.FileImport,
            null,
            symbols,
            null,
            null,
            "human-labels",
            "none",
            "N/A",
            null,
            artifact.Content.Sha256,
            null,
            null,
            request.CreatedAtUtc,
            artifact,
            request.RunId,
            request.ConfigHash,
            request.CodeVersion,
            requestAttributes: new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["source_news_dataset_id"] = sourceNewsDatasetId
            });

    private static void ValidateRequest(CatalystGroundTruthBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceNewsDatasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CodeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BuilderVersion);
        if (request.ConfigHash is null ||
            request.ConfigHash.Length != 64 ||
            request.ConfigHash.Any(character => !Uri.IsHexDigit(character)) ||
            !request.ConfigHash.Equals(request.ConfigHash.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Config hash must be a canonical lowercase SHA-256 value.",
                nameof(request));
        }

        if (request.CreatedAtUtc == default || request.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Dataset creation time must be a non-default UTC value.",
                nameof(request));
        }
        if (request.HumanLabelDocument.IsEmpty ||
            request.HumanLabelDocument.Length > CatalystHumanLabelImporter.MaximumImportBytes)
        {
            throw new ArgumentException(
                "Human label document is empty or exceeds the import limit.",
                nameof(request));
        }

        request.ReadinessThresholds?.Validate();
        request.Sampling?.Validate();
    }

    private static void ValidateSource(
        EvidenceDatasetManifest source,
        string expectedDatasetId)
    {
        if (!source.DatasetId.Equals(expectedDatasetId, StringComparison.Ordinal) ||
            source.Kind != EvidenceDatasetKind.NewsRevisions ||
            !source.Quality.Passed ||
            source.Partitions.Count == 0 ||
            source.Partitions.Any(partition =>
                partition.DatasetKind != EvidenceDatasetKind.NewsRevisions ||
                !partition.Quality.Passed))
        {
            throw new InvalidDataException(
                "Ground truth requires the exact quality-passed committed NewsRevisions dataset.");
        }
    }

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string ToSnakeCase(CatalystNewsCategory category)
    {
        var value = category.ToString();
        var result = new System.Text.StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && Char.IsUpper(character))
            {
                result.Append('_');
            }

            result.Append(Char.ToLowerInvariant(character));
        }

        return result.ToString();
    }

    private static async Task<BuildGateLease> AcquireGateAsync(
        string key,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var gate = BuildGates.GetOrAdd(key, _ => new BuildGate());
            lock (gate)
            {
                if (gate.Retired)
                {
                    continue;
                }

                gate.ReferenceCount++;
            }

            try
            {
                await gate.Semaphore.WaitAsync(cancellationToken);
                return new BuildGateLease(key, gate);
            }
            catch
            {
                ReleaseGate(key, gate, releaseSemaphore: false);
                throw;
            }
        }
    }

    private static void ReleaseGate(string key, BuildGate gate, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            gate.Semaphore.Release();
        }

        var remove = false;
        lock (gate)
        {
            gate.ReferenceCount--;
            if (gate.ReferenceCount == 0)
            {
                gate.Retired = true;
                remove = true;
            }
        }

        if (remove)
        {
            BuildGates.TryRemove(new KeyValuePair<string, BuildGate>(key, gate));
        }
    }

    private sealed class BuildGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class BuildGateLease(string key, BuildGate gate) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                ReleaseGate(key, gate, releaseSemaphore: true);
            }
        }
    }
}

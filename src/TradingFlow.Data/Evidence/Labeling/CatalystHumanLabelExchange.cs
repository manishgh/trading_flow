using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Labeling;

public sealed record CatalystLabelingWorkItemExport(
    ReadOnlyMemory<byte> Content,
    string Sha256,
    string SourceNewsDatasetId,
    IReadOnlyList<CatalystLabelingWorkItem> WorkItems,
    CatalystLabelingSampleDefinition Sampling,
    int EligibleCandidateCount);

public sealed record CatalystLabelingSampleDefinition(
    string SamplingVersion,
    int? TargetWorkItemCount,
    bool EarliestRevisionPerArticle)
{
    public const string FullDatasetVersion = "full-dataset-v1";
    public const string StratifiedSampleVersion = "year-symbol-round-robin-v1";

    public static CatalystLabelingSampleDefinition FullDataset { get; } =
        new(FullDatasetVersion, null, false);

    public static CatalystLabelingSampleDefinition Stratified(int targetWorkItemCount) =>
        new(StratifiedSampleVersion, targetWorkItemCount, true);

    public CatalystLabelingSampleDefinition Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SamplingVersion);
        if (SamplingVersion.Trim().Length > 128)
        {
            throw new ArgumentException(
                "Sampling version must not exceed 128 characters.",
                nameof(SamplingVersion));
        }

        if (TargetWorkItemCount is <= 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TargetWorkItemCount),
                TargetWorkItemCount,
                "Target work-item count must be between 1 and 100,000 when specified.");
        }

        return this;
    }
}

public sealed record CatalystHumanLabelTemplateExport(
    ReadOnlyMemory<byte> Content,
    string Sha256,
    string SourceNewsDatasetId,
    string WorkItemExportSha256);

public sealed record CatalystLabelingWorkItem(
    string WorkItemId,
    string SourceNewsDatasetId,
    string NewsProvider,
    string ProviderArticleId,
    string NewsRevisionId,
    string NewsRevisionContentSha256,
    string Headline,
    string? Summary,
    string ArticleUrl,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> ProviderCategories,
    DateTimeOffset PublishedAtUtc,
    DateTimeOffset ProviderUpdatedAtUtc,
    DateTimeOffset AvailableAtUtc,
    IReadOnlyList<EvidenceRowSourceAddress> Sources);

public sealed record CatalystHumanAnnotation(
    string WorkItemId,
    string NewsRevisionContentSha256,
    string AnnotatorId,
    DateTimeOffset LabeledAtUtc,
    CatalystNewsCategory Category,
    CatalystDirection Direction,
    bool DirectionIsClear,
    CatalystMateriality Materiality);

public sealed record CatalystHumanAdjudication(
    string WorkItemId,
    string NewsRevisionContentSha256,
    string AdjudicatorId,
    DateTimeOffset AdjudicatedAtUtc,
    CatalystNewsCategory Category,
    CatalystDirection Direction,
    bool DirectionIsClear,
    CatalystMateriality Materiality,
    string Rationale);

public sealed record CatalystResolvedGroundTruthLabel(
    CatalystLabelingWorkItem WorkItem,
    CatalystNewsCategory Category,
    CatalystDirection Direction,
    bool DirectionIsClear,
    CatalystMateriality Materiality,
    GroundTruthResolution Resolution,
    IReadOnlyList<CatalystHumanAnnotation> Annotations,
    CatalystHumanAdjudication? Adjudication,
    DateTimeOffset ResolvedAtUtc);

public sealed record CatalystHumanLabelImportResult(
    IReadOnlyList<CatalystResolvedGroundTruthLabel> ResolvedLabels,
    int DoubleLabeledArticleCount,
    int AdjudicatedArticleCount,
    int UnresolvedConflictCount);

public sealed class CatalystLabelImportException(
    string code,
    string message,
    Exception? innerException = null) : FormatException(message, innerException)
{
    public string Code { get; } = code;
}

/// <summary>
/// Produces a byte-stable JSON Lines work queue from exact rows in one committed
/// NewsRevisions dataset. Export order is based on immutable identity, never input order.
/// </summary>
public static class CatalystLabelingWorkItemExporter
{
    public const string FormatVersion = "tradingflow.catalyst-label-work-items.v2";

    public static CatalystLabelingWorkItemExport Export(
        EvidenceDatasetManifest sourceNewsDataset,
        IReadOnlyCollection<NewsRevisionEvidenceRow> newsRevisions,
        CatalystLabelingSampleDefinition? sampling = null)
    {
        ArgumentNullException.ThrowIfNull(sourceNewsDataset);
        ArgumentNullException.ThrowIfNull(newsRevisions);
        var validatedSampling =
            (sampling ?? CatalystLabelingSampleDefinition.FullDataset).Validate();
        if (sourceNewsDataset.Kind != EvidenceDatasetKind.NewsRevisions ||
            !sourceNewsDataset.Quality.Passed ||
            sourceNewsDataset.Partitions.Any(partition => !partition.Quality.Passed))
        {
            throw new InvalidDataException(
                "Label work items require one quality-passed committed NewsRevisions dataset.");
        }

        if (newsRevisions.Count == 0)
        {
            throw new InvalidDataException("Label work-item export cannot be empty.");
        }

        var candidates = SelectEligibleRevisions(
                newsRevisions,
                validatedSampling.EarliestRevisionPerArticle)
            .Select(revision => CreateWorkItem(sourceNewsDataset.DatasetId, revision))
            .ToArray();
        var workItems = SelectWorkItems(
            sourceNewsDataset.DatasetId,
            candidates,
            validatedSampling);
        var duplicate = workItems
            .GroupBy(value => value.WorkItemId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Duplicate immutable news revision work item '{duplicate.Key}'.");
        }

        var lines = new List<string>(workItems.Length + 1)
        {
            JsonSerializer.Serialize(
                new WorkItemExportHeader(
                    FormatVersion,
                    sourceNewsDataset.DatasetId,
                    validatedSampling.SamplingVersion,
                    validatedSampling.TargetWorkItemCount,
                    validatedSampling.EarliestRevisionPerArticle,
                    candidates.Length,
                    workItems.Length),
                CatalystLabelJson.Options)
        };
        lines.AddRange(workItems.Select(item =>
            JsonSerializer.Serialize(WorkItemJson.FromDomain(item), CatalystLabelJson.Options)));
        var content = Encoding.UTF8.GetBytes(String.Join('\n', lines) + "\n");
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return new(
            content,
            sha256,
            sourceNewsDataset.DatasetId,
            new ReadOnlyCollection<CatalystLabelingWorkItem>(workItems),
            validatedSampling,
            candidates.Length);
    }

    private static IReadOnlyList<NewsRevisionEvidenceRow> SelectEligibleRevisions(
        IReadOnlyCollection<NewsRevisionEvidenceRow> newsRevisions,
        bool earliestRevisionPerArticle)
    {
        if (!earliestRevisionPerArticle)
        {
            return newsRevisions
                .OrderBy(value => value.Provider, StringComparer.Ordinal)
                .ThenBy(value => value.ProviderArticleId, StringComparer.Ordinal)
                .ThenBy(value => value.AvailabilityTimestampUtc)
                .ThenBy(value => value.RevisionId, StringComparer.Ordinal)
                .ToArray();
        }

        return newsRevisions
            .GroupBy(
                value => (value.Provider, value.ProviderArticleId),
                ProviderArticleIdentityComparer.Instance)
            .Select(group => group
                .OrderBy(value => value.AvailabilityTimestampUtc)
                .ThenBy(value => value.ProviderUpdatedAtUtc)
                .ThenBy(value => value.RevisionId, StringComparer.Ordinal)
                .First())
            .OrderBy(value => value.Provider, StringComparer.Ordinal)
            .ThenBy(value => value.ProviderArticleId, StringComparer.Ordinal)
            .ToArray();
    }

    private static CatalystLabelingWorkItem[] SelectWorkItems(
        string sourceNewsDatasetId,
        IReadOnlyList<CatalystLabelingWorkItem> candidates,
        CatalystLabelingSampleDefinition sampling)
    {
        if (sampling.TargetWorkItemCount is null)
        {
            return candidates
                .OrderBy(value => value.WorkItemId, StringComparer.Ordinal)
                .ToArray();
        }

        var target = sampling.TargetWorkItemCount.Value;
        if (candidates.Count < target)
        {
            throw new InvalidDataException(
                $"Label sample requested {target} work items, but only " +
                $"{candidates.Count} eligible first-revision articles exist.");
        }

        var buckets = candidates
            .GroupBy(value => new SampleStratum(
                value.AvailableAtUtc.Year,
                PrimarySymbol(value.Symbols)))
            .Select(group => new SampleBucket(
                group.Key,
                SampleHash(
                    sampling.SamplingVersion,
                    sourceNewsDatasetId,
                    group.Key.Year,
                    group.Key.PrimarySymbol,
                    "bucket"),
                group
                    .OrderBy(value => SampleHash(
                        sampling.SamplingVersion,
                        sourceNewsDatasetId,
                        group.Key.Year,
                        group.Key.PrimarySymbol,
                        value.WorkItemId), StringComparer.Ordinal)
                    .ThenBy(value => value.WorkItemId, StringComparer.Ordinal)
                    .ToArray()))
            .OrderBy(value => value.Priority, StringComparer.Ordinal)
            .ThenBy(value => value.Stratum.Year)
            .ThenBy(value => value.Stratum.PrimarySymbol, StringComparer.Ordinal)
            .ToArray();

        var selected = new List<CatalystLabelingWorkItem>(target);
        for (var depth = 0; selected.Count < target; depth++)
        {
            var addedAtDepth = 0;
            foreach (var bucket in buckets)
            {
                if (depth >= bucket.Items.Count)
                {
                    continue;
                }

                selected.Add(bucket.Items[depth]);
                addedAtDepth++;
                if (selected.Count == target)
                {
                    break;
                }
            }

            if (addedAtDepth == 0)
            {
                throw new InvalidDataException(
                    "Deterministic catalyst-label sampling exhausted before reaching its target.");
            }
        }

        return selected
            .OrderBy(value => value.WorkItemId, StringComparer.Ordinal)
            .ToArray();
    }

    private static string PrimarySymbol(IReadOnlyList<string> symbols) =>
        symbols
            .Where(value => !String.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToUpperInvariant())
            .OrderBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault() ?? "__NO_SYMBOL__";

    private static string SampleHash(
        string samplingVersion,
        string sourceNewsDatasetId,
        int year,
        string primarySymbol,
        string discriminator) =>
        EvidenceCanonicalJson.ComputeSha256(new
        {
            SamplingVersion = samplingVersion,
            SourceNewsDatasetId = sourceNewsDatasetId,
            Year = year,
            PrimarySymbol = primarySymbol,
            Discriminator = discriminator
        });

    private static CatalystLabelingWorkItem CreateWorkItem(
        string sourceNewsDatasetId,
        NewsRevisionEvidenceRow revision)
    {
        var canonicalContent =
            SentimentAssessmentEvidenceRow.CreateNewsRevisionInputContent(revision);
        var contentHash =
            SentimentAssessmentEvidenceRow.ComputeInputContentSha256(canonicalContent);
        var workItemId = "catalyst-label-" + EvidenceCanonicalJson.ComputeSha256(new
        {
            SourceNewsDatasetId = sourceNewsDatasetId,
            revision.Provider,
            revision.ProviderArticleId,
            revision.RevisionId,
            NewsRevisionContentSha256 = contentHash
        });
        return new(
            workItemId,
            sourceNewsDatasetId,
            revision.Provider,
            revision.ProviderArticleId,
            revision.RevisionId,
            contentHash,
            revision.Headline,
            revision.Summary,
            revision.ArticleUrl,
            revision.Symbols,
            revision.Categories,
            revision.PublishedAtUtc,
            revision.ProviderUpdatedAtUtc,
            revision.AvailabilityTimestampUtc,
            revision.Sources);
    }

    private sealed record WorkItemExportHeader(
        [property: JsonPropertyOrder(0)] string RecordType,
        [property: JsonPropertyOrder(1)] string FormatVersion,
        [property: JsonPropertyOrder(2)] string SourceNewsDatasetId,
        [property: JsonPropertyOrder(3)] string SamplingVersion,
        [property: JsonPropertyOrder(4)] int? TargetWorkItemCount,
        [property: JsonPropertyOrder(5)] bool EarliestRevisionPerArticle,
        [property: JsonPropertyOrder(6)] int EligibleCandidateCount,
        [property: JsonPropertyOrder(7)] int WorkItemCount)
    {
        public WorkItemExportHeader(
            string formatVersion,
            string sourceNewsDatasetId,
            string samplingVersion,
            int? targetWorkItemCount,
            bool earliestRevisionPerArticle,
            int eligibleCandidateCount,
            int workItemCount) : this(
                "manifest",
                formatVersion,
                sourceNewsDatasetId,
                samplingVersion,
                targetWorkItemCount,
                earliestRevisionPerArticle,
                eligibleCandidateCount,
                workItemCount)
        {
        }
    }

    private sealed record SampleStratum(int Year, string PrimarySymbol);

    private sealed record SampleBucket(
        SampleStratum Stratum,
        string Priority,
        IReadOnlyList<CatalystLabelingWorkItem> Items);

    private sealed class ProviderArticleIdentityComparer :
        IEqualityComparer<(string Provider, string ProviderArticleId)>
    {
        public static ProviderArticleIdentityComparer Instance { get; } = new();

        public bool Equals(
            (string Provider, string ProviderArticleId) left,
            (string Provider, string ProviderArticleId) right) =>
            StringComparer.Ordinal.Equals(left.Provider, right.Provider) &&
            StringComparer.Ordinal.Equals(left.ProviderArticleId, right.ProviderArticleId);

        public int GetHashCode((string Provider, string ProviderArticleId) value) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(value.Provider),
                StringComparer.Ordinal.GetHashCode(value.ProviderArticleId));
    }

    private sealed record WorkItemJson(
        [property: JsonPropertyOrder(0)] string RecordType,
        [property: JsonPropertyOrder(1)] string WorkItemId,
        [property: JsonPropertyOrder(2)] string SourceNewsDatasetId,
        [property: JsonPropertyOrder(3)] string NewsProvider,
        [property: JsonPropertyOrder(4)] string ProviderArticleId,
        [property: JsonPropertyOrder(5)] string NewsRevisionId,
        [property: JsonPropertyOrder(6)] string NewsRevisionContentSha256,
        [property: JsonPropertyOrder(7)] string Headline,
        [property: JsonPropertyOrder(8)] string? Summary,
        [property: JsonPropertyOrder(9)] string ArticleUrl,
        [property: JsonPropertyOrder(10)] IReadOnlyList<string> Symbols,
        [property: JsonPropertyOrder(11)] IReadOnlyList<string> ProviderCategories,
        [property: JsonPropertyOrder(12)] DateTimeOffset PublishedAtUtc,
        [property: JsonPropertyOrder(13)] DateTimeOffset ProviderUpdatedAtUtc,
        [property: JsonPropertyOrder(14)] DateTimeOffset AvailableAtUtc)
    {
        public static WorkItemJson FromDomain(CatalystLabelingWorkItem value) =>
            new(
                "work_item",
                value.WorkItemId,
                value.SourceNewsDatasetId,
                value.NewsProvider,
                value.ProviderArticleId,
                value.NewsRevisionId,
                value.NewsRevisionContentSha256,
                value.Headline,
                value.Summary,
                value.ArticleUrl,
                value.Symbols,
                value.ProviderCategories,
                value.PublishedAtUtc,
                value.ProviderUpdatedAtUtc,
                value.AvailableAtUtc);
    }
}

public static class CatalystHumanLabelTemplateExporter
{
    public static CatalystHumanLabelTemplateExport Export(
        CatalystLabelingWorkItemExport workItemExport)
    {
        ArgumentNullException.ThrowIfNull(workItemExport);
        var content = JsonSerializer.SerializeToUtf8Bytes(
            new HumanLabelTemplate(
                CatalystHumanLabelImporter.FormatVersion,
                workItemExport.SourceNewsDatasetId,
                workItemExport.Sha256,
                [],
                []),
            CatalystLabelJson.Options);
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return new(
            content,
            sha256,
            workItemExport.SourceNewsDatasetId,
            workItemExport.Sha256);
    }

    private sealed record HumanLabelTemplate(
        string FormatVersion,
        string SourceNewsDatasetId,
        string WorkItemExportSha256,
        IReadOnlyList<object> Annotations,
        IReadOnlyList<object> Adjudications);
}

/// <summary>
/// Strictly parses a versioned human-label document and resolves only uncontested,
/// consensus, or explicitly adjudicated labels. It never calls an inference service.
/// </summary>
public static class CatalystHumanLabelImporter
{
    public const string FormatVersion = "tradingflow.catalyst-human-labels.v1";
    public const int MaximumImportBytes = 32 * 1024 * 1024;

    public static CatalystHumanLabelImportResult Import(
        CatalystLabelingWorkItemExport workItemExport,
        ReadOnlyMemory<byte> labelDocument,
        DateTimeOffset importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(workItemExport);
        if (importedAtUtc == default || importedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Import timestamp must be a non-default UTC value.",
                nameof(importedAtUtc));
        }
        if (labelDocument.IsEmpty || labelDocument.Length > MaximumImportBytes)
        {
            throw Error("malformed_document", "Human-label document is empty or exceeds 32 MiB.");
        }

        HumanLabelDocument document;
        try
        {
            document = JsonSerializer.Deserialize<HumanLabelDocument>(
                    labelDocument.Span,
                    CatalystLabelJson.Options)
                ?? throw Error("malformed_document", "Human-label document is JSON null.");
        }
        catch (CatalystLabelImportException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException)
        {
            throw Error("malformed_document", "Human-label document is invalid JSON.", exception);
        }

        if (!String.Equals(document.FormatVersion, FormatVersion, StringComparison.Ordinal))
        {
            throw Error(
                "unsupported_format_version",
                $"Unsupported human-label format '{document.FormatVersion}'.");
        }

        if (!String.Equals(
                document.SourceNewsDatasetId,
                workItemExport.SourceNewsDatasetId,
                StringComparison.Ordinal) ||
            !String.Equals(
                document.WorkItemExportSha256,
                workItemExport.Sha256,
                StringComparison.Ordinal))
        {
            throw Error(
                "work_item_export_mismatch",
                "Human labels do not identify the exact immutable work-item export.");
        }

        var workItems = workItemExport.WorkItems.ToDictionary(
            value => value.WorkItemId,
            StringComparer.Ordinal);
        var annotations = ValidateAnnotations(
            document.Annotations ?? [],
            workItems,
            importedAtUtc);
        var adjudications = ValidateAdjudications(
            document.Adjudications ?? [],
            workItems,
            importedAtUtc);

        var resolved = new List<CatalystResolvedGroundTruthLabel>();
        var doubleLabeled = 0;
        var adjudicated = 0;
        var unresolved = 0;
        foreach (var group in annotations
                     .GroupBy(value => value.WorkItemId, StringComparer.Ordinal)
                     .OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var orderedAnnotations = group
                .OrderBy(value => value.AnnotatorId, StringComparer.Ordinal)
                .ThenBy(value => value.LabeledAtUtc)
                .ToArray();
            if (orderedAnnotations.Length >= 2)
            {
                doubleLabeled++;
            }

            var labelsAgree = orderedAnnotations
                .Select(LabelKey)
                .Distinct()
                .Count() == 1;
            adjudications.TryGetValue(group.Key, out var itemAdjudication);
            if (labelsAgree && itemAdjudication is not null)
            {
                throw Error(
                    "unnecessary_adjudication",
                    $"Work item '{group.Key}' has consensus labels and must not be adjudicated.");
            }

            if (!labelsAgree && itemAdjudication is null)
            {
                unresolved++;
                continue;
            }

            var workItem = workItems[group.Key];
            if (itemAdjudication is not null)
            {
                adjudicated++;
                resolved.Add(new(
                    workItem,
                    itemAdjudication.Category,
                    itemAdjudication.Direction,
                    itemAdjudication.DirectionIsClear,
                    itemAdjudication.Materiality,
                    GroundTruthResolution.Adjudicated,
                    orderedAnnotations,
                    itemAdjudication,
                    itemAdjudication.AdjudicatedAtUtc));
                continue;
            }

            var first = orderedAnnotations[0];
            resolved.Add(new(
                workItem,
                first.Category,
                first.Direction,
                first.DirectionIsClear,
                first.Materiality,
                orderedAnnotations.Length == 1
                    ? GroundTruthResolution.SingleAnnotation
                    : GroundTruthResolution.AnnotatorConsensus,
                orderedAnnotations,
                null,
                orderedAnnotations.Max(value => value.LabeledAtUtc)));
        }

        var orphanAdjudication = adjudications.Keys
            .FirstOrDefault(workItemId =>
                annotations.All(annotation =>
                    !annotation.WorkItemId.Equals(workItemId, StringComparison.Ordinal)));
        if (orphanAdjudication is not null)
        {
            throw Error(
                "orphan_adjudication",
                $"Adjudication for work item '{orphanAdjudication}' has no annotations.");
        }

        return new(
            new ReadOnlyCollection<CatalystResolvedGroundTruthLabel>(
                resolved.OrderBy(value => value.WorkItem.WorkItemId, StringComparer.Ordinal).ToArray()),
            doubleLabeled,
            adjudicated,
            unresolved);
    }

    private static IReadOnlyList<CatalystHumanAnnotation> ValidateAnnotations(
        IReadOnlyList<HumanAnnotationJson> values,
        IReadOnlyDictionary<string, CatalystLabelingWorkItem> workItems,
        DateTimeOffset importedAtUtc)
    {
        var result = new List<CatalystHumanAnnotation>(values.Count);
        var seen = new Dictionary<(string WorkItemId, string AnnotatorId), CatalystHumanAnnotation>();
        foreach (var value in values)
        {
            var item = RequireWorkItem(value.WorkItemId, value.NewsRevisionContentSha256, workItems);
            ValidatePrincipal(value.AnnotatorId, nameof(value.AnnotatorId));
            ValidateTimestamp(value.LabeledAtUtc, item.AvailableAtUtc, importedAtUtc, "label");
            ValidateLabel(value.Category, value.Direction, value.DirectionIsClear, value.Materiality);
            var annotation = new CatalystHumanAnnotation(
                item.WorkItemId,
                item.NewsRevisionContentSha256,
                value.AnnotatorId.Trim(),
                value.LabeledAtUtc,
                value.Category,
                value.Direction,
                value.DirectionIsClear,
                value.Materiality);
            var key = (annotation.WorkItemId, annotation.AnnotatorId);
            if (seen.TryGetValue(key, out var existing))
            {
                var duplicate = existing == annotation;
                throw Error(
                    duplicate ? "duplicate_annotation" : "conflicting_annotation",
                    $"{(duplicate ? "Duplicate" : "Conflicting")} annotation for " +
                    $"'{annotation.WorkItemId}' by '{annotation.AnnotatorId}'.");
            }

            seen.Add(key, annotation);
            result.Add(annotation);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, CatalystHumanAdjudication> ValidateAdjudications(
        IReadOnlyList<HumanAdjudicationJson> values,
        IReadOnlyDictionary<string, CatalystLabelingWorkItem> workItems,
        DateTimeOffset importedAtUtc)
    {
        var result = new Dictionary<string, CatalystHumanAdjudication>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var item = RequireWorkItem(value.WorkItemId, value.NewsRevisionContentSha256, workItems);
            ValidatePrincipal(value.AdjudicatorId, nameof(value.AdjudicatorId));
            ValidateTimestamp(value.AdjudicatedAtUtc, item.AvailableAtUtc, importedAtUtc, "adjudication");
            ValidateLabel(value.Category, value.Direction, value.DirectionIsClear, value.Materiality);
            if (String.IsNullOrWhiteSpace(value.Rationale))
            {
                throw Error("missing_adjudication_rationale", "Adjudication rationale is required.");
            }

            var adjudication = new CatalystHumanAdjudication(
                item.WorkItemId,
                item.NewsRevisionContentSha256,
                value.AdjudicatorId.Trim(),
                value.AdjudicatedAtUtc,
                value.Category,
                value.Direction,
                value.DirectionIsClear,
                value.Materiality,
                value.Rationale.Trim());
            if (result.TryGetValue(item.WorkItemId, out var existing))
            {
                var duplicate = existing == adjudication;
                throw Error(
                    duplicate ? "duplicate_adjudication" : "conflicting_adjudication",
                    $"{(duplicate ? "Duplicate" : "Conflicting")} adjudication for '{item.WorkItemId}'.");
            }

            result.Add(item.WorkItemId, adjudication);
        }

        return result;
    }

    private static CatalystLabelingWorkItem RequireWorkItem(
        string workItemId,
        string contentSha256,
        IReadOnlyDictionary<string, CatalystLabelingWorkItem> workItems)
    {
        if (String.IsNullOrWhiteSpace(workItemId) ||
            !workItems.TryGetValue(workItemId.Trim(), out var workItem))
        {
            throw Error("unknown_work_item", $"Unknown work item '{workItemId}'.");
        }

        if (!String.Equals(
                contentSha256,
                workItem.NewsRevisionContentSha256,
                StringComparison.Ordinal))
        {
            throw Error(
                "news_revision_hash_mismatch",
                $"Work item '{workItem.WorkItemId}' does not match the labeled news revision hash.");
        }

        return workItem;
    }

    private static void ValidateLabel(
        CatalystNewsCategory category,
        CatalystDirection direction,
        bool directionIsClear,
        CatalystMateriality materiality)
    {
        if (!Enum.IsDefined(category) ||
            !Enum.IsDefined(direction) ||
            !Enum.IsDefined(materiality))
        {
            throw Error("invalid_label_value", "Category, direction, or materiality is invalid.");
        }

        if (directionIsClear && direction is CatalystDirection.Neutral or CatalystDirection.Ambiguous)
        {
            throw Error(
                "invalid_clear_direction",
                "A clear-direction annotation must be positive or negative.");
        }
    }

    private static void ValidatePrincipal(string value, string fieldName)
    {
        if (String.IsNullOrWhiteSpace(value) || value.Trim().Length > 128)
        {
            throw Error("invalid_principal", $"{fieldName} is required and must not exceed 128 characters.");
        }
    }

    private static void ValidateTimestamp(
        DateTimeOffset value,
        DateTimeOffset minimum,
        DateTimeOffset maximum,
        string name)
    {
        if (value == default ||
            value.Offset != TimeSpan.Zero ||
            value < minimum ||
            value > maximum)
        {
            throw Error(
                $"invalid_{name}_timestamp",
                $"{name} timestamp must be UTC, after news availability, and no later than import.");
        }
    }

    private static (CatalystNewsCategory, CatalystDirection, bool, CatalystMateriality) LabelKey(
        CatalystHumanAnnotation value) =>
        (value.Category, value.Direction, value.DirectionIsClear, value.Materiality);

    private static CatalystLabelImportException Error(
        string code,
        string message,
        Exception? innerException = null) =>
        new(code, message, innerException);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record HumanLabelDocument(
        string FormatVersion,
        string SourceNewsDatasetId,
        string WorkItemExportSha256,
        IReadOnlyList<HumanAnnotationJson>? Annotations,
        IReadOnlyList<HumanAdjudicationJson>? Adjudications);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record HumanAnnotationJson(
        string WorkItemId,
        string NewsRevisionContentSha256,
        string AnnotatorId,
        DateTimeOffset LabeledAtUtc,
        CatalystNewsCategory Category,
        CatalystDirection Direction,
        bool DirectionIsClear,
        CatalystMateriality Materiality);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record HumanAdjudicationJson(
        string WorkItemId,
        string NewsRevisionContentSha256,
        string AdjudicatorId,
        DateTimeOffset AdjudicatedAtUtc,
        CatalystNewsCategory Category,
        CatalystDirection Direction,
        bool DirectionIsClear,
        CatalystMateriality Materiality,
        string Rationale);
}

internal static class CatalystLabelJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 16,
            WriteIndented = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.SnakeCaseLower,
            allowIntegerValues: false));
        return options;
    }
}

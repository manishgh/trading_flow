using System.Collections.ObjectModel;

namespace TradingFlow.Domain.Research;

public enum CatalystNewsCategory
{
    Earnings = 1,
    Guidance = 2,
    MergerAcquisition = 3,
    Regulatory = 4,
    CapitalStructure = 5,
    MajorCommercialEvent = 6,
    Management = 7,
    AnalystAction = 8,
    Legal = 9,
    Operational = 10,
    MacroSector = 11,
    PromotionalLowInformation = 12,
    Unknown = 13
}

public enum CatalystDirection
{
    Positive = 1,
    Negative = 2,
    Neutral = 3,
    Ambiguous = 4
}

public enum CatalystMateriality
{
    Low = 1,
    Medium = 2,
    High = 3
}

public enum GroundTruthResolution
{
    SingleAnnotation = 1,
    AnnotatorConsensus = 2,
    Adjudicated = 3
}

public sealed record ClassifierGroundTruthEvidenceRow : INormalizedEvidenceRow
{
    public ClassifierGroundTruthEvidenceRow(
        int schemaVersion,
        string runId,
        string configHash,
        string codeVersion,
        string dataFeed,
        string groundTruthId,
        string sourceNewsDatasetId,
        string workItemId,
        string newsProvider,
        string providerArticleId,
        string newsRevisionId,
        string newsRevisionContentSha256,
        CatalystNewsCategory category,
        CatalystDirection direction,
        bool directionIsClear,
        CatalystMateriality materiality,
        GroundTruthResolution resolution,
        IReadOnlyList<string> annotatorIds,
        IReadOnlyList<DateTimeOffset> annotationTimestampsUtc,
        string? adjudicatorId,
        DateTimeOffset? adjudicatedAtUtc,
        string? adjudicationRationale,
        DateTimeOffset newsAvailableAtUtc,
        DateTimeOffset resolvedAtUtc,
        IReadOnlyList<EvidenceRowSourceAddress> sources)
    {
        SchemaVersion = EvidenceRowContract.NormalizeSchemaVersion(schemaVersion);
        RunId = EvidenceRowContract.NormalizeRequired(runId, nameof(runId));
        ConfigHash = EvidenceRowContract.NormalizeSha256(configHash);
        CodeVersion = EvidenceRowContract.NormalizeRequired(codeVersion, nameof(codeVersion));
        DataFeed = EvidenceRowContract.NormalizeRequired(dataFeed, nameof(dataFeed)).ToLowerInvariant();
        GroundTruthId = EvidenceRowContract.NormalizeRequired(groundTruthId, nameof(groundTruthId));
        SourceNewsDatasetId = EvidenceRowContract.NormalizeRequired(
            sourceNewsDatasetId,
            nameof(sourceNewsDatasetId));
        WorkItemId = EvidenceRowContract.NormalizeRequired(workItemId, nameof(workItemId));
        NewsProvider = EvidenceRowContract.NormalizeRequired(newsProvider, nameof(newsProvider))
            .ToLowerInvariant();
        ProviderArticleId = EvidenceRowContract.NormalizeRequired(
            providerArticleId,
            nameof(providerArticleId));
        NewsRevisionId = EvidenceRowContract.NormalizeRequired(newsRevisionId, nameof(newsRevisionId));
        NewsRevisionContentSha256 = EvidenceRowContract.NormalizeSha256(newsRevisionContentSha256);
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        if (!Enum.IsDefined(materiality))
        {
            throw new ArgumentOutOfRangeException(nameof(materiality));
        }

        if (!Enum.IsDefined(resolution))
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }

        if (directionIsClear && direction is CatalystDirection.Neutral or CatalystDirection.Ambiguous)
        {
            throw new ArgumentException(
                "A clear direction must be positive or negative.",
                nameof(directionIsClear));
        }

        Category = category;
        Direction = direction;
        DirectionIsClear = directionIsClear;
        Materiality = materiality;
        Resolution = resolution;

        var normalizedAnnotators = (annotatorIds ?? throw new ArgumentNullException(nameof(annotatorIds)))
            .Select(value => EvidenceRowContract.NormalizeRequired(value, nameof(annotatorIds)))
            .ToArray();
        var normalizedTimestamps = (annotationTimestampsUtc ??
            throw new ArgumentNullException(nameof(annotationTimestampsUtc))).ToArray();
        if (normalizedAnnotators.Length == 0 ||
            normalizedAnnotators.Length != normalizedTimestamps.Length)
        {
            throw new ArgumentException(
                "Ground truth requires matching non-empty annotator and timestamp provenance.");
        }

        if (normalizedAnnotators.Distinct(StringComparer.Ordinal).Count() != normalizedAnnotators.Length)
        {
            throw new ArgumentException("Annotator identifiers must be unique.", nameof(annotatorIds));
        }

        foreach (var timestamp in normalizedTimestamps)
        {
            EvidenceRowContract.EnsureUtc(timestamp, nameof(annotationTimestampsUtc));
        }

        var ordered = normalizedAnnotators
            .Zip(normalizedTimestamps)
            .OrderBy(value => value.First, StringComparer.Ordinal)
            .ThenBy(value => value.Second)
            .ToArray();
        AnnotatorIds = new ReadOnlyCollection<string>(ordered.Select(value => value.First).ToArray());
        AnnotationTimestampsUtc = new ReadOnlyCollection<DateTimeOffset>(
            ordered.Select(value => value.Second).ToArray());

        AdjudicatorId = String.IsNullOrWhiteSpace(adjudicatorId) ? null : adjudicatorId.Trim();
        AdjudicatedAtUtc = EvidenceRowContract.OptionalUtc(adjudicatedAtUtc, nameof(adjudicatedAtUtc));
        AdjudicationRationale = String.IsNullOrWhiteSpace(adjudicationRationale)
            ? null
            : adjudicationRationale.Trim();
        if (resolution == GroundTruthResolution.Adjudicated)
        {
            if (AnnotatorIds.Count < 2 ||
                AdjudicatorId is null ||
                AdjudicatedAtUtc is null ||
                AdjudicationRationale is null)
            {
                throw new ArgumentException(
                    "Adjudicated ground truth requires two annotations and complete adjudicator provenance.");
            }
        }
        else if (AdjudicatorId is not null ||
                 AdjudicatedAtUtc is not null ||
                 AdjudicationRationale is not null)
        {
            throw new ArgumentException(
                "Non-adjudicated ground truth cannot carry adjudication provenance.");
        }

        if (resolution == GroundTruthResolution.SingleAnnotation && AnnotatorIds.Count != 1)
        {
            throw new ArgumentException("Single-annotation resolution requires exactly one annotator.");
        }

        if (resolution == GroundTruthResolution.AnnotatorConsensus && AnnotatorIds.Count < 2)
        {
            throw new ArgumentException("Consensus resolution requires at least two annotators.");
        }

        NewsAvailableAtUtc = EvidenceRowContract.RequireUtc(
            newsAvailableAtUtc,
            nameof(newsAvailableAtUtc));
        ResolvedAtUtc = EvidenceRowContract.RequireUtc(resolvedAtUtc, nameof(resolvedAtUtc));
        if (AnnotationTimestampsUtc.Any(value => value < NewsAvailableAtUtc) ||
            AdjudicatedAtUtc < AnnotationTimestampsUtc.Max() ||
            ResolvedAtUtc < AnnotationTimestampsUtc.Max() ||
            ResolvedAtUtc < AdjudicatedAtUtc)
        {
            throw new ArgumentException(
                "Label, adjudication, and resolution timestamps violate causal ordering.");
        }

        Sources = EvidenceRowContract.CopySources(sources);
    }

    public int SchemaVersion { get; }
    public string RunId { get; }
    public string ConfigHash { get; }
    public string CodeVersion { get; }
    public string DataFeed { get; }
    public string GroundTruthId { get; }
    public string SourceNewsDatasetId { get; }
    public string WorkItemId { get; }
    public string NewsProvider { get; }
    public string ProviderArticleId { get; }
    public string NewsRevisionId { get; }
    public string NewsRevisionContentSha256 { get; }
    public CatalystNewsCategory Category { get; }
    public CatalystDirection Direction { get; }
    public bool DirectionIsClear { get; }
    public CatalystMateriality Materiality { get; }
    public GroundTruthResolution Resolution { get; }
    public IReadOnlyList<string> AnnotatorIds { get; }
    public IReadOnlyList<DateTimeOffset> AnnotationTimestampsUtc { get; }
    public string? AdjudicatorId { get; }
    public DateTimeOffset? AdjudicatedAtUtc { get; }
    public string? AdjudicationRationale { get; }
    public DateTimeOffset NewsAvailableAtUtc { get; }
    public DateTimeOffset ResolvedAtUtc { get; }
    public IReadOnlyList<EvidenceRowSourceAddress> Sources { get; }
    public DateTimeOffset SourceTimestampUtc => ResolvedAtUtc;
}

public sealed record CatalystGroundTruthReadinessThresholds(
    int MinimumValidLabels = 500,
    int MinimumDoubleLabeledArticles = 100,
    bool RequireEveryCategory = true)
{
    public CatalystGroundTruthReadinessThresholds Validate()
    {
        if (MinimumValidLabels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumValidLabels));
        }

        if (MinimumDoubleLabeledArticles < 0 ||
            MinimumDoubleLabeledArticles > MinimumValidLabels)
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumDoubleLabeledArticles));
        }

        return this;
    }
}

public sealed class CatalystGroundTruthReadinessReport
{
    public CatalystGroundTruthReadinessReport(
        CatalystGroundTruthReadinessThresholds thresholds,
        int validLabelCount,
        int doubleLabeledArticleCount,
        int adjudicatedArticleCount,
        int unresolvedConflictCount,
        IReadOnlyDictionary<CatalystNewsCategory, int> categoryCounts)
    {
        Thresholds = (thresholds ?? throw new ArgumentNullException(nameof(thresholds))).Validate();
        if (validLabelCount < 0 ||
            doubleLabeledArticleCount < 0 ||
            adjudicatedArticleCount < 0 ||
            unresolvedConflictCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(validLabelCount));
        }

        ValidLabelCount = validLabelCount;
        DoubleLabeledArticleCount = doubleLabeledArticleCount;
        AdjudicatedArticleCount = adjudicatedArticleCount;
        UnresolvedConflictCount = unresolvedConflictCount;
        CategoryCounts = new ReadOnlyDictionary<CatalystNewsCategory, int>(
            Enum.GetValues<CatalystNewsCategory>()
                .ToDictionary(
                    category => category,
                    category => categoryCounts?.GetValueOrDefault(category) ?? 0));

        var failures = new List<string>();
        if (validLabelCount < Thresholds.MinimumValidLabels)
        {
            failures.Add(
                $"valid_labels_below_minimum:{validLabelCount}/{Thresholds.MinimumValidLabels}");
        }

        if (doubleLabeledArticleCount < Thresholds.MinimumDoubleLabeledArticles)
        {
            failures.Add(
                "double_labeled_articles_below_minimum:" +
                $"{doubleLabeledArticleCount}/{Thresholds.MinimumDoubleLabeledArticles}");
        }

        if (unresolvedConflictCount > 0)
        {
            failures.Add($"unresolved_annotation_conflicts:{unresolvedConflictCount}");
        }

        if (Thresholds.RequireEveryCategory)
        {
            failures.AddRange(CategoryCounts
                .Where(pair => pair.Value == 0)
                .OrderBy(pair => pair.Key)
                .Select(pair => $"category_missing:{pair.Key}"));
        }

        Failures = new ReadOnlyCollection<string>(
            failures.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    public CatalystGroundTruthReadinessThresholds Thresholds { get; }
    public int ValidLabelCount { get; }
    public int DoubleLabeledArticleCount { get; }
    public int AdjudicatedArticleCount { get; }
    public int UnresolvedConflictCount { get; }
    public IReadOnlyDictionary<CatalystNewsCategory, int> CategoryCounts { get; }
    public IReadOnlyList<string> Failures { get; }
    public bool IsReady => Failures.Count == 0;
    public bool CatalystPromotionAllowed => IsReady;
}

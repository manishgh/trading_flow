using System.Globalization;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Labeling;

public sealed record CatalystGroundTruthVerificationReport(
    CatalystGroundTruthReadinessReport Readiness,
    IReadOnlyList<string> IntegrityFailures)
{
    public bool IsVerified =>
        IntegrityFailures.Count == 0;

    public bool CatalystPromotionAllowed =>
        IsVerified && Readiness.CatalystPromotionAllowed;

    public IReadOnlyList<string> Failures =>
        IntegrityFailures
            .Concat(Readiness.Failures)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
}

/// <summary>
/// Recomputes NWS-11 readiness from immutable ground-truth rows and compares the
/// result with catalog metadata. Missing, malformed, weakened, or inconsistent
/// metadata fails closed and cannot authorize catalyst-dependent promotion.
/// </summary>
public static class CatalystGroundTruthPromotionGate
{
    private const int RequiredMinimumValidLabels = 500;
    private const int RequiredMinimumDoubleLabeledArticles = 100;

    public static CatalystGroundTruthVerificationReport Evaluate(
        EvidenceDatasetManifest manifest,
        IReadOnlyList<ClassifierGroundTruthEvidenceRow> rows,
        string? expectedSourceNewsDatasetId = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(rows);

        var failures = new List<string>();
        if (manifest.Kind != EvidenceDatasetKind.ClassifierGroundTruth)
        {
            failures.Add("dataset_kind_is_not_classifier_ground_truth");
        }

        if (!manifest.Quality.Passed ||
            manifest.Partitions.Count == 0 ||
            manifest.Partitions.Any(partition =>
                partition.DatasetKind != EvidenceDatasetKind.ClassifierGroundTruth ||
                !partition.Quality.Passed))
        {
            failures.Add("ground_truth_dataset_quality_not_passed");
        }

        var declaredRowCount = manifest.Partitions.Sum(partition => partition.RowCount);
        if (declaredRowCount != rows.Count)
        {
            failures.Add(
                $"ground_truth_row_count_mismatch:{rows.Count}/{declaredRowCount}");
        }

        AddDuplicateFailure(
            rows.Select(row => row.GroundTruthId),
            "duplicate_ground_truth_id",
            failures);
        AddDuplicateFailure(
            rows.Select(row => row.WorkItemId),
            "duplicate_work_item_id",
            failures);

        var sourceDatasetId = RequiredAttribute(
            manifest,
            "source_news_dataset_id",
            failures);
        if (!String.IsNullOrWhiteSpace(expectedSourceNewsDatasetId) &&
            !expectedSourceNewsDatasetId.Equals(
                sourceDatasetId,
                StringComparison.Ordinal))
        {
            failures.Add("source_news_dataset_id_does_not_match_requested_dataset");
        }

        if (!String.IsNullOrWhiteSpace(sourceDatasetId) &&
            rows.Any(row => !row.SourceNewsDatasetId.Equals(
                sourceDatasetId,
                StringComparison.Ordinal)))
        {
            failures.Add("ground_truth_rows_reference_mixed_news_datasets");
        }

        RequireExactAttribute(
            manifest,
            "work_item_format_version",
            CatalystLabelingWorkItemExporter.FormatVersion,
            failures);
        RequireExactAttribute(
            manifest,
            "human_label_format_version",
            CatalystHumanLabelImporter.FormatVersion,
            failures);
        RequireSha256Attribute(manifest, "work_item_export_sha256", failures);
        RequireSha256Attribute(manifest, "human_label_document_sha256", failures);

        var configuredMinimumLabels = ParseNonNegativeInteger(
            manifest,
            "nws11_minimum_valid_labels",
            failures);
        var configuredMinimumDoubleLabels = ParseNonNegativeInteger(
            manifest,
            "nws11_minimum_double_labeled_articles",
            failures);
        var requireEveryCategory = ParseBoolean(
            manifest,
            "nws11_require_every_category",
            failures);
        if (configuredMinimumLabels is { } minimumLabels &&
            minimumLabels < RequiredMinimumValidLabels)
        {
            failures.Add(
                $"nws11_minimum_valid_labels_weakened:{minimumLabels}/" +
                $"{RequiredMinimumValidLabels}");
        }

        if (configuredMinimumDoubleLabels is { } minimumDoubleLabels &&
            minimumDoubleLabels < RequiredMinimumDoubleLabeledArticles)
        {
            failures.Add(
                $"nws11_minimum_double_labeled_articles_weakened:" +
                $"{minimumDoubleLabels}/{RequiredMinimumDoubleLabeledArticles}");
        }

        if (requireEveryCategory is false)
        {
            failures.Add("nws11_every_category_requirement_disabled");
        }

        var thresholds = new CatalystGroundTruthReadinessThresholds(
            Math.Max(
                RequiredMinimumValidLabels,
                configuredMinimumLabels ?? RequiredMinimumValidLabels),
            Math.Max(
                RequiredMinimumDoubleLabeledArticles,
                configuredMinimumDoubleLabels ?? RequiredMinimumDoubleLabeledArticles),
            true);
        var doubleLabeledCount = rows.Count(row => row.AnnotatorIds.Count >= 2);
        var adjudicatedCount = rows.Count(row =>
            row.Resolution == GroundTruthResolution.Adjudicated);
        var unresolvedConflictCount = ParseNonNegativeInteger(
            manifest,
            "nws11_unresolved_conflicts",
            failures) ?? Int32.MaxValue;
        var categoryCounts = Enum.GetValues<CatalystNewsCategory>()
            .ToDictionary(
                category => category,
                category => rows.Count(row => row.Category == category));
        var readiness = new CatalystGroundTruthReadinessReport(
            thresholds,
            rows.Count,
            doubleLabeledCount,
            adjudicatedCount,
            unresolvedConflictCount,
            categoryCounts);

        RequireCount(
            manifest,
            "nws11_valid_labels",
            rows.Count,
            failures);
        RequireCount(
            manifest,
            "nws11_double_labeled_articles",
            doubleLabeledCount,
            failures);
        RequireCount(
            manifest,
            "nws11_adjudicated_articles",
            adjudicatedCount,
            failures);
        foreach (var pair in categoryCounts.OrderBy(value => value.Key))
        {
            RequireCount(
                manifest,
                $"nws11_category_{ToSnakeCase(pair.Key)}",
                pair.Value,
                failures);
        }

        var declaredReady = ParseBoolean(
            manifest,
            "nws11_ready",
            failures);
        if (declaredReady is { } ready && ready != readiness.IsReady)
        {
            failures.Add(
                $"nws11_ready_mismatch:{ready.ToString().ToLowerInvariant()}/" +
                $"{readiness.IsReady.ToString().ToLowerInvariant()}");
        }

        return new(
            readiness,
            failures
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());
    }

    private static void AddDuplicateFailure(
        IEnumerable<string> values,
        string failure,
        ICollection<string> failures)
    {
        if (values
            .GroupBy(value => value, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            failures.Add(failure);
        }
    }

    private static string? RequiredAttribute(
        EvidenceDatasetManifest manifest,
        string name,
        ICollection<string> failures)
    {
        if (!manifest.Attributes.TryGetValue(name, out var value) ||
            String.IsNullOrWhiteSpace(value))
        {
            failures.Add($"required_manifest_attribute_missing:{name}");
            return null;
        }

        return value;
    }

    private static void RequireExactAttribute(
        EvidenceDatasetManifest manifest,
        string name,
        string expected,
        ICollection<string> failures)
    {
        var value = RequiredAttribute(manifest, name, failures);
        if (value is not null &&
            !value.Equals(expected, StringComparison.Ordinal))
        {
            failures.Add($"manifest_attribute_mismatch:{name}");
        }
    }

    private static void RequireSha256Attribute(
        EvidenceDatasetManifest manifest,
        string name,
        ICollection<string> failures)
    {
        var value = RequiredAttribute(manifest, name, failures);
        if (value is not null &&
            (value.Length != 64 ||
             value.Any(character => !Uri.IsHexDigit(character)) ||
             !value.Equals(value.ToLowerInvariant(), StringComparison.Ordinal)))
        {
            failures.Add($"manifest_attribute_is_not_canonical_sha256:{name}");
        }
    }

    private static int? ParseNonNegativeInteger(
        EvidenceDatasetManifest manifest,
        string name,
        ICollection<string> failures)
    {
        var value = RequiredAttribute(manifest, name, failures);
        if (value is null)
        {
            return null;
        }

        if (!Int32.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed < 0)
        {
            failures.Add($"manifest_attribute_is_not_non_negative_integer:{name}");
            return null;
        }

        return parsed;
    }

    private static bool? ParseBoolean(
        EvidenceDatasetManifest manifest,
        string name,
        ICollection<string> failures)
    {
        var value = RequiredAttribute(manifest, name, failures);
        if (value is null)
        {
            return null;
        }

        if (!Boolean.TryParse(value, out var parsed))
        {
            failures.Add($"manifest_attribute_is_not_boolean:{name}");
            return null;
        }

        return parsed;
    }

    private static void RequireCount(
        EvidenceDatasetManifest manifest,
        string name,
        int actual,
        ICollection<string> failures)
    {
        var declared = ParseNonNegativeInteger(manifest, name, failures);
        if (declared is { } count && count != actual)
        {
            failures.Add($"manifest_count_mismatch:{name}:{actual}/{count}");
        }
    }

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
}

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Labeling;

public enum NewsAssessmentTextSupport { HeadlineOnly = 1, SummarySupported = 2, BodySupported = 3 }
public enum NewsAssessmentScope { Unknown = 0, Company = 1, Sector = 2, Global = 3 }
public enum NewsAssessmentContextCategory { Unknown = 0, Company = 1, Sector = 2, Global = 3, Financial = 4, Policy = 5 }
public enum NewsAssessmentAvailabilityBasis { ObservedReceipt = 1, ConservativeProviderUpdated = 2 }

public sealed record NewsAssessmentAvailabilityContract(NewsAssessmentAvailabilityBasis Basis, int ReconstructionDelaySeconds = 0)
{
    public const string Version = "news-assessment-availability-v1";
    public void Validate() {
        if (!Enum.IsDefined(Basis) || ReconstructionDelaySeconds is < 0 or > 86400 ||
            Basis == NewsAssessmentAvailabilityBasis.ObservedReceipt && ReconstructionDelaySeconds != 0)
            throw new InvalidDataException("Availability requires observed receipt or an explicit bounded diagnostic provider-update delay.");
    }
}
public sealed record NewsAssessmentSourceFile(string Path, string Sha256);
public sealed record NewsAssessmentExcludedRevision(string ArticleId, string RevisionContentSha256,
    string ReceiptPath, string Reason);
public sealed record NewsAssessmentWorkItem(CatalystLabelingWorkItem Item, string? Body,
    NewsAssessmentTextSupport TextSupport, DateTimeOffset ObservedReceiptAtUtc,
    DateTimeOffset? ReconstructedArticleAvailableAtUtc, string ReceiptPath, string PayloadPath,
    int SourceRowIndex, bool HeldOut, bool IndependentSecondLabelRequired);
public sealed record NewsAssessmentPacket(string FormatVersion, string ProtocolVersion, string SourceSummarySha256,
    string SamplingVersion, NewsAssessmentAvailabilityContract AvailabilityContract,
    ImmutableArray<NewsAssessmentWorkItem> WorkItems, ImmutableArray<NewsAssessmentExcludedRevision> ExcludedRevisions,
    ImmutableArray<NewsAssessmentSourceFile> SourceFiles, int EligibleRevisionCount,
    bool HistoricalFirstReceiptProven = false, bool AutomaticTradingActivationAllowed = false);

public sealed record NewsAssessmentLabel(CatalystNewsCategory Category, CatalystDirection Direction,
    bool DirectionIsClear, CatalystMateriality Materiality, NewsAssessmentScope Scope,
    NewsAssessmentContextCategory ContextCategory, ImmutableArray<string> Targets, bool Adverse,
    NewsAssessmentTextSupport EvidenceSupport, ImmutableArray<string> ReadingCategories,
    ImmutableArray<string> ReadingSectors);
public sealed record NewsAssessmentAnnotation(string WorkItemId, string NewsRevisionContentSha256,
    string AnnotatorId, DateTimeOffset LabeledAtUtc, NewsAssessmentLabel Label);
public sealed record NewsAssessmentAdjudication(string WorkItemId, string NewsRevisionContentSha256,
    string AdjudicatorId, DateTimeOffset AdjudicatedAtUtc, NewsAssessmentLabel Label, string Rationale);
public sealed record NewsAssessmentHumanDocument(string FormatVersion, string PacketSha256,
    string HumanProvenanceDeclaration, ImmutableArray<NewsAssessmentAnnotation> Annotations,
    ImmutableArray<NewsAssessmentAdjudication> Adjudications);
public sealed record NewsAssessmentResolvedLabel(string WorkItemId, NewsAssessmentLabel Label,
    ImmutableArray<string> AnnotatorIds, DateTimeOffset ResolvedAtUtc, string Resolution);
public sealed record NewsAssessmentImportResult(string PacketSha256, string HumanDocumentSha256,
    ImmutableArray<NewsAssessmentResolvedLabel> ResolvedLabels, int DoubleLabeledArticleCount,
    int UnresolvedConflictCount, ImmutableArray<string> ReadinessFailures, bool HumanCountAndCoverageRequirementsSatisfied,
    bool PromotionAllowed = false, bool TradingActivationAllowed = false);

public sealed record NewsAssessmentPrediction(string WorkItemId, string NewsRevisionContentSha256,
    DateTimeOffset CompletedAtUtc, ImmutableArray<string> ReadingCategories, ImmutableArray<string> ReadingSectors,
    NewsAssessmentLabel? CatalystLabel, string? EconomicAbstentionReason);
public sealed record NewsAssessmentPredictionDocument(string FormatVersion, string PacketSha256,
    string ClassifierVersion, string ClassifierArtifactSha256, string Task,
    DateTimeOffset ModelFrozenAtUtc, ImmutableArray<string> TrainingWorkItemIds,
    ImmutableArray<NewsAssessmentPrediction> Predictions);
public sealed record NewsAssessmentScore(string Group, int HumanLabelCount, int PredictionCount,
    decimal? ReadingExactSetAccuracy, decimal? EconomicCategoryAccuracy, int ClearDirectionCount,
    decimal? ClearDirectionAccuracy, decimal? ContextScopeAccuracy);
public sealed record NewsAssessmentReadingMetric(string Task, string Label, int HumanPositiveCount,
    int PredictedPositiveCount, int TruePositiveCount, int FalsePositiveCount, int FalseNegativeCount,
    decimal? Precision, decimal? Recall);
public sealed record NewsAssessmentEvaluation(string PacketSha256, string HumanDocumentSha256,
    string PredictionDocumentSha256, string ClassifierVersion, string ClassifierArtifactSha256,
    DateTimeOffset EvaluatedAtUtc, DateTimeOffset? EarliestObservedClassificationKnownAtUtc,
    ImmutableArray<NewsAssessmentScore> Scores, ImmutableArray<NewsAssessmentReadingMetric> ReadingMetrics,
    ImmutableArray<string> Failures,
    bool Nws12MeasuredThresholdsSatisfied, bool PromotionAllowed = false,
    bool TradingActivationAllowed = false, bool HistoricalClassifierAvailabilityProven = false);

/// <summary>Offline, immutable assessment. It neither changes operator storage nor calls a classifier/provider.</summary>
public static class NewsAssessmentWorkflow
{
    public const string PacketFormat = "tradingflow.news-assessment-packet.v1";
    public const string HumanFormat = "tradingflow.news-assessment-human-labels.v1";
    public const string PredictionFormat = "tradingflow.news-assessment-predictions.v1";
    public const string ProtocolVersion = "catalyst-human-labeling-v1";
    public const string SamplingVersion = "stored-revision-month-symbol-content-round-robin-v1";
    public static JsonSerializerOptions JsonOptions { get; } = new(CatalystLabelJson.Options) {
        WriteIndented = true, MaxDepth = 32, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
    public static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
    public static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    public static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    public static T Read<T>(ReadOnlyMemory<byte> bytes) {
        using var document = JsonDocument.Parse(bytes);
        UniqueProperties(document.RootElement);
        return JsonSerializer.Deserialize<T>(bytes.Span, JsonOptions) ?? throw new InvalidDataException("Assessment document is null.");
    }
    private static void UniqueProperties(JsonElement value) {
        if (value.ValueKind == JsonValueKind.Object) {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) {
                if (!seen.Add(property.Name)) throw new InvalidDataException("Duplicate assessment JSON field: " + property.Name);
                UniqueProperties(property.Value);
            }
        } else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) UniqueProperties(child);
    }

    public static NewsAssessmentPacket Export(string collectionRoot, string summaryPath,
        NewsAssessmentAvailabilityContract availability, int sampleSize = 500)
    {
        availability.Validate();
        if (sampleSize < 1 || sampleSize > 100000) throw new ArgumentOutOfRangeException(nameof(sampleSize));
        collectionRoot = Path.GetFullPath(collectionRoot);
        summaryPath = Resolve(collectionRoot, summaryPath);
        var summaryHash = FileHash(summaryPath);
        using var summary = JsonDocument.Parse(File.ReadAllBytes(summaryPath));
        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal) { [summaryPath] = summaryHash };
        var candidates = new List<NewsAssessmentWorkItem>();
        var excluded = new List<NewsAssessmentExcludedRevision>();
        var receiptPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in summary.RootElement.GetProperty("Windows").EnumerateArray()) {
            if (!window.GetProperty("PaginationComplete").GetBoolean() || window.GetProperty("Error").ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("Assessment requires a fully paginated archived source window; HTTP200 alone is insufficient.");
            foreach (var entry in window.GetProperty("Receipts").EnumerateArray()) receiptPaths.Add(Resolve(collectionRoot, entry.GetString()!));
        }
        foreach (var receiptPath in receiptPaths.Order(StringComparer.Ordinal)) {
            using var receipt = JsonDocument.Parse(File.ReadAllBytes(receiptPath));
            var r = receipt.RootElement;
            if (r.GetProperty("HttpStatus").GetInt32() != 200 || r.GetProperty("HistoricalFirstReceiptClaim").GetBoolean())
                throw new InvalidDataException("Archive receipt must retain HTTP200 and must not claim a historical first delivery.");
            var received = Clock(r.GetProperty("CapturedUtc").GetString()!);
            var payloadPath = Resolve(collectionRoot, r.GetProperty("Payload").GetString()!);
            var payloadHash = FileHash(payloadPath);
            if (payloadHash != r.GetProperty("PayloadSha256").GetString()) throw new InvalidDataException("Archived news payload hash changed.");
            sources[receiptPath] = FileHash(receiptPath); sources[payloadPath] = payloadHash;
            using var payload = JsonDocument.Parse(File.ReadAllBytes(payloadPath));
            var index = 0;
            foreach (var article in payload.RootElement.GetProperty("news").EnumerateArray()) {
                var contentHash = Hash(Encoding.UTF8.GetBytes(article.GetRawText()));
                var id = article.GetProperty("id").ToString();
                var created = Clock(article.GetProperty("created_at").GetString()!);
                var updated = Clock(article.GetProperty("updated_at").GetString()!);
                if (updated < created || updated > received || created > received) {
                    excluded.Add(new(id, contentHash, receiptPath, updated < created
                        ? "provider_update_precedes_creation" : "provider_clock_after_observed_receipt"));
                    index++; continue;
                }
                var headline = article.GetProperty("headline").GetString();
                if (String.IsNullOrWhiteSpace(headline)) { excluded.Add(new(id, contentHash, receiptPath, "headline_unavailable")); index++; continue; }
                var body = Text(article, "content"); var description = Text(article, "summary");
                var support = !String.IsNullOrWhiteSpace(body) ? NewsAssessmentTextSupport.BodySupported :
                    !String.IsNullOrWhiteSpace(description) ? NewsAssessmentTextSupport.SummarySupported : NewsAssessmentTextSupport.HeadlineOnly;
                var symbols = Strings(article, "symbols").Select(symbol => symbol.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var workId = "news-assessment-" + EvidenceCanonicalJson.ComputeSha256(new { summaryHash, Provider = "alpaca", id, contentHash });
                var item = new CatalystLabelingWorkItem(workId, summaryHash, "alpaca", id, "raw-" + contentHash[..24], contentHash,
                    headline, description, article.GetProperty("url").GetString()!, symbols, Strings(article, "categories"),
                    created, updated, received, []);
                candidates.Add(new(item, body, support, received,
                    availability.Basis == NewsAssessmentAvailabilityBasis.ConservativeProviderUpdated ? updated.AddSeconds(availability.ReconstructionDelaySeconds) : null,
                    receiptPath, payloadPath, index, false, false));
                index++;
            }
        }
        // Earliest STORED revision/receipt, never a claim to possess the original historical revision.
        // Conflicting contents with the same provider update clock have no revision order.
        // Exclude the whole article instead of choosing a content hash as a fake chronology.
        var ambiguousArticles = candidates.GroupBy(item => (item.Item.NewsProvider, item.Item.ProviderArticleId))
            .Where(group => group.GroupBy(item => item.Item.ProviderUpdatedAtUtc)
                .Any(revisions => revisions.Select(item => item.Item.NewsRevisionContentSha256).Distinct(StringComparer.Ordinal).Count() > 1))
            .Select(group => group.Key).ToHashSet();
        foreach (var item in candidates.Where(item => ambiguousArticles.Contains((item.Item.NewsProvider, item.Item.ProviderArticleId))))
            excluded.Add(new(item.Item.ProviderArticleId, item.Item.NewsRevisionContentSha256, item.ReceiptPath, "ambiguous_same_update_revision_contents"));
        var eligible = candidates.Where(item => !ambiguousArticles.Contains((item.Item.NewsProvider, item.Item.ProviderArticleId)))
            .GroupBy(item => (item.Item.NewsProvider, item.Item.ProviderArticleId))
            .Select(group => group.OrderBy(item => item.Item.ProviderUpdatedAtUtc).ThenBy(item => item.ObservedReceiptAtUtc)
                .ThenBy(item => item.Item.NewsRevisionContentSha256, StringComparer.Ordinal).First()).ToArray();
        if (eligible.Length < sampleSize) throw new InvalidDataException($"Requested {sampleSize} assessment items but only {eligible.Length} valid stored article revisions exist.");
        var buckets = eligible.GroupBy(item => $"{item.Item.ProviderUpdatedAtUtc:yyyy-MM}|{item.Item.Symbols.FirstOrDefault() ?? "__UNTAGGED__"}|{item.TextSupport}")
            .OrderBy(group => EvidenceCanonicalJson.ComputeSha256(new { SamplingVersion, Stratum = group.Key }), StringComparer.Ordinal)
            .Select(group => new Queue<NewsAssessmentWorkItem>(group.OrderBy(item => item.Item.WorkItemId, StringComparer.Ordinal))).ToArray();
        var selected = new List<NewsAssessmentWorkItem>(sampleSize);
        while (selected.Count < sampleSize) foreach (var bucket in buckets) {
            if (selected.Count == sampleSize) break;
            if (bucket.TryDequeue(out var item)) selected.Add(item);
        }
        var heldout = selected.OrderBy(item => EvidenceCanonicalJson.ComputeSha256(new { Partition = "heldout-v1", item.Item.WorkItemId }), StringComparer.Ordinal)
            .Take(Math.Max(1, sampleSize / 5)).Select(item => item.Item.WorkItemId).ToHashSet(StringComparer.Ordinal);
        var doubles = selected.OrderBy(item => EvidenceCanonicalJson.ComputeSha256(new { Partition = "second-independent-label-v1", item.Item.WorkItemId }), StringComparer.Ordinal)
            .Take(Math.Min(100, sampleSize)).Select(item => item.Item.WorkItemId).ToHashSet(StringComparer.Ordinal);
        return new(PacketFormat, ProtocolVersion, summaryHash, SamplingVersion, availability,
            selected.OrderBy(item => item.Item.WorkItemId, StringComparer.Ordinal).Select(item => item with {
                HeldOut = heldout.Contains(item.Item.WorkItemId), IndependentSecondLabelRequired = doubles.Contains(item.Item.WorkItemId) }).ToImmutableArray(),
            excluded.Distinct().OrderBy(item => item.ArticleId, StringComparer.Ordinal).ThenBy(item => item.ReceiptPath, StringComparer.Ordinal).ToImmutableArray(),
            sources.Select(item => new NewsAssessmentSourceFile(item.Key, item.Value)).ToImmutableArray(), eligible.Length);
    }

    public static NewsAssessmentHumanDocument EmptyHumanDocument(string packetSha256) => new(HumanFormat,
        packetSha256, "HUMAN_COMPLETION_REQUIRED", [], []);

    public static NewsAssessmentImportResult Import(NewsAssessmentPacket packet, string packetSha256,
        NewsAssessmentHumanDocument document, DateTimeOffset importedAtUtc)
    {
        ValidatePacket(packet, packetSha256); Utc(importedAtUtc);
        if (document.FormatVersion != HumanFormat || document.PacketSha256 != packetSha256 || document.Annotations.IsDefault || document.Adjudications.IsDefault)
            throw new InvalidDataException("Human document does not bind the exact assessment packet.");
        if (document.Annotations.Length + document.Adjudications.Length > 0 && document.HumanProvenanceDeclaration != "independent_human_no_future_data")
            throw new InvalidDataException("Nonempty labels require the explicit independent-human/no-future-data declaration.");
        var items = packet.WorkItems.ToDictionary(item => item.Item.WorkItemId, StringComparer.Ordinal);
        // Reuse the binding v1 importer for economic enums, direction clarity,
        // principal identity, exact revision hashes, duplicate annotations and clocks.
        ValidateBase(packet, packetSha256, document.Annotations.Select(annotation => new BaseAnnotation(annotation.WorkItemId,
            annotation.NewsRevisionContentSha256, annotation.AnnotatorId, annotation.LabeledAtUtc, annotation.Label)), importedAtUtc);
        ValidateBase(packet, packetSha256, document.Adjudications.Select(adjudication => new BaseAnnotation(adjudication.WorkItemId,
            adjudication.NewsRevisionContentSha256, adjudication.AdjudicatorId, adjudication.AdjudicatedAtUtc, adjudication.Label)), importedAtUtc);
        var annotations = document.Annotations.Select(annotation => annotation with { AnnotatorId = annotation.AnnotatorId.Trim(),
            Label = ValidateLabel(annotation.Label, items[annotation.WorkItemId]) }).ToArray();
        var adjudications = document.Adjudications.ToDictionary(item => item.WorkItemId, item => item with {
            Label = ValidateLabel(item.Label, items[item.WorkItemId]) }, StringComparer.Ordinal);
        var resolved = new List<NewsAssessmentResolvedLabel>(); var doubles = 0; var conflicts = 0;
        foreach (var group in annotations.GroupBy(item => item.WorkItemId).OrderBy(group => group.Key, StringComparer.Ordinal)) {
            var rows = group.OrderBy(item => item.AnnotatorId, StringComparer.Ordinal).ToArray();
            if (rows.Length >= 2) doubles++;
            var agree = rows.Select(item => Hash(Bytes(item.Label))).Distinct(StringComparer.Ordinal).Count() == 1;
            adjudications.TryGetValue(group.Key, out var adjudication);
            if (agree && adjudication is not null) throw new InvalidDataException("Consensus assessment must not be adjudicated.");
            if (!agree && adjudication is null) { conflicts++; continue; }
            if (adjudication is not null && (String.IsNullOrWhiteSpace(adjudication.Rationale) ||
                adjudication.AdjudicatedAtUtc < rows.Max(item => item.LabeledAtUtc)))
                throw new InvalidDataException("Assessment adjudication needs a rationale and a timestamp after every independent annotation.");
            resolved.Add(new(group.Key, adjudication?.Label ?? rows[0].Label, rows.Select(item => item.AnnotatorId).ToImmutableArray(),
                adjudication?.AdjudicatedAtUtc ?? rows.Max(item => item.LabeledAtUtc),
                adjudication is not null ? "adjudicated" : rows.Length >= 2 ? "independent_consensus" : "single_human"));
        }
        if (adjudications.Keys.Except(annotations.Select(item => item.WorkItemId), StringComparer.Ordinal).Any())
            throw new InvalidDataException("Assessment adjudication has no contributing annotations.");
        var legacy = new CatalystGroundTruthReadinessReport(new(), resolved.Count, doubles,
            resolved.Count(item => item.Resolution == "adjudicated"), conflicts,
            resolved.GroupBy(item => item.Label.Category).ToDictionary(group => group.Key, group => group.Count()));
        var failures = legacy.Failures.ToList();
        foreach (var item in packet.WorkItems.Where(item => item.IndependentSecondLabelRequired))
            if (annotations.Count(row => row.WorkItemId == item.Item.WorkItemId) < 2)
                failures.Add("assigned_independent_second_label_missing:" + item.Item.WorkItemId);
        foreach (var category in Enum.GetValues<NewsAssessmentContextCategory>().Where(value => value != NewsAssessmentContextCategory.Unknown))
            if (!resolved.Any(item => item.Label.ContextCategory == category)) failures.Add("context_category_missing:" + category);
        foreach (var scope in Enum.GetValues<NewsAssessmentScope>().Where(value => value != NewsAssessmentScope.Unknown))
            if (!resolved.Any(item => item.Label.Scope == scope)) failures.Add("event_scope_missing:" + scope);
        foreach (var support in packet.WorkItems.Select(item => item.TextSupport).Distinct())
            if (!resolved.Any(item => items[item.WorkItemId].TextSupport == support)) failures.Add("text_support_missing:" + support);
        return new(packetSha256, Hash(Bytes(document)), resolved.ToImmutableArray(), doubles, conflicts,
            failures.Order(StringComparer.Ordinal).ToImmutableArray(), failures.Count == 0);
    }

    public static NewsAssessmentPredictionDocument PredictDisplayBaseline(NewsAssessmentPacket packet,
        string packetSha256, DateTimeOffset completedAtUtc)
    {
        ValidatePacket(packet, packetSha256); Utc(completedAtUtc);
        var predictions = packet.WorkItems.Where(item => item.HeldOut).Select(item => {
            if (completedAtUtc < item.ObservedReceiptAtUtc) throw new InvalidDataException("A classifier applied now cannot complete before the archived download.");
            var topics = NewsDisplayTaxonomy.Describe(new PersistedNewsItem { Ticker = item.Item.Symbols.Count == 0 ? "MARKET" : String.Join(',', item.Item.Symbols),
                Provider = item.Item.NewsProvider, Headline = item.Item.Headline, Summary = item.Item.Summary });
            return new NewsAssessmentPrediction(item.Item.WorkItemId, item.Item.NewsRevisionContentSha256, completedAtUtc,
                topics.Categories.ToImmutableArray(), topics.Sectors.ToImmutableArray(), null,
                "display_topics_do_not_implement_13_economic_categories_direction_materiality_scope_or_adverse_event_predictions");
        }).ToImmutableArray();
        return new(PredictionFormat, packetSha256, NewsDisplayTaxonomy.Version, FileHash(typeof(NewsDisplayTaxonomy).Assembly.Location),
            "reading_topics_headline_summary_provider_ticker_only", completedAtUtc, [], predictions);
    }

    public static NewsAssessmentEvaluation Evaluate(NewsAssessmentPacket packet, string packetSha256,
        NewsAssessmentHumanDocument humans, NewsAssessmentPredictionDocument predictions, DateTimeOffset evaluatedAtUtc)
    {
        var imported = Import(packet, packetSha256, humans, evaluatedAtUtc);
        Utc(predictions.ModelFrozenAtUtc);
        if (predictions.FormatVersion != PredictionFormat || predictions.PacketSha256 != packetSha256 ||
            String.IsNullOrWhiteSpace(predictions.ClassifierVersion) || !IsHash(predictions.ClassifierArtifactSha256) ||
            predictions.Predictions.IsDefault || predictions.TrainingWorkItemIds.IsDefault || predictions.ModelFrozenAtUtc > evaluatedAtUtc)
            throw new InvalidDataException("Classifier prediction identity, artifact hash, packet or clock is incomplete.");
        var heldout = packet.WorkItems.Where(item => item.HeldOut).ToDictionary(item => item.Item.WorkItemId, StringComparer.Ordinal);
        if (predictions.TrainingWorkItemIds.Intersect(heldout.Keys, StringComparer.Ordinal).Any())
            throw new InvalidDataException("Held-out assessment items were declared as classifier training data.");
        if (predictions.TrainingWorkItemIds.Any(String.IsNullOrWhiteSpace) ||
            predictions.TrainingWorkItemIds.Distinct(StringComparer.Ordinal).Count() != predictions.TrainingWorkItemIds.Length)
            throw new InvalidDataException("Classifier training identities must be explicit and distinct.");
        var predicted = predictions.Predictions.ToDictionary(item => item.WorkItemId, StringComparer.Ordinal);
        if (!predicted.Keys.Order(StringComparer.Ordinal).SequenceEqual(heldout.Keys.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Classifier predictions must cover the exact frozen held-out set once.");
        foreach (var prediction in predicted.Values) {
            Utc(prediction.CompletedAtUtc);
            var item = heldout[prediction.WorkItemId];
            if (prediction.NewsRevisionContentSha256 != item.Item.NewsRevisionContentSha256 || prediction.CompletedAtUtc < item.ObservedReceiptAtUtc ||
                prediction.CompletedAtUtc < predictions.ModelFrozenAtUtc || prediction.CompletedAtUtc > evaluatedAtUtc)
                throw new InvalidDataException("Classifier revision hash or actual classification completion time is invalid.");
            ValidateReading(prediction.ReadingCategories, prediction.ReadingSectors);
            if (prediction.CatalystLabel is { } label) {
                _ = ValidateLabel(label, item);
                ValidateBase(packet, packetSha256, [new(prediction.WorkItemId, prediction.NewsRevisionContentSha256,
                    "external-classifier", prediction.CompletedAtUtc, label)], evaluatedAtUtc);
            } else if (String.IsNullOrWhiteSpace(prediction.EconomicAbstentionReason))
                throw new InvalidDataException("Absent economic predictions must declare abstention instead of fabricating neutral labels.");
        }
        var gold = imported.ResolvedLabels.Where(item => heldout.ContainsKey(item.WorkItemId)).ToArray();
        var scores = new List<NewsAssessmentScore> { Score("all_held_out", gold, predicted) };
        foreach (var group in gold.GroupBy(item => (heldout[item.WorkItemId].TextSupport, item.Label.Scope)))
            scores.Add(Score($"{group.Key.TextSupport}/{group.Key.Scope}", group.ToArray(), predicted));
        var failures = imported.ReadinessFailures.ToList();
        // A hash supplied after seeing the answers is not a blind assessment freeze.
        var firstHumanLabel = humans.Annotations.Length == 0 ? (DateTimeOffset?)null : humans.Annotations.Min(item => item.LabeledAtUtc);
        if (firstHumanLabel is { } first && (predictions.ModelFrozenAtUtc > first || predicted.Values.Any(item => item.CompletedAtUtc > first)))
            failures.Add("blind_predictions_not_frozen_before_first_human_label");
        if (gold.Length != heldout.Count) failures.Add("held_out_human_labels_incomplete");
        foreach (var score in scores) {
            if (score.PredictionCount != score.HumanLabelCount) failures.Add("economic_predictions_abstained_or_missing:" + score.Group);
            if (score.EconomicCategoryAccuracy is null or < .85m) failures.Add("nws12_category_accuracy_not_met:" + score.Group);
            if (score.ClearDirectionAccuracy is null or < .90m) failures.Add("nws12_clear_direction_accuracy_not_met:" + score.Group);
        }
        return new(packetSha256, imported.HumanDocumentSha256, Hash(Bytes(predictions)), predictions.ClassifierVersion,
            predictions.ClassifierArtifactSha256, evaluatedAtUtc,
            predicted.Count == 0 ? null : predicted.Values.Min(item => item.CompletedAtUtc), scores.ToImmutableArray(), ReadingMetrics(gold, predicted),
            failures.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray(), failures.Count == 0);
    }

    private static ImmutableArray<NewsAssessmentReadingMetric> ReadingMetrics(IReadOnlyList<NewsAssessmentResolvedLabel> gold,
        IReadOnlyDictionary<string, NewsAssessmentPrediction> predictions)
    {
        var result = new List<NewsAssessmentReadingMetric>();
        void Add(string task, IEnumerable<string> keys, Func<NewsAssessmentLabel, ImmutableArray<string>> human,
            Func<NewsAssessmentPrediction, ImmutableArray<string>> predicted) {
            foreach (var key in keys) {
                var tp = gold.Count(row => human(row.Label).Contains(key) && predicted(predictions[row.WorkItemId]).Contains(key));
                var fp = gold.Count(row => !human(row.Label).Contains(key) && predicted(predictions[row.WorkItemId]).Contains(key));
                var fn = gold.Count(row => human(row.Label).Contains(key) && !predicted(predictions[row.WorkItemId]).Contains(key));
                result.Add(new(task, key, tp + fn, tp + fp, tp, fp, fn,
                    tp + fp == 0 ? null : (decimal)tp / (tp + fp), tp + fn == 0 ? null : (decimal)tp / (tp + fn)));
            }
        }
        Add("reading_category", NewsDisplayTaxonomy.Categories.Where(item => item.Key != "all").Select(item => item.Key),
            label => label.ReadingCategories, prediction => prediction.ReadingCategories);
        Add("reading_sector_topic", NewsDisplayTaxonomy.Sectors.Select(item => item.Key),
            label => label.ReadingSectors, prediction => prediction.ReadingSectors);
        return result.ToImmutableArray();
    }

    private static NewsAssessmentScore Score(string name, IReadOnlyList<NewsAssessmentResolvedLabel> gold,
        IReadOnlyDictionary<string, NewsAssessmentPrediction> predictions)
    {
        decimal? Ratio(int numerator, int count) => count == 0 ? null : (decimal)numerator / count;
        var economic = gold.Where(item => predictions[item.WorkItemId].CatalystLabel is not null).ToArray();
        var clear = gold.Where(item => item.Label.DirectionIsClear).ToArray();
        return new(name, gold.Count, economic.Length,
            Ratio(gold.Count(item => item.Label.ReadingCategories.Order(StringComparer.Ordinal).SequenceEqual(predictions[item.WorkItemId].ReadingCategories.Order(StringComparer.Ordinal))), gold.Count),
            Ratio(economic.Count(item => item.Label.Category == predictions[item.WorkItemId].CatalystLabel!.Category), economic.Length),
            clear.Length, Ratio(clear.Count(item => predictions[item.WorkItemId].CatalystLabel is { } label && label.DirectionIsClear && label.Direction == item.Label.Direction), clear.Length),
            Ratio(economic.Count(item => item.Label.Scope == predictions[item.WorkItemId].CatalystLabel!.Scope &&
                item.Label.ContextCategory == predictions[item.WorkItemId].CatalystLabel!.ContextCategory), economic.Length));
    }

    private sealed record BaseAnnotation(string WorkItemId, string RevisionHash, string ActorId, DateTimeOffset AtUtc, NewsAssessmentLabel Label);
    private static void ValidateBase(NewsAssessmentPacket packet, string sha, IEnumerable<BaseAnnotation> values, DateTimeOffset now)
    {
        var rows = values.ToArray();
        if (rows.Any(item => item.Label is null)) throw new InvalidDataException("Assessment label is missing.");
        var export = new CatalystLabelingWorkItemExport(Bytes(packet), sha, packet.SourceSummarySha256,
            packet.WorkItems.Select(item => item.Item).ToArray(), new(SamplingVersion, packet.WorkItems.Length, true), packet.EligibleRevisionCount);
        var projection = Bytes(new { FormatVersion = CatalystHumanLabelImporter.FormatVersion, SourceNewsDatasetId = packet.SourceSummarySha256,
            WorkItemExportSha256 = sha, Annotations = rows.Select(item => new { item.WorkItemId,
                NewsRevisionContentSha256 = item.RevisionHash, AnnotatorId = item.ActorId, LabeledAtUtc = item.AtUtc,
                item.Label.Category, item.Label.Direction, item.Label.DirectionIsClear, item.Label.Materiality }).ToArray(), Adjudications = Array.Empty<object>() });
        _ = CatalystHumanLabelImporter.Import(export, projection, now);
    }
    private static NewsAssessmentLabel ValidateLabel(NewsAssessmentLabel label, NewsAssessmentWorkItem item)
    {
        if (!Enum.IsDefined(label.Scope) || !Enum.IsDefined(label.ContextCategory) || label.EvidenceSupport != item.TextSupport || label.Targets.IsDefault ||
            label.Targets.Any(String.IsNullOrWhiteSpace) || label.Targets.Distinct(StringComparer.Ordinal).Count() != label.Targets.Length)
            throw new InvalidDataException("Assessment scope, evidence support or targets are invalid.");
        var valid = label.Scope switch {
            NewsAssessmentScope.Unknown => label.Targets.Length == 0 && label.ContextCategory == NewsAssessmentContextCategory.Unknown,
            NewsAssessmentScope.Company => label.Targets.Length > 0 && label.Targets.All(target => item.Item.Symbols.Contains(target, StringComparer.Ordinal)) &&
                label.ContextCategory is NewsAssessmentContextCategory.Company or NewsAssessmentContextCategory.Financial or NewsAssessmentContextCategory.Policy,
            NewsAssessmentScope.Sector => label.Targets.Length > 0 && label.ContextCategory is NewsAssessmentContextCategory.Sector or NewsAssessmentContextCategory.Financial or NewsAssessmentContextCategory.Policy,
            NewsAssessmentScope.Global => label.Targets.SequenceEqual(new[] { "*" }) && label.ContextCategory is NewsAssessmentContextCategory.Global or NewsAssessmentContextCategory.Financial or NewsAssessmentContextCategory.Policy,
            _ => false };
        if (!valid) throw new InvalidDataException("Human event scope/target/context labels contradict their evidence contract.");
        ValidateReading(label.ReadingCategories, label.ReadingSectors);
        return label with { Targets = label.Targets.Order(StringComparer.Ordinal).ToImmutableArray(),
            ReadingCategories = label.ReadingCategories.Order(StringComparer.Ordinal).ToImmutableArray(), ReadingSectors = label.ReadingSectors.Order(StringComparer.Ordinal).ToImmutableArray() };
    }
    private static void ValidateReading(ImmutableArray<string> categories, ImmutableArray<string> sectors) {
        if (categories.IsDefaultOrEmpty || sectors.IsDefault || categories.Distinct(StringComparer.Ordinal).Count() != categories.Length ||
            sectors.Distinct(StringComparer.Ordinal).Count() != sectors.Length || categories.Any(value => !NewsDisplayTaxonomy.Categories.Any(option => option.Key == value && value != "all")) ||
            sectors.Any(value => !NewsDisplayTaxonomy.Sectors.Any(option => option.Key == value)) || categories.Contains("market") && categories.Length != 1)
            throw new InvalidDataException("Reading-topic labels require valid distinct display taxonomy keys; market is the exclusive fallback.");
    }
    public static void ValidatePacket(NewsAssessmentPacket packet, string sha) {
        if (packet.FormatVersion != PacketFormat || packet.ProtocolVersion != ProtocolVersion || packet.WorkItems.IsDefaultOrEmpty ||
            Hash(Bytes(packet)) != sha || !IsHash(packet.SourceSummarySha256) || packet.SourceFiles.IsDefaultOrEmpty ||
            packet.SourceFiles.Any(file => !IsHash(file.Sha256)) || packet.ExcludedRevisions.IsDefault ||
            packet.EligibleRevisionCount < packet.WorkItems.Length || packet.SamplingVersion != SamplingVersion ||
            packet.HistoricalFirstReceiptProven || packet.AutomaticTradingActivationAllowed)
            throw new InvalidDataException("Assessment packet identity or historical-availability claims are invalid.");
        packet.AvailabilityContract.Validate();
        if (packet.WorkItems.Select(item => item.Item.WorkItemId).Distinct(StringComparer.Ordinal).Count() != packet.WorkItems.Length)
            throw new InvalidDataException("Assessment packet contains duplicate work items.");
        foreach (var item in packet.WorkItems) {
            Utc(item.ObservedReceiptAtUtc); Utc(item.Item.PublishedAtUtc); Utc(item.Item.ProviderUpdatedAtUtc);
            var support = !String.IsNullOrWhiteSpace(item.Body) ? NewsAssessmentTextSupport.BodySupported :
                !String.IsNullOrWhiteSpace(item.Item.Summary) ? NewsAssessmentTextSupport.SummarySupported : NewsAssessmentTextSupport.HeadlineOnly;
            DateTimeOffset? reconstruction = packet.AvailabilityContract.Basis == NewsAssessmentAvailabilityBasis.ConservativeProviderUpdated
                ? item.Item.ProviderUpdatedAtUtc.AddSeconds(packet.AvailabilityContract.ReconstructionDelaySeconds) : null;
            if (item.Item.ProviderUpdatedAtUtc < item.Item.PublishedAtUtc || item.Item.ProviderUpdatedAtUtc > item.ObservedReceiptAtUtc ||
                item.Item.AvailableAtUtc != item.ObservedReceiptAtUtc || !IsHash(item.Item.NewsRevisionContentSha256) ||
                item.Item.SourceNewsDatasetId != packet.SourceSummarySha256 || item.TextSupport != support ||
                item.ReconstructedArticleAvailableAtUtc != reconstruction || item.SourceRowIndex < 0 ||
                !packet.SourceFiles.Any(file => file.Path == item.ReceiptPath) || !packet.SourceFiles.Any(file => file.Path == item.PayloadPath))
                throw new InvalidDataException("Timing-conflicted article cannot enter an assessment packet.");
        }
    }
    private static string Resolve(string root, string path) {
        var resolved = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path.Replace('\\', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Assessment source path escaped its collection root.");
        return resolved;
    }
    private static DateTimeOffset Clock(string value) { var parsed = DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture); Utc(parsed); return parsed; }
    private static void Utc(DateTimeOffset value) { if (value == default || value.Offset != TimeSpan.Zero) throw new InvalidDataException("Assessment clocks must be non-default UTC."); }
    private static bool IsHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind != JsonValueKind.Null ? field.GetString() : null;
    private static string[] Strings(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Array
        ? field.EnumerateArray().Select(item => item.GetString() ?? throw new InvalidDataException("Null source string.")).ToArray() : [];
}

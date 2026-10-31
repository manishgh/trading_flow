using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using TradingFlow.Cli;
using TradingFlow.Data.Evidence.Labeling;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class NewsAssessmentWorkflowTests
{
    private static readonly DateTimeOffset Receipt = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LabelTime = Receipt.AddHours(1);

    [Fact]
    public void ExportPreservesBodyAndActualReceiptSeparatelyFromDiagnosticReconstruction()
    {
        using var archive = new Archive();
        archive.Write(Article(1), Article(2, summary: "Headline context"), Article(3, body: "Exact archived body"));
        var observed = archive.Export(3);
        var diagnostic = archive.Export(3, new(NewsAssessmentAvailabilityBasis.ConservativeProviderUpdated, 60));
        Assert.Equal(3, observed.WorkItems.Select(item => item.TextSupport).Distinct().Count());
        Assert.Equal("Exact archived body", observed.WorkItems.Single(item => item.Item.ProviderArticleId == "3").Body);
        Assert.All(observed.WorkItems, item => { Assert.Equal(Receipt, item.Item.AvailableAtUtc); Assert.Null(item.ReconstructedArticleAvailableAtUtc); });
        Assert.All(diagnostic.WorkItems, item => {
            Assert.Equal(Receipt, item.ObservedReceiptAtUtc);
            Assert.Equal(item.Item.ProviderUpdatedAtUtc.AddSeconds(60), item.ReconstructedArticleAvailableAtUtc);
        });
        Assert.False(diagnostic.HistoricalFirstReceiptProven);
        var bytes = NewsAssessmentWorkflow.Bytes(diagnostic);
        var parsed = NewsAssessmentWorkflow.Read<NewsAssessmentPacket>(bytes);
        NewsAssessmentWorkflow.ValidatePacket(parsed, NewsAssessmentWorkflow.Hash(bytes));
        Assert.Equal(bytes, NewsAssessmentWorkflow.Bytes(parsed));
    }

    [Fact]
    public void ReexportIsByteIdenticalAndQuarantinesTimingInversionsAndAmbiguousRevisions()
    {
        using var archive = new Archive();
        archive.Write(Article(1), Article(61242877, updated: "2026-08-17T11:55:31Z", created: "2026-08-17T11:56:00Z"),
            Article(2, headline: "First revision"), Article(2, headline: "Conflicting revision with same clock"),
            Article(3, updated: "2026-10-20T00:00:00Z"));
        var packet = archive.Export(1);
        Assert.Equal(NewsAssessmentWorkflow.Bytes(packet), NewsAssessmentWorkflow.Bytes(archive.Export(1)));
        Assert.Equal("1", Assert.Single(packet.WorkItems).Item.ProviderArticleId);
        Assert.Contains(packet.ExcludedRevisions, item => item.ArticleId == "61242877" && item.Reason == "provider_update_precedes_creation");
        Assert.Contains(packet.ExcludedRevisions, item => item.ArticleId == "2" && item.Reason == "ambiguous_same_update_revision_contents");
        Assert.Contains(packet.ExcludedRevisions, item => item.ArticleId == "3" && item.Reason == "provider_clock_after_observed_receipt");
    }

    [Fact]
    public void InvalidPayloadHashAndEscapingSourcePathsFailClosed()
    {
        using var archive = new Archive(); archive.Write(Article(1));
        File.AppendAllText(archive.PayloadPath, " ");
        Assert.Throws<InvalidDataException>(() => archive.Export(1));
        archive.Write(Article(1));
        File.WriteAllText(archive.SummaryPath, JsonSerializer.Serialize(new { Windows = new[] {
            new { PaginationComplete = true, Error = (string?)null, Receipts = new[] { "../outside.json" } } } }));
        Assert.Throws<InvalidDataException>(() => archive.Export(1));
    }

    [Fact]
    public void BlankLabelsRetainTheOriginalFiveHundredAndOneHundredRequirements()
    {
        using var archive = new Archive(); archive.Write(Article(1));
        var packet = archive.Export(1); var sha = Sha(packet);
        var result = NewsAssessmentWorkflow.Import(packet, sha, NewsAssessmentWorkflow.EmptyHumanDocument(sha), LabelTime);
        Assert.Empty(result.ResolvedLabels);
        Assert.False(result.HumanCountAndCoverageRequirementsSatisfied);
        Assert.Contains("valid_labels_below_minimum:0/500", result.ReadinessFailures);
        Assert.Contains("double_labeled_articles_below_minimum:0/100", result.ReadinessFailures);
        Assert.False(result.PromotionAllowed); Assert.False(result.TradingActivationAllowed);
    }

    [Theory]
    [InlineData("context")]
    [InlineData("reading")]
    [InlineData("adverse")]
    public void DisagreementsOutsideTheThirteenEconomicLabelsStillRequireHumanAdjudication(string field)
    {
        using var archive = new Archive(); archive.Write(Article(1));
        var packet = archive.Export(1); var item = packet.WorkItems.Single(); var sha = Sha(packet);
        var label = Label(item);
        var changed = field switch {
            "context" => label with { Scope = NewsAssessmentScope.Global, ContextCategory = NewsAssessmentContextCategory.Policy, Targets = ["*"] },
            "reading" => label with { ReadingCategories = ["financial"] },
            _ => label with { Adverse = true } };
        var document = Human(sha, Annotation(item, "human-a", label), Annotation(item, "human-b", changed));
        var unresolved = NewsAssessmentWorkflow.Import(packet, sha, document, LabelTime.AddMinutes(2));
        Assert.Equal(1, unresolved.DoubleLabeledArticleCount); Assert.Equal(1, unresolved.UnresolvedConflictCount);
        Assert.Empty(unresolved.ResolvedLabels);
        var adjudicated = document with { Adjudications = [new(item.Item.WorkItemId, item.Item.NewsRevisionContentSha256,
            "human-c", LabelTime.AddMinutes(1), label, "Resolved using the exact archived revision text.")] };
        var result = NewsAssessmentWorkflow.Import(packet, sha, adjudicated, LabelTime.AddMinutes(2));
        Assert.Equal("adjudicated", Assert.Single(result.ResolvedLabels).Resolution);
        Assert.Equal(0, result.UnresolvedConflictCount);
    }

    [Fact]
    public void ReceiptClockRevisionAndScopeMismatchesAreRejectedRatherThanReconstructed()
    {
        using var archive = new Archive(); archive.Write(Article(1));
        var packet = archive.Export(1, new(NewsAssessmentAvailabilityBasis.ConservativeProviderUpdated, 60));
        var item = packet.WorkItems.Single(); var sha = Sha(packet); var row = Annotation(item, "human-a", Label(item));
        Assert.ThrowsAny<Exception>(() => NewsAssessmentWorkflow.Import(packet, sha,
            Human(sha, row with { LabeledAtUtc = item.Item.ProviderUpdatedAtUtc.AddMinutes(5) }), LabelTime));
        Assert.ThrowsAny<Exception>(() => NewsAssessmentWorkflow.Import(packet, sha,
            Human(sha, row with { NewsRevisionContentSha256 = new string('a', 64) }), LabelTime));
        Assert.Throws<InvalidDataException>(() => NewsAssessmentWorkflow.Import(packet, sha,
            Human(sha, row with { Label = row.Label with { Targets = ["OTHER"] } }), LabelTime));
    }

    [Fact]
    public void BaselineUsesActualDisplayRulesAndAbstainsFromAllEconomicTasksEvenWithPerfectReadingAgreement()
    {
        using var archive = new Archive(); archive.Write(Article(1, headline: "Software earnings and Federal Reserve policy"));
        var packet = archive.Export(1); var sha = Sha(packet); var item = packet.WorkItems.Single();
        var baseline = NewsAssessmentWorkflow.PredictDisplayBaseline(packet, sha, Receipt.AddMinutes(5));
        var prediction = Assert.Single(baseline.Predictions);
        var actualRules = NewsDisplayTaxonomy.Describe(new PersistedNewsItem { Ticker = "SYM", Provider = "alpaca", Headline = item.Item.Headline });
        Assert.Equal(NewsDisplayTaxonomy.Version, baseline.ClassifierVersion);
        Assert.Equal(actualRules.Categories, prediction.ReadingCategories);
        Assert.Equal(actualRules.Sectors, prediction.ReadingSectors);
        Assert.Null(prediction.CatalystLabel); Assert.NotEmpty(prediction.EconomicAbstentionReason!);
        var gold = Label(item) with { ReadingCategories = prediction.ReadingCategories, ReadingSectors = prediction.ReadingSectors };
        var evaluation = NewsAssessmentWorkflow.Evaluate(packet, sha, Human(sha, Annotation(item, "human-a", gold)), baseline, LabelTime);
        Assert.Equal(1m, evaluation.Scores.Single(score => score.Group == "all_held_out").ReadingExactSetAccuracy);
        Assert.Null(evaluation.Scores.Single(score => score.Group == "all_held_out").EconomicCategoryAccuracy);
        Assert.NotEmpty(evaluation.ReadingMetrics);
        Assert.False(evaluation.Nws12MeasuredThresholdsSatisfied); Assert.False(evaluation.PromotionAllowed);
        Assert.False(evaluation.HistoricalClassifierAvailabilityProven);
        Assert.Throws<InvalidDataException>(() => NewsAssessmentWorkflow.PredictDisplayBaseline(packet, sha, Receipt.AddDays(-1)));
    }

    [Theory]
    [InlineData("training")]
    [InlineData("revision")]
    [InlineData("future")]
    public void HeldOutOverlapWrongRevisionAndFuturePredictionFailClosed(string failure)
    {
        using var archive = new Archive(); archive.Write(Article(1));
        var packet = archive.Export(1); var sha = Sha(packet); var item = packet.WorkItems.Single();
        var predictions = NewsAssessmentWorkflow.PredictDisplayBaseline(packet, sha, Receipt.AddMinutes(5));
        predictions = failure switch {
            "training" => predictions with { TrainingWorkItemIds = [item.Item.WorkItemId] },
            "revision" => predictions with { Predictions = [predictions.Predictions[0] with { NewsRevisionContentSha256 = new string('f', 64) }] },
            _ => predictions with { Predictions = [predictions.Predictions[0] with { CompletedAtUtc = LabelTime.AddHours(1) }] } };
        Assert.Throws<InvalidDataException>(() => NewsAssessmentWorkflow.Evaluate(packet, sha,
            Human(sha, Annotation(item, "human-a", Label(item))), predictions, LabelTime));
    }

    [Fact]
    public void PredictionsFrozenAfterHumanLabelsCannotCountAsBlindEvaluation()
    {
        using var archive = new Archive(); archive.Write(Article(1));
        var packet = archive.Export(1); var sha = Sha(packet); var item = packet.WorkItems.Single();
        var late = NewsAssessmentWorkflow.PredictDisplayBaseline(packet, sha, LabelTime.AddMinutes(1));
        var result = NewsAssessmentWorkflow.Evaluate(packet, sha, Human(sha, Annotation(item, "human-a", Label(item))), late, LabelTime.AddMinutes(2));
        Assert.Contains("blind_predictions_not_frozen_before_first_human_label", result.Failures);
    }

    [Fact]
    public void StrictJsonRejectsMissingAdverseLabelAndDuplicateFields()
    {
        using var archive = new Archive(); archive.Write(Article(1));
        var item = archive.Export(1).WorkItems.Single();
        var json = Encoding.UTF8.GetString(NewsAssessmentWorkflow.Bytes(Label(item)));
        Assert.Throws<JsonException>(() => NewsAssessmentWorkflow.Read<NewsAssessmentLabel>(Encoding.UTF8.GetBytes(json.Replace("\"adverse\": false,", "", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => NewsAssessmentWorkflow.Read<JsonElement>(Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}")));
    }

    [Fact]
    public void CliExportsTwoReadableBlindQueuesWithoutInventedLabelsAndReplaysExactBytes()
    {
        using var archive = new Archive(); archive.Write(Enumerable.Range(1, 500).Select(id => Article(id)).ToArray());
        var output = Path.Combine(archive.Root, "packet");
        var args = new[] { "export", "--collection-root", archive.Root, "--summary", archive.SummaryPath,
            "--output", output, "--availability", "observed-receipt" };
        Assert.Equal(0, NewsAssessmentCommand.Run(args, TextWriter.Null));
        var first = File.ReadAllBytes(Path.Combine(output, "assessment-package.json"));
        Assert.Equal(0, NewsAssessmentCommand.Run(args, TextWriter.Null));
        Assert.Equal(first, File.ReadAllBytes(Path.Combine(output, "assessment-package.json")));
        foreach (var (reviewer, count) in new[] { ("reviewer-a", 500), ("reviewer-b", 100) }) {
            using var template = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, reviewer, "human-labels-template.json")));
            Assert.Equal(count, template.RootElement.GetProperty("annotations").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, template.RootElement.GetProperty("annotations")[0].GetProperty("label").GetProperty("category").ValueKind);
            Assert.True(File.Exists(Path.Combine(output, reviewer, "REVIEW-QUEUE.md")));
            Assert.True(File.Exists(Path.Combine(output, reviewer, "catalyst-human-labeling-v1.md")));
        }
        using var manifest = JsonDocument.Parse(first);
        Assert.Equal(0, manifest.RootElement.GetProperty("human_labels_completed").GetInt32());
        Assert.False(manifest.RootElement.GetProperty("catalog_ground_truth_published").GetBoolean());
        Assert.Throws<ArgumentException>(() => NewsAssessmentCommand.Run([.. args, "--sample-size", "499"], TextWriter.Null));
    }

    private static string Sha(NewsAssessmentPacket packet) => NewsAssessmentWorkflow.Hash(NewsAssessmentWorkflow.Bytes(packet));
    private static NewsAssessmentHumanDocument Human(string sha, params NewsAssessmentAnnotation[] annotations) =>
        new(NewsAssessmentWorkflow.HumanFormat, sha, "independent_human_no_future_data", annotations.ToImmutableArray(), []);
    private static NewsAssessmentAnnotation Annotation(NewsAssessmentWorkItem item, string actor, NewsAssessmentLabel label) =>
        new(item.Item.WorkItemId, item.Item.NewsRevisionContentSha256, actor, LabelTime, label);
    private static NewsAssessmentLabel Label(NewsAssessmentWorkItem item) => new(CatalystNewsCategory.Unknown, CatalystDirection.Ambiguous,
        false, CatalystMateriality.Low, NewsAssessmentScope.Company, NewsAssessmentContextCategory.Company, ["SYM"], false, item.TextSupport, ["company"], []);
    private static object Article(int id, string headline = "Company update", string? summary = null, string? body = null,
        string created = "2026-08-17T11:56:00Z", string updated = "2026-08-17T11:56:00Z") =>
        new { id, created_at = created, updated_at = updated, headline, summary, content = body, url = "https://example.invalid/article/" + id, symbols = new[] { "SYM" }, categories = Array.Empty<string>() };

    private sealed class Archive : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tradingflow-news-assessment-" + Guid.NewGuid().ToString("N"));
        public string PayloadPath => Path.Combine(Root, "page.json");
        public string SummaryPath => Path.Combine(Root, "summary.json");
        public void Write(params object[] articles) {
            Directory.CreateDirectory(Root);
            File.WriteAllText(PayloadPath, JsonSerializer.Serialize(new { news = articles }));
            File.WriteAllText(Path.Combine(Root, "receipt.json"), JsonSerializer.Serialize(new { HttpStatus = 200,
                HistoricalFirstReceiptClaim = false, CapturedUtc = Receipt.ToString("O"), Payload = "page.json", PayloadSha256 = NewsAssessmentWorkflow.FileHash(PayloadPath) }));
            File.WriteAllText(SummaryPath, JsonSerializer.Serialize(new { Windows = new[] { new { PaginationComplete = true,
                Error = (string?)null, Receipts = new[] { "receipt.json" } } } }));
        }
        public NewsAssessmentPacket Export(int count, NewsAssessmentAvailabilityContract? availability = null) =>
            NewsAssessmentWorkflow.Export(Root, SummaryPath, availability ?? new(NewsAssessmentAvailabilityBasis.ObservedReceipt), count);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}

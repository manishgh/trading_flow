using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Labeling;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class CatalystGroundTruthEvidenceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly string ConfigHash = Hash("ground-truth-config");

    [Fact]
    public void WorkItemExport_IsByteDeterministicForShuffledNews()
    {
        var news = News(20);
        var source = SourceManifest(news);

        var first = CatalystLabelingWorkItemExporter.Export(source, news);
        var second = CatalystLabelingWorkItemExporter.Export(
            source,
            news.Reverse().ToArray());

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.Content.ToArray(), second.Content.ToArray());
        Assert.Equal(
            first.WorkItems.Select(value => value.WorkItemId),
            second.WorkItems.Select(value => value.WorkItemId));
    }

    [Fact]
    public void StratifiedSample_IsDeterministicExactAndCoversYearSymbolStrata()
    {
        var news = Enumerable.Range(0, 520)
            .Select(index => NewsRow(
                index,
                new DateTimeOffset(
                    2020 + index % 7,
                    1 + index % 12,
                    1 + index % 20,
                    14,
                    0,
                    0,
                    TimeSpan.Zero),
                $"SYM{index % 40:D2}",
                $"article-{index:D4}",
                $"revision-{index:D4}"))
            .ToArray();
        var source = SourceManifest(news);
        var definition = CatalystLabelingSampleDefinition.Stratified(500);

        var first = CatalystLabelingWorkItemExporter.Export(source, news, definition);
        var shuffled = CatalystLabelingWorkItemExporter.Export(
            source,
            news.OrderByDescending(value => value.RevisionId).ToArray(),
            definition);

        Assert.Equal(500, first.WorkItems.Count);
        Assert.Equal(520, first.EligibleCandidateCount);
        Assert.Equal(first.Sha256, shuffled.Sha256);
        Assert.Equal(first.Content.ToArray(), shuffled.Content.ToArray());
        Assert.Equal(280, first.WorkItems
            .Select(value => (value.AvailableAtUtc.Year, value.Symbols.Single()))
            .Distinct()
            .Count());
    }

    [Fact]
    public void StratifiedSample_UsesOnlyEarliestAvailableRevisionPerProviderArticle()
    {
        var firstAvailability = Now.AddDays(-2);
        var news = new[]
        {
            NewsRow(
                1,
                firstAvailability.AddHours(2),
                "MSFT",
                "shared-article",
                "revision-later"),
            NewsRow(
                2,
                firstAvailability,
                "MSFT",
                "shared-article",
                "revision-earliest"),
            NewsRow(
                3,
                firstAvailability.AddHours(1),
                "MU",
                "other-article",
                "revision-other")
        };
        var source = SourceManifest(news);

        var result = CatalystLabelingWorkItemExporter.Export(
            source,
            news,
            CatalystLabelingSampleDefinition.Stratified(2));

        Assert.Equal(2, result.EligibleCandidateCount);
        Assert.Equal(2, result.WorkItems.Count);
        Assert.Contains(
            result.WorkItems,
            value => value.ProviderArticleId == "shared-article" &&
                     value.NewsRevisionId == "revision-earliest");
        Assert.DoesNotContain(
            result.WorkItems,
            value => value.NewsRevisionId == "revision-later");
    }

    [Fact]
    public void LabelTemplate_BindsToExactSampleAndIsByteDeterministic()
    {
        var news = News(10);
        var export = CatalystLabelingWorkItemExporter.Export(
            SourceManifest(news),
            news,
            CatalystLabelingSampleDefinition.Stratified(5));

        var first = CatalystHumanLabelTemplateExporter.Export(export);
        var second = CatalystHumanLabelTemplateExporter.Export(export);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.Content.ToArray(), second.Content.ToArray());
        Assert.Equal(export.Sha256, first.WorkItemExportSha256);
        var imported = CatalystHumanLabelImporter.Import(export, first.Content, Now);
        Assert.Empty(imported.ResolvedLabels);
    }

    [Theory]
    [InlineData("{", "malformed_document")]
    [InlineData("{}", "unsupported_format_version")]
    public void Import_RejectsMalformedOrUnsupportedDocuments(
        string json,
        string expectedCode)
    {
        var export = Export(News(1));

        var exception = Assert.Throws<CatalystLabelImportException>(() =>
            CatalystHumanLabelImporter.Import(
                export,
                Encoding.UTF8.GetBytes(json),
                Now));

        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public void Import_RejectsDuplicateAndConflictingAnnotations()
    {
        var export = Export(News(1));
        var first = Annotation(export.WorkItems[0], "analyst-a");
        var duplicate = LabelDocument(export, [first, first]);
        var duplicateException = Assert.Throws<CatalystLabelImportException>(() =>
            CatalystHumanLabelImporter.Import(export, duplicate, Now));
        Assert.Equal("duplicate_annotation", duplicateException.Code);

        var conflict = first with { Category = "guidance" };
        var conflictingDocument = LabelDocument(export, [first, conflict]);
        var conflictingException = Assert.Throws<CatalystLabelImportException>(() =>
            CatalystHumanLabelImporter.Import(export, conflictingDocument, Now));
        Assert.Equal("conflicting_annotation", conflictingException.Code);
    }

    [Fact]
    public void Import_PreservesUnresolvedConflictAndRequiresAdjudication()
    {
        var export = Export(News(1));
        var first = Annotation(export.WorkItems[0], "analyst-a");
        var second = Annotation(export.WorkItems[0], "analyst-b") with
        {
            Direction = "negative"
        };

        var result = CatalystHumanLabelImporter.Import(
            export,
            LabelDocument(export, [first, second]),
            Now);

        Assert.Empty(result.ResolvedLabels);
        Assert.Equal(1, result.DoubleLabeledArticleCount);
        Assert.Equal(1, result.UnresolvedConflictCount);

        var adjudication = Adjudication(export.WorkItems[0]);
        var adjudicated = CatalystHumanLabelImporter.Import(
            export,
            LabelDocument(export, [first, second], [adjudication]),
            Now);
        var resolved = Assert.Single(adjudicated.ResolvedLabels);
        Assert.Equal(GroundTruthResolution.Adjudicated, resolved.Resolution);
        Assert.Equal("adjudicator-1", resolved.Adjudication!.AdjudicatorId);
        Assert.Equal(1, adjudicated.AdjudicatedArticleCount);
        Assert.Equal(0, adjudicated.UnresolvedConflictCount);
    }

    [Fact]
    public void Readiness_EnforcesExactNws11Boundaries()
    {
        var allCategories = Enum.GetValues<CatalystNewsCategory>()
            .ToDictionary(value => value, _ => 1);
        var ready = new CatalystGroundTruthReadinessReport(
            new CatalystGroundTruthReadinessThresholds(),
            500,
            100,
            0,
            0,
            allCategories);
        Assert.True(ready.IsReady);
        Assert.True(ready.CatalystPromotionAllowed);

        var tooFewLabels = new CatalystGroundTruthReadinessReport(
            new CatalystGroundTruthReadinessThresholds(),
            499,
            100,
            0,
            0,
            allCategories);
        Assert.False(tooFewLabels.IsReady);
        Assert.Contains(
            "valid_labels_below_minimum:499/500",
            tooFewLabels.Failures);

        var tooFewDoubleLabels = new CatalystGroundTruthReadinessReport(
            new CatalystGroundTruthReadinessThresholds(),
            500,
            99,
            0,
            0,
            allCategories);
        Assert.False(tooFewDoubleLabels.IsReady);
        Assert.Contains(
            "double_labeled_articles_below_minimum:99/100",
            tooFewDoubleLabels.Failures);

        var missingCategory = allCategories.ToDictionary(value => value.Key, value => value.Value);
        missingCategory[CatalystNewsCategory.Legal] = 0;
        var imbalanced = new CatalystGroundTruthReadinessReport(
            new CatalystGroundTruthReadinessThresholds(),
            500,
            100,
            0,
            0,
            missingCategory);
        Assert.False(imbalanced.IsReady);
        Assert.Contains("category_missing:Legal", imbalanced.Failures);
    }

    [Fact]
    public async Task Builder_PublishesVerifiableFailClosedDatasetWithoutFabricatingLabels()
    {
        var fixture = Fixture(13);
        var labels = fixture.Export.WorkItems
            .Select((item, index) => Annotation(
                item,
                $"analyst-{index:D2}",
                Enum.GetValues<CatalystNewsCategory>()[index]))
            .ToArray();
        var request = fixture.Request(LabelDocument(fixture.Export, labels));

        var result = await fixture.Builder.BuildAsync(request);
        var manifest = await fixture.Catalog.GetDatasetAsync(result.DatasetId);
        var rows = await new ParquetEvidencePartitionDataReader(
                new EvidenceParquetCodec(),
                fixture.Store)
            .ReadClassifierGroundTruthAsync(manifest!);

        Assert.Equal(13, rows.Count);
        Assert.False(result.Readiness.IsReady);
        Assert.False(result.Readiness.CatalystPromotionAllowed);
        Assert.Equal(13, result.Readiness.ValidLabelCount);
        Assert.Contains(
            result.Readiness.Failures,
            value => value.StartsWith("valid_labels_below_minimum:", StringComparison.Ordinal));
        Assert.All(rows, row =>
        {
            Assert.StartsWith("ground-truth-", row.GroundTruthId);
            Assert.Equal(fixture.Source.DatasetId, row.SourceNewsDatasetId);
            Assert.Equal(2, row.Sources.Count);
        });
        Assert.Equal(
            "false",
            manifest!.Attributes["nws11_ready"]);
        Assert.Equal(
            result.WorkItemExportArtifact.Content.Sha256,
            manifest.Attributes["work_item_export_sha256"]);

        var verification = CatalystGroundTruthPromotionGate.Evaluate(
            manifest,
            rows,
            fixture.Source.DatasetId);
        Assert.True(verification.IsVerified);
        Assert.False(verification.CatalystPromotionAllowed);
        Assert.Contains(
            verification.Failures,
            value => value.StartsWith(
                "valid_labels_below_minimum:",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Builder_RecreatesExactDeterministicSampleBeforeImportingLabels()
    {
        var fixture = Fixture(8);
        var sampling = CatalystLabelingSampleDefinition.Stratified(4);
        var sampledExport = CatalystLabelingWorkItemExporter.Export(
            fixture.Source,
            fixture.News,
            sampling);
        var labels = sampledExport.WorkItems
            .Select((item, index) => Annotation(item, $"analyst-{index:D2}"))
            .ToArray();

        var result = await fixture.Builder.BuildAsync(
            fixture.Request(
                LabelDocument(sampledExport, labels),
                new CatalystGroundTruthReadinessThresholds(4, 0, false),
                sampling));
        var manifest = await fixture.Catalog.GetDatasetAsync(result.DatasetId);

        Assert.Equal(4, result.RowCount);
        Assert.Equal(
            sampledExport.Sha256,
            result.WorkItemExportArtifact.Content.Sha256);
        Assert.Equal(
            sampling.SamplingVersion,
            manifest!.Attributes["label_sampling_version"]);
        Assert.Equal("4", manifest.Attributes["label_sampling_target"]);
        Assert.Equal("4", manifest.Attributes["label_sampling_selected_count"]);
    }

    [Fact]
    public async Task Builder_RejectsBlankTemplateWithoutPublishingGroundTruth()
    {
        var fixture = Fixture(8);
        var sampling = CatalystLabelingSampleDefinition.Stratified(4);
        var sampledExport = CatalystLabelingWorkItemExporter.Export(
            fixture.Source,
            fixture.News,
            sampling);
        var template = CatalystHumanLabelTemplateExporter.Export(sampledExport);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Builder.BuildAsync(
                fixture.Request(
                    template.Content.ToArray(),
                    new CatalystGroundTruthReadinessThresholds(4, 0, false),
                    sampling)));

        Assert.Contains(
            "No uncontested or adjudicated labels",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, fixture.Catalog.DatasetCommitCount);
    }

    [Fact]
    public async Task Reader_FailsClosedWhenPublishedPartitionIsTampered()
    {
        var fixture = Fixture(1);
        var labels = new[] { Annotation(fixture.Export.WorkItems[0], "analyst-a") };
        var result = await fixture.Builder.BuildAsync(
            fixture.Request(
                LabelDocument(fixture.Export, labels),
                new CatalystGroundTruthReadinessThresholds(1, 0, false)));
        var manifest = (await fixture.Catalog.GetDatasetAsync(result.DatasetId))!;
        fixture.Store.Tamper(manifest.Partitions[0].Artifact);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ParquetEvidencePartitionDataReader(
                    new EvidenceParquetCodec(),
                    fixture.Store)
                .ReadClassifierGroundTruthAsync(manifest));
    }

    [Fact]
    public async Task PromotionGate_FailsClosedWhenReadinessMetadataIsTamperedOrWeakened()
    {
        var fixture = Fixture(1);
        var result = await fixture.Builder.BuildAsync(
            fixture.Request(
                LabelDocument(
                    fixture.Export,
                    [Annotation(fixture.Export.WorkItems[0], "analyst-a")]),
                new CatalystGroundTruthReadinessThresholds(1, 0, false)));
        var manifest = (await fixture.Catalog.GetDatasetAsync(result.DatasetId))!;
        var rows = await new ParquetEvidencePartitionDataReader(
                new EvidenceParquetCodec(),
                fixture.Store)
            .ReadClassifierGroundTruthAsync(manifest);
        var attributes = manifest.Attributes.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        attributes["nws11_ready"] = "true";
        attributes["nws11_valid_labels"] = "500";
        var tampered = CopyManifest(manifest, attributes);

        var verification = CatalystGroundTruthPromotionGate.Evaluate(
            tampered,
            rows,
            fixture.Source.DatasetId);

        Assert.False(verification.IsVerified);
        Assert.False(verification.CatalystPromotionAllowed);
        Assert.Contains(
            "nws11_minimum_valid_labels_weakened:1/500",
            verification.IntegrityFailures);
        Assert.Contains(
            "nws11_minimum_double_labeled_articles_weakened:0/100",
            verification.IntegrityFailures);
        Assert.Contains(
            "nws11_every_category_requirement_disabled",
            verification.IntegrityFailures);
        Assert.Contains(
            "manifest_count_mismatch:nws11_valid_labels:1/500",
            verification.IntegrityFailures);
        Assert.Contains(
            "nws11_ready_mismatch:true/false",
            verification.IntegrityFailures);
    }

    [Fact]
    public async Task Builder_IsIdempotentAcrossRetryAndRestart()
    {
        var fixture = Fixture(1);
        var document = LabelDocument(
            fixture.Export,
            [Annotation(fixture.Export.WorkItems[0], "analyst-a")]);
        var request = fixture.Request(
            document,
            new CatalystGroundTruthReadinessThresholds(1, 0, false));

        var first = await fixture.Builder.BuildAsync(request);
        var artifactCount = fixture.Store.ArtifactCount;
        var second = await fixture.Builder.BuildAsync(request);
        var restarted = fixture.CreateBuilder();
        var third = await restarted.BuildAsync(request);

        Assert.False(first.AlreadyCommitted);
        Assert.True(second.AlreadyCommitted);
        Assert.True(third.AlreadyCommitted);
        Assert.Equal(first.DatasetId, second.DatasetId);
        Assert.Equal(first.DatasetId, third.DatasetId);
        Assert.Equal(artifactCount, fixture.Store.ArtifactCount);
        Assert.Equal(1, fixture.Catalog.DatasetCommitCount);
    }

    [Fact]
    public async Task Builder_RestartsAfterCatalogCommitFailureWithoutPartialVisibility()
    {
        var fixture = Fixture(1);
        fixture.Catalog.FailNextDatasetCommit = true;
        var request = fixture.Request(
            LabelDocument(
                fixture.Export,
                [Annotation(fixture.Export.WorkItems[0], "analyst-a")]),
            new CatalystGroundTruthReadinessThresholds(1, 0, false));

        await Assert.ThrowsAsync<IOException>(() => fixture.Builder.BuildAsync(request));
        Assert.Equal(0, fixture.Catalog.DatasetCommitCount);

        var result = await fixture.CreateBuilder().BuildAsync(request);

        Assert.False(result.AlreadyCommitted);
        Assert.NotNull(await fixture.Catalog.GetDatasetAsync(result.DatasetId));
        Assert.Equal(1, fixture.Catalog.DatasetCommitCount);
    }

    [Fact]
    public async Task Builder_ConcurrentRetriesConvergeOnOneCommittedDataset()
    {
        var fixture = Fixture(4);
        var request = fixture.Request(
            LabelDocument(
                fixture.Export,
                fixture.Export.WorkItems
                    .Select((item, index) => Annotation(item, $"analyst-{index}"))
                    .ToArray()),
            new CatalystGroundTruthReadinessThresholds(4, 0, false));

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => fixture.CreateBuilder().BuildAsync(request)));

        Assert.Single(results.Select(value => value.DatasetId).Distinct(StringComparer.Ordinal));
        Assert.Equal(1, fixture.Catalog.DatasetCommitCount);
        Assert.Equal(3, fixture.Store.ArtifactCount);
    }

    private static TestFixture Fixture(int count)
    {
        var news = News(count);
        var source = SourceManifest(news);
        var store = new InMemoryArtifactStore();
        var catalog = new TestCatalog(source);
        var reader = new TestPartitionReader(news);
        var fixture = new TestFixture(source, news, store, catalog, reader);
        return fixture;
    }

    private static CatalystLabelingWorkItemExport Export(
        IReadOnlyList<NewsRevisionEvidenceRow> news) =>
        CatalystLabelingWorkItemExporter.Export(SourceManifest(news), news);

    private static IReadOnlyList<NewsRevisionEvidenceRow> News(int count) =>
        Enumerable.Range(0, count)
            .Select(index => NewsRow(
                index,
                Now.AddDays(-10).AddMinutes(index),
                "MSFT",
                $"article-{index:D4}",
                $"revision-{index:D4}"))
            .ToArray();

    private static NewsRevisionEvidenceRow NewsRow(
        int index,
        DateTimeOffset timestamp,
        string symbol,
        string articleId,
        string revisionId) =>
        new(
            1,
            "news-run",
            Hash("news-config"),
            "news-code",
            "benzinga",
            NewsAvailabilityEvidence.ObservedReceiptTime,
            "alpaca",
            articleId,
            revisionId,
            $"Issuer event {index:D4}",
            $"Exact article summary {index:D4}.",
            $"https://example.test/news/{index:D4}",
            [symbol],
            ["provider-category"],
            timestamp,
            timestamp,
            timestamp,
            timestamp.AddSeconds(1),
            [new EvidenceRowSourceAddress(
                $"news-observation-{index:D4}-{revisionId}",
                Hash($"news-source-{index:D4}-{revisionId}"))]);

    private static EvidenceDatasetManifest SourceManifest(
        IReadOnlyList<NewsRevisionEvidenceRow> news)
    {
        var sources = news
            .Select(row => new EvidenceSourceReference(
                row.Sources[0].ObservationId,
                new EvidenceArtifactReference(
                    new EvidenceContentAddress(
                        row.Sources[0].ObservationSha256,
                        100,
                        "application/json"),
                    new EvidenceObjectNamespace("raw/alpaca/news")),
                row.ReceivedAtUtc))
            .ToArray();
        var partition = new EvidenceDatasetPartitionManifest(
            "news-partition",
            EvidenceDatasetKind.NewsRevisions,
            1,
            new EvidencePartitionProvenance(
                "alpaca",
                "/v1beta1/news",
                "benzinga",
                "none",
                "N/A",
                null,
                [],
                [],
                ["MSFT"],
                "event",
                news.Min(value => value.ProviderUpdatedAtUtc),
                news.Max(value => value.ProviderUpdatedAtUtc).AddTicks(1)),
            news.Min(value => value.SourceTimestampUtc),
            news.Max(value => value.SourceTimestampUtc),
            news.Count,
            new EvidenceArtifactReference(
                new EvidenceContentAddress(
                    Hash("news-partition"),
                    200,
                    EvidenceParquetCodec.MediaType),
                new EvidenceObjectNamespace("normalized/news")),
            sources,
            "news-normalizer-v1",
            "news-code",
            new EvidenceQualityReport(),
            dimensions: new Dictionary<string, string>
            {
                ["run_id"] = "news-run",
                ["config_hash"] = Hash("news-config"),
                ["code_version"] = "news-code",
                ["normalizer_version"] = "news-normalizer-v1",
                ["data_feed"] = "benzinga"
            });
        return new EvidenceDatasetManifest(
            EvidenceDatasetKind.NewsRevisions,
            1,
            Now.AddDays(-1),
            "news-collection-job",
            Hash("news-plan"),
            Hash("news-config"),
            "news-code",
            "news-normalizer-v1",
            "benzinga",
            [partition],
            new EvidenceQualityReport());
    }

    private static AnnotationJson Annotation(
        CatalystLabelingWorkItem item,
        string annotator,
        CatalystNewsCategory category = CatalystNewsCategory.Earnings) =>
        new(
            item.WorkItemId,
            item.NewsRevisionContentSha256,
            annotator,
            Now.AddMinutes(-5),
            ToSnake(category),
            "positive",
            true,
            "high");

    private static AdjudicationJson Adjudication(CatalystLabelingWorkItem item) =>
        new(
            item.WorkItemId,
            item.NewsRevisionContentSha256,
            "adjudicator-1",
            Now.AddMinutes(-1),
            "earnings",
            "positive",
            true,
            "high",
            "Reviewed both annotations against the exact article revision.");

    private static byte[] LabelDocument(
        CatalystLabelingWorkItemExport export,
        IReadOnlyList<AnnotationJson> annotations,
        IReadOnlyList<AdjudicationJson>? adjudications = null) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                format_version = CatalystHumanLabelImporter.FormatVersion,
                source_news_dataset_id = export.SourceNewsDatasetId,
                work_item_export_sha256 = export.Sha256,
                annotations,
                adjudications = adjudications ?? []
            },
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

    private static string ToSnake(CatalystNewsCategory value)
    {
        var text = value.ToString();
        var builder = new StringBuilder();
        for (var index = 0; index < text.Length; index++)
        {
            if (index > 0 && Char.IsUpper(text[index]))
            {
                builder.Append('_');
            }

            builder.Append(Char.ToLowerInvariant(text[index]));
        }

        return builder.ToString();
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static EvidenceDatasetManifest CopyManifest(
        EvidenceDatasetManifest source,
        IReadOnlyDictionary<string, string> attributes) =>
        new(
            source.Kind,
            source.SchemaVersion,
            source.CreatedAtUtc,
            source.CollectionJobId,
            source.CollectionPlanHash,
            source.ConfigHash,
            source.CodeVersion,
            source.NormalizerVersion,
            source.DataFeed,
            source.Partitions,
            source.Quality,
            attributes);

    private sealed record AnnotationJson(
        string WorkItemId,
        string NewsRevisionContentSha256,
        string AnnotatorId,
        DateTimeOffset LabeledAtUtc,
        string Category,
        string Direction,
        bool DirectionIsClear,
        string Materiality);

    private sealed record AdjudicationJson(
        string WorkItemId,
        string NewsRevisionContentSha256,
        string AdjudicatorId,
        DateTimeOffset AdjudicatedAtUtc,
        string Category,
        string Direction,
        bool DirectionIsClear,
        string Materiality,
        string Rationale);

    private sealed class TestFixture(
        EvidenceDatasetManifest source,
        IReadOnlyList<NewsRevisionEvidenceRow> news,
        InMemoryArtifactStore store,
        TestCatalog catalog,
        TestPartitionReader reader)
    {
        public EvidenceDatasetManifest Source { get; } = source;
        public IReadOnlyList<NewsRevisionEvidenceRow> News { get; } = news;
        public InMemoryArtifactStore Store { get; } = store;
        public TestCatalog Catalog { get; } = catalog;
        public CatalystLabelingWorkItemExport Export { get; } =
            CatalystLabelingWorkItemExporter.Export(source, news);
        public CatalystGroundTruthDatasetBuilder Builder => CreateBuilder();

        public CatalystGroundTruthDatasetBuilder CreateBuilder() =>
            new(
                Catalog,
                reader,
                new EvidenceParquetPartitionPublisher(
                    new EvidenceParquetCodec(),
                    Store),
                Store);

        public CatalystGroundTruthBuildRequest Request(
            byte[] document,
            CatalystGroundTruthReadinessThresholds? thresholds = null,
            CatalystLabelingSampleDefinition? sampling = null) =>
            new(
                Source.DatasetId,
                document,
                "ground-truth-run",
                ConfigHash,
                "test-code",
                "ground-truth-builder-v1",
                Now,
                thresholds,
                sampling);
    }

    private sealed class TestPartitionReader(
        IReadOnlyList<NewsRevisionEvidenceRow> news) : IEvidencePartitionDataReader
    {
        public Task<IReadOnlyList<NewsRevisionEvidenceRow>> ReadNewsRevisionsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(news);
        }

        public Task<IReadOnlyList<MarketBarEvidenceRow>> ReadMarketBarsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) => Unused<MarketBarEvidenceRow>();
        public Task<IReadOnlyList<SentimentAssessmentEvidenceRow>> ReadSentimentAssessmentsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) => Unused<SentimentAssessmentEvidenceRow>();
        public Task<IReadOnlyList<SecurityMasterSnapshotEvidenceRow>> ReadSecurityMasterSnapshotsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) => Unused<SecurityMasterSnapshotEvidenceRow>();
        public Task<IReadOnlyList<SymbolIntervalEvidenceRow>> ReadSymbolIntervalsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) => Unused<SymbolIntervalEvidenceRow>();
        public Task<IReadOnlyList<CorporateActionEvidenceRow>> ReadCorporateActionsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) => Unused<CorporateActionEvidenceRow>();
        public Task<IReadOnlyList<UniverseMembershipEvidenceRow>> ReadUniverseMembershipAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) => Unused<UniverseMembershipEvidenceRow>();
        public Task<IReadOnlyList<SipQuoteEvidenceRow>> ReadSipQuotesAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) => Unused<SipQuoteEvidenceRow>();
        public Task<IReadOnlyList<ExchangeSessionEvidenceRow>> ReadExchangeSessionsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) => Unused<ExchangeSessionEvidenceRow>();

        private static Task<IReadOnlyList<T>> Unused<T>() =>
            throw new NotSupportedException();
    }

    private sealed class InMemoryArtifactStore : IImmutableArtifactStore
    {
        private readonly ConcurrentDictionary<(string Namespace, string Hash), byte[]>
            artifacts = new();

        public int ArtifactCount => artifacts.Count;

        public Task<ImmutableArtifactReceipt> PutIfAbsentAsync(
            ImmutableArtifactWriteRequest request,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (
                request.Artifact.ObjectNamespace.Value,
                request.Artifact.Content.Sha256);
            var candidate = content.ToArray();
            var existing = artifacts.GetOrAdd(key, candidate);
            if (!existing.AsSpan().SequenceEqual(candidate))
            {
                throw new InvalidDataException("Immutable content-address collision.");
            }

            return Task.FromResult(new ImmutableArtifactReceipt(
                request.Artifact,
                $"{key.Item1}/{key.Item2}",
                !ReferenceEquals(existing, candidate)));
        }

        public Task<Stream> OpenReadAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new MemoryStream(
                artifacts[(artifact.ObjectNamespace.Value, artifact.Content.Sha256)],
                writable: false));
        }

        public Task<ImmutableArtifactVerification> VerifyAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!artifacts.TryGetValue(
                    (artifact.ObjectNamespace.Value, artifact.Content.Sha256),
                    out var bytes))
            {
                return Task.FromResult(new ImmutableArtifactVerification(
                    false,
                    artifact,
                    null,
                    null,
                    "Missing artifact."));
            }

            var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var valid =
                actualHash.Equals(artifact.Content.Sha256, StringComparison.Ordinal) &&
                bytes.LongLength == artifact.Content.ByteLength;
            return Task.FromResult(new ImmutableArtifactVerification(
                valid,
                artifact,
                bytes.LongLength,
                actualHash,
                valid ? null : "Hash or length mismatch."));
        }

        public void Tamper(EvidenceArtifactReference artifact)
        {
            var key = (artifact.ObjectNamespace.Value, artifact.Content.Sha256);
            artifacts[key][0] ^= 0xFF;
        }
    }

    private sealed class TestCatalog(EvidenceDatasetManifest source) : IEvidenceCatalog
    {
        private readonly object sync = new();
        private readonly Dictionary<string, EvidenceCollectionPlan> plans =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, EvidenceSourceObservation> observations =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, EvidenceDatasetManifest> byId =
            new(StringComparer.Ordinal)
            {
                [source.DatasetId] = source
            };
        private readonly Dictionary<string, string> byLogicalKey =
            new(StringComparer.Ordinal);

        public bool FailNextDatasetCommit { get; set; }
        public int DatasetCommitCount { get; private set; }

        public Task<EvidenceDatasetManifest?> GetDatasetAsync(
            string datasetId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                byId.TryGetValue(datasetId, out var value);
                return Task.FromResult(value);
            }
        }

        public Task<EvidenceCatalogCommitResult> CommitDatasetAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (FailNextDatasetCommit)
                {
                    FailNextDatasetCommit = false;
                    throw new IOException("Synthetic catalog failure.");
                }

                if (byLogicalKey.TryGetValue(manifest.LogicalDatasetKey, out var existing))
                {
                    return Task.FromResult(new EvidenceCatalogCommitResult(existing, true));
                }

                if (!plans.TryGetValue(manifest.CollectionJobId, out var plan) ||
                    !plan.LogicalPlanHash.Equals(
                        manifest.CollectionPlanHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Dataset collection plan is not registered.");
                }

                foreach (var sourceReference in manifest.Partitions
                             .SelectMany(partition => partition.SourceObservations)
                             .Distinct())
                {
                    if (!observations.TryGetValue(
                            sourceReference.ObservationId,
                            out var observation) ||
                        !observation.CollectionJobId.Equals(
                            manifest.CollectionJobId,
                            StringComparison.Ordinal) ||
                        !observation.Artifact.Equals(sourceReference.Artifact))
                    {
                        throw new InvalidDataException(
                            $"Dataset source observation " +
                            $"'{sourceReference.ObservationId}' is not registered.");
                    }
                }

                byLogicalKey.Add(manifest.LogicalDatasetKey, manifest.DatasetId);
                byId.Add(manifest.DatasetId, manifest);
                DatasetCommitCount++;
                return Task.FromResult(
                    new EvidenceCatalogCommitResult(manifest.DatasetId, false));
            }
        }

        public Task<EvidenceCatalogCommitResult> RegisterCollectionPlanAsync(
            EvidenceCollectionPlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (plans.TryGetValue(plan.JobId, out var existing))
                {
                    if (!EvidenceCanonicalJson.ComputeSha256(existing).Equals(
                            EvidenceCanonicalJson.ComputeSha256(plan),
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Collection plan '{plan.JobId}' conflicts with registered content.");
                    }

                    return Task.FromResult(
                        new EvidenceCatalogCommitResult(plan.JobId, true));
                }

                plans.Add(plan.JobId, plan);
                return Task.FromResult(
                    new EvidenceCatalogCommitResult(plan.JobId, false));
            }
        }

        public Task<EvidenceCollectionPlan?> GetCollectionPlanAsync(
            string jobId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                plans.TryGetValue(jobId, out var plan);
                return Task.FromResult(plan);
            }
        }
        public Task SaveCollectionCheckpointAsync(EvidenceCollectionCheckpoint checkpoint, CancellationToken cancellationToken = default) => Unused();
        public Task<EvidenceCollectionCheckpoint?> GetCollectionCheckpointAsync(string jobId, CancellationToken cancellationToken = default) => Unused<EvidenceCollectionCheckpoint?>();
        public Task SaveRequestCursorCheckpointAsync(EvidenceRequestCursorCheckpoint checkpoint, CancellationToken cancellationToken = default) => Unused();
        public Task<EvidenceRequestCursorCheckpoint?> GetRequestCursorCheckpointAsync(string jobId, string requestId, CancellationToken cancellationToken = default) => Unused<EvidenceRequestCursorCheckpoint?>();
        public Task<EvidenceCatalogCommitResult> RegisterSourceObservationAsync(
            EvidenceSourceObservation observation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (!plans.TryGetValue(observation.CollectionJobId, out var plan) ||
                    !plan.LogicalPlanHash.Equals(
                        observation.CollectionPlanHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Source observation collection plan is not registered.");
                }

                if (observations.TryGetValue(observation.ObservationId, out var existing))
                {
                    if (!EvidenceCanonicalJson.ComputeSha256(existing).Equals(
                            EvidenceCanonicalJson.ComputeSha256(observation),
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Source observation '{observation.ObservationId}' conflicts.");
                    }

                    return Task.FromResult(
                        new EvidenceCatalogCommitResult(observation.ObservationId, true));
                }

                observations.Add(observation.ObservationId, observation);
                return Task.FromResult(
                    new EvidenceCatalogCommitResult(observation.ObservationId, false));
            }
        }

        public Task<EvidenceSourceObservation?> GetSourceObservationAsync(
            string observationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                observations.TryGetValue(observationId, out var observation);
                return Task.FromResult(observation);
            }
        }

        public Task<IReadOnlyList<EvidenceSourceObservation>> FindSourceObservationsAsync(
            string jobId,
            string requestId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                return Task.FromResult<IReadOnlyList<EvidenceSourceObservation>>(
                    observations.Values
                        .Where(value =>
                            value.CollectionJobId.Equals(jobId, StringComparison.Ordinal) &&
                            value.IngestionRequestId.Equals(
                                requestId,
                                StringComparison.Ordinal))
                        .OrderBy(value => value.ObservationId, StringComparer.Ordinal)
                        .ToArray());
            }
        }
        public Task<IReadOnlyList<EvidenceDatasetManifest>> FindDatasetsAsync(EvidenceDatasetQuery query, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<EvidenceDatasetManifest>>();
        public Task<EvidenceCatalogCommitResult> RegisterResearchRunAsync(EvidenceResearchRunManifest manifest, CancellationToken cancellationToken = default) => Unused<EvidenceCatalogCommitResult>();
        public Task<EvidenceResearchRunManifest?> GetResearchRunAsync(string researchRunId, CancellationToken cancellationToken = default) => Unused<EvidenceResearchRunManifest?>();
        public Task<EvidenceHoldoutConsumption?> GetHoldoutConsumptionAsync(string holdoutId, CancellationToken cancellationToken = default) => Unused<EvidenceHoldoutConsumption?>();

        public Task<EvidenceCatalogCommitResult> ReserveHoldoutAsync(EvidenceHoldoutConsumption consumption, CancellationToken cancellationToken = default) => Unused<EvidenceCatalogCommitResult>();
        public Task<EvidenceCatalogCommitResult> RegisterQuarantineAsync(EvidenceQuarantineRecord quarantine, CancellationToken cancellationToken = default) => Unused<EvidenceCatalogCommitResult>();
        public Task<EvidenceQuarantineEntry?> GetQuarantineAsync(string quarantineId, CancellationToken cancellationToken = default) => Unused<EvidenceQuarantineEntry?>();
        public Task<IReadOnlyList<EvidenceQuarantineEntry>> FindQuarantinesAsync(EvidenceQuarantineStatus status, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<EvidenceQuarantineEntry>>();
        public Task ResolveQuarantineAsync(EvidenceQuarantineResolution resolution, CancellationToken cancellationToken = default) => Unused();
        public Task<IReadOnlyList<EvidenceArtifactReference>> ResolveReferenceClosureAsync(EvidencePinSubject subject, CancellationToken cancellationToken = default) => Unused<IReadOnlyList<EvidenceArtifactReference>>();
        public Task<EvidenceRetentionPin> PinAsync(EvidencePinSubject subject, string reason, CancellationToken cancellationToken = default) => Unused<EvidenceRetentionPin>();

        private static Task Unused() => throw new NotSupportedException();
        private static Task<T> Unused<T>() => throw new NotSupportedException();
    }
}

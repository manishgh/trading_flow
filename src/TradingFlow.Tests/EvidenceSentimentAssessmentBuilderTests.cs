using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Data.Evidence.Sentiment;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class EvidenceSentimentAssessmentBuilderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly string ModelArtifactSha256 = Hash("finbert-model-artifact");
    private static readonly DateTimeOffset ModelTrainingDataCutoffUtc = Now.AddDays(-30);
    private const string ConfigHash =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string SourceHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Build_IsDeterministicAndIdempotent()
    {
        var fixture = Fixture();

        var first = await fixture.Builder.BuildAsync(fixture.Request);
        var second = await fixture.Builder.BuildAsync(fixture.Request);

        Assert.Equal(first.DatasetId, second.DatasetId);
        Assert.False(first.AlreadyCommitted);
        Assert.True(second.AlreadyCommitted);
        Assert.Equal(1, fixture.Catalog.DatasetCount);
        Assert.Equal(2, fixture.Catalog.CommitCalls);
        Assert.Equal(1, fixture.Store.ArtifactCount);
        var manifest = Assert.IsType<EvidenceDatasetManifest>(
            await fixture.Catalog.GetDatasetAsync(first.DatasetId));
        Assert.Equal(
            fixture.Source.DatasetId,
            manifest.Attributes["source_news_dataset_id"]);
        Assert.Equal(
            SentimentAssessmentEvidenceRow.NewsRevisionInputFormatVersion,
            manifest.Attributes["input_format_version"]);
    }

    [Fact]
    public async Task Build_BindsExactRevisionInputHashAndSourceLineage()
    {
        var fixture = Fixture();

        var result = await fixture.Builder.BuildAsync(fixture.Request);
        var manifest = Assert.IsType<EvidenceDatasetManifest>(
            await fixture.Catalog.GetDatasetAsync(result.DatasetId));
        var reader = new ParquetEvidencePartitionDataReader(
            new EvidenceParquetCodec(),
            fixture.Store);
        var assessment = Assert.Single(
            await reader.ReadSentimentAssessmentsAsync(manifest));
        var assessorInput = Assert.Single(fixture.Assessor.Inputs);
        var expectedContent =
            SentimentAssessmentEvidenceRow.CreateNewsRevisionInputContent(fixture.News[0]);

        Assert.Equal(fixture.News[0].RevisionId, assessment.NewsRevisionId);
        Assert.Equal(expectedContent, assessorInput.CanonicalInputContent);
        Assert.Equal(
            SentimentAssessmentEvidenceRow.ComputeInputContentSha256(expectedContent),
            assessorInput.CanonicalInputSha256);
        Assert.Equal(assessorInput.CanonicalInputSha256, assessment.InputContentSha256);
        Assert.Equal(fixture.News[0].Sources, assessment.Sources);
        Assert.Equal(fixture.News[0].Headline, assessorInput.Headline);
        Assert.Equal(fixture.News[0].Summary, assessorInput.Summary);
        Assert.Equal("fake-finbert", assessment.AssessmentProvider);
        Assert.Equal("ProsusAI/finbert", assessment.AssessmentModel);
        Assert.Equal("model-sha-1", assessment.AssessmentModelVersion);
        Assert.Equal("hf:ProsusAI/finbert@model-sha-1", assessment.ModelArtifactId);
        Assert.Equal(ModelArtifactSha256, assessment.ModelArtifactSha256);
        Assert.Equal(
            ModelTrainingDataCutoffUtc,
            assessment.ModelTrainingDataCutoffUtc);
        Assert.Equal("sentiment-stage-v1", assessment.PromptOrStageVersion);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("cutoff")]
    public async Task Build_RejectsMissingModelVintageWithoutPublishing(string missing)
    {
        var fixture = Fixture();
        var request = fixture.Request with
        {
            ModelArtifactSha256 =
                missing == "hash" ? String.Empty : fixture.Request.ModelArtifactSha256,
            ModelTrainingDataCutoffUtc =
                missing == "cutoff"
                    ? default
                    : fixture.Request.ModelTrainingDataCutoffUtc
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            fixture.Builder.BuildAsync(request));

        Assert.Empty(fixture.Assessor.Inputs);
        Assert.Equal(0, fixture.Catalog.DatasetCount);
        Assert.Equal(0, fixture.Store.ArtifactCount);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("model-version")]
    [InlineData("stage")]
    public async Task Build_RejectsMissingAssessmentMetadataWithoutCatalogVisibility(
        string missing)
    {
        var fixture = Fixture(outputFactory: input =>
        {
            var valid = Output(input);
            return valid with
            {
                AssessmentProvider =
                    missing == "provider" ? " " : valid.AssessmentProvider,
                AssessmentModel =
                    missing == "model" ? "" : valid.AssessmentModel,
                AssessmentModelVersion =
                    missing == "model-version" ? "\t" : valid.AssessmentModelVersion,
                PromptOrStageVersion =
                    missing == "stage" ? " " : valid.PromptOrStageVersion
            };
        });

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            fixture.Builder.BuildAsync(fixture.Request));

        Assert.Equal(0, fixture.Catalog.DatasetCount);
        Assert.Equal(0, fixture.Catalog.CommitCalls);
        Assert.Equal(0, fixture.Store.ArtifactCount);
    }

    [Fact]
    public async Task Build_RejectsFutureAssessmentClockWithoutPublishing()
    {
        var fixture = Fixture(outputFactory: input =>
            Output(input) with
            {
                AssessedAtUtc = Now.AddMinutes(1),
                ObservedAtUtc = Now.AddMinutes(1)
            });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Builder.BuildAsync(fixture.Request));

        Assert.Equal(0, fixture.Catalog.DatasetCount);
        Assert.Equal(0, fixture.Store.ArtifactCount);
    }

    [Fact]
    public async Task Build_CancellationLeavesNoPartialDataset()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = Fixture(async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        using var cancellation = new CancellationTokenSource();

        var build = fixture.Builder.BuildAsync(fixture.Request, cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
        Assert.Equal(0, fixture.Catalog.DatasetCount);
        Assert.Equal(0, fixture.Catalog.CommitCalls);
        Assert.Equal(0, fixture.Store.ArtifactCount);
    }

    [Fact]
    public async Task Build_RestartsAfterCommitFailureAndReusesImmutableArtifact()
    {
        var fixture = Fixture();
        fixture.Catalog.FailNextCommit = true;

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Builder.BuildAsync(fixture.Request));

        Assert.Equal(0, fixture.Catalog.DatasetCount);
        Assert.Equal(1, fixture.Store.ArtifactCount);

        var recovered = await fixture.Builder.BuildAsync(fixture.Request);

        Assert.False(recovered.AlreadyCommitted);
        Assert.Equal(1, fixture.Catalog.DatasetCount);
        Assert.Equal(1, fixture.Store.ArtifactCount);
    }

    [Fact]
    public async Task Build_ImmutableStoreCollisionLeavesNoCatalogDataset()
    {
        var fixture = Fixture();
        fixture.Store.FailNextWrite = true;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Builder.BuildAsync(fixture.Request));

        Assert.Equal(0, fixture.Catalog.DatasetCount);
        Assert.Equal(0, fixture.Catalog.CommitCalls);
        Assert.Equal(0, fixture.Store.ArtifactCount);
    }

    [Fact]
    public async Task Build_ConcurrentCallsConvergeOnOneDataset()
    {
        var fixture = Fixture();

        var results = await Task.WhenAll(
            fixture.Builder.BuildAsync(fixture.Request),
            fixture.Builder.BuildAsync(fixture.Request),
            fixture.Builder.BuildAsync(fixture.Request));

        Assert.Single(results.Select(result => result.DatasetId).Distinct());
        Assert.Equal(1, fixture.Catalog.DatasetCount);
        Assert.Equal(1, fixture.Store.ArtifactCount);
        Assert.Single(results, result => !result.AlreadyCommitted);
    }

    [Fact]
    public async Task Build_LogicalCollisionReturnsAuthoritativeDataset()
    {
        var fixture = Fixture();
        var first = await fixture.Builder.BuildAsync(fixture.Request);
        var laterRequest = fixture.Request with { CreatedAtUtc = Now.AddMinutes(1) };
        var laterFixture = Fixture(
            fixture.Catalog,
            fixture.Store,
            fixture.News,
            laterRequest);

        var second = await laterFixture.Builder.BuildAsync(laterRequest);

        Assert.Equal(first.DatasetId, second.DatasetId);
        Assert.True(second.AlreadyCommitted);
        Assert.Equal(1, fixture.Catalog.DatasetCount);
    }

    [Fact]
    public async Task Build_RejectsDuplicateNewsRevisionBeforeAssessment()
    {
        var news = News();
        var fixture = Fixture(news: [news, news]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Builder.BuildAsync(fixture.Request));

        Assert.Empty(fixture.Assessor.Inputs);
        Assert.Equal(0, fixture.Catalog.DatasetCount);
        Assert.Equal(0, fixture.Store.ArtifactCount);
    }

    private static TestFixture Fixture(
        Func<EvidenceSentimentAssessmentInput, EvidenceSentimentAssessmentOutput>?
            outputFactory = null,
        IReadOnlyList<NewsRevisionEvidenceRow>? news = null) =>
        Fixture(
            null,
            null,
            news ?? [News()],
            null,
            outputFactory is null
                ? null
                : (input, _) => Task.FromResult(outputFactory(input)));

    private static TestFixture Fixture(
        Func<
            EvidenceSentimentAssessmentInput,
            CancellationToken,
            Task<EvidenceSentimentAssessmentOutput>> assessor,
        IReadOnlyList<NewsRevisionEvidenceRow>? news = null) =>
        Fixture(null, null, news ?? [News()], null, assessor);

    private static TestFixture Fixture(
        TestCatalog? existingCatalog,
        InMemoryArtifactStore? existingStore,
        IReadOnlyList<NewsRevisionEvidenceRow> news,
        EvidenceSentimentAssessmentBuildRequest? request = null,
        Func<
            EvidenceSentimentAssessmentInput,
            CancellationToken,
            Task<EvidenceSentimentAssessmentOutput>>? assessor = null)
    {
        var source = SourceManifest(news);
        var catalog = existingCatalog ?? new TestCatalog(source);
        if (existingCatalog is not null)
        {
            catalog.AddSource(source);
        }

        var store = existingStore ?? new InMemoryArtifactStore();
        var fakeAssessor = new FakeAssessor(
            assessor ?? ((input, _) => Task.FromResult(Output(input))));
        var builder = new EvidenceSentimentAssessmentBuilder(
            catalog,
            new TestPartitionReader(news),
            new EvidenceParquetPartitionPublisher(
                new EvidenceParquetCodec(),
                store),
            fakeAssessor,
            new FixedTimeProvider(Now.AddMinutes(5)));
        var effectiveRequest = request ?? new EvidenceSentimentAssessmentBuildRequest(
            source.DatasetId,
            "sentiment-run",
            ConfigHash,
            "test-code",
            "sentiment-builder-v1",
            "hf:ProsusAI/finbert@model-sha-1",
            ModelArtifactSha256,
            ModelTrainingDataCutoffUtc,
            Now,
            2);
        return new(
            builder,
            effectiveRequest,
            source,
            news,
            catalog,
            store,
            fakeAssessor);
    }

    private static EvidenceSentimentAssessmentOutput Output(
        EvidenceSentimentAssessmentInput input) =>
        new(
            input.Headline.Contains("beats", StringComparison.OrdinalIgnoreCase)
                ? 0.75m
                : 0.25m,
            "fake-finbert",
            "ProsusAI/finbert",
            "model-sha-1",
            input.ModelArtifactId,
            input.ModelArtifactSha256,
            input.ModelTrainingDataCutoffUtc,
            "sentiment-stage-v1",
            Now.AddSeconds(-2),
            Now.AddSeconds(-1));

    private static NewsRevisionEvidenceRow News() =>
        new(
            1,
            "news-run",
            Hash("news-config"),
            "news-code",
            "benzinga",
            NewsAvailabilityEvidence.ObservedReceiptTime,
            "alpaca",
            "article-1",
            "revision-1",
            "Issuer beats expectations",
            "Revenue and guidance increased.",
            "https://example.test/news/article-1",
            ["MSFT"],
            ["earnings"],
            Now.AddHours(-2),
            Now.AddHours(-2),
            Now.AddHours(-2),
            Now.AddHours(-2).AddSeconds(1),
            [new EvidenceRowSourceAddress("observation-1", SourceHash)]);

    private static EvidenceDatasetManifest SourceManifest(
        IReadOnlyList<NewsRevisionEvidenceRow> news)
    {
        var sourceArtifact = new EvidenceArtifactReference(
            new EvidenceContentAddress(
                SourceHash,
                100,
                "application/json"),
            new EvidenceObjectNamespace("raw/alpaca/news"));
        var sourceReference = new EvidenceSourceReference(
            "observation-1",
            sourceArtifact,
            Now.AddHours(-2).AddSeconds(1));
        var partitionArtifact = new EvidenceArtifactReference(
            new EvidenceContentAddress(
                Hash("news-partition"),
                200,
                EvidenceParquetCodec.MediaType),
            new EvidenceObjectNamespace("normalized/news"));
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
                news.SelectMany(row => row.Symbols).Distinct().ToArray(),
                "event",
                Now.AddDays(-1),
                Now),
            news.Min(row => row.SourceTimestampUtc),
            news.Max(row => row.SourceTimestampUtc),
            news.Count,
            partitionArtifact,
            [sourceReference],
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
            Now.AddHours(-1),
            "news-collection-job",
            Hash("news-plan"),
            Hash("news-config"),
            "news-code",
            "news-normalizer-v1",
            "benzinga",
            [partition],
            new EvidenceQualityReport());
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed record TestFixture(
        EvidenceSentimentAssessmentBuilder Builder,
        EvidenceSentimentAssessmentBuildRequest Request,
        EvidenceDatasetManifest Source,
        IReadOnlyList<NewsRevisionEvidenceRow> News,
        TestCatalog Catalog,
        InMemoryArtifactStore Store,
        FakeAssessor Assessor);

    private sealed class FakeAssessor(
        Func<
            EvidenceSentimentAssessmentInput,
            CancellationToken,
            Task<EvidenceSentimentAssessmentOutput>> assess) :
        IEvidenceSentimentAssessor
    {
        public ConcurrentQueue<EvidenceSentimentAssessmentInput> Inputs { get; } = new();

        public Task<EvidenceSentimentAssessmentOutput> AssessAsync(
            EvidenceSentimentAssessmentInput input,
            CancellationToken cancellationToken = default)
        {
            Inputs.Enqueue(input);
            return assess(input, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InMemoryArtifactStore : IImmutableArtifactStore
    {
        private readonly ConcurrentDictionary<(string Namespace, string Hash), byte[]>
            artifacts = new();

        public bool FailNextWrite { get; set; }
        public int ArtifactCount => artifacts.Count;

        public Task<ImmutableArtifactReceipt> PutIfAbsentAsync(
            ImmutableArtifactWriteRequest request,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new InvalidDataException("Synthetic immutable collision.");
            }

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

            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return Task.FromResult(new ImmutableArtifactVerification(
                hash.Equals(artifact.Content.Sha256, StringComparison.Ordinal) &&
                bytes.LongLength == artifact.Content.ByteLength,
                artifact,
                bytes.LongLength,
                hash,
                null));
        }
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

        public Task<IReadOnlyList<SentimentAssessmentEvidenceRow>>
            ReadSentimentAssessmentsAsync(
                EvidenceDatasetManifest manifest,
                CancellationToken cancellationToken = default) =>
            Unused<SentimentAssessmentEvidenceRow>();

        public Task<IReadOnlyList<SecurityMasterSnapshotEvidenceRow>>
            ReadSecurityMasterSnapshotsAsync(
                EvidenceDatasetManifest manifest,
                CancellationToken cancellationToken = default) =>
            Unused<SecurityMasterSnapshotEvidenceRow>();

        public Task<IReadOnlyList<SymbolIntervalEvidenceRow>> ReadSymbolIntervalsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) =>
            Unused<SymbolIntervalEvidenceRow>();

        public Task<IReadOnlyList<CorporateActionEvidenceRow>> ReadCorporateActionsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) =>
            Unused<CorporateActionEvidenceRow>();

        public Task<IReadOnlyList<UniverseMembershipEvidenceRow>>
            ReadUniverseMembershipAsync(
                EvidenceDatasetManifest manifest,
                CancellationToken cancellationToken = default) =>
            Unused<UniverseMembershipEvidenceRow>();

        public Task<IReadOnlyList<SipQuoteEvidenceRow>> ReadSipQuotesAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) =>
            Unused<SipQuoteEvidenceRow>();

        public Task<IReadOnlyList<ExchangeSessionEvidenceRow>> ReadExchangeSessionsAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default) =>
            Unused<ExchangeSessionEvidenceRow>();

        private static Task<IReadOnlyList<T>> Unused<T>() =>
            throw new NotSupportedException();
    }

    private sealed class TestCatalog(EvidenceDatasetManifest source) : IEvidenceCatalog
    {
        private readonly object sync = new();
        private readonly Dictionary<string, EvidenceDatasetManifest> byId =
            new(StringComparer.Ordinal)
            {
                [source.DatasetId] = source
            };
        private readonly Dictionary<string, string> byLogicalKey =
            new(StringComparer.Ordinal);

        public bool FailNextCommit { get; set; }
        public int CommitCalls { get; private set; }
        public int DatasetCount
        {
            get
            {
                lock (sync)
                {
                    return byLogicalKey.Count;
                }
            }
        }

        public void AddSource(EvidenceDatasetManifest manifest)
        {
            lock (sync)
            {
                byId[manifest.DatasetId] = manifest;
            }
        }

        public Task<EvidenceDatasetManifest?> GetDatasetAsync(
            string datasetId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                byId.TryGetValue(datasetId, out var manifest);
                return Task.FromResult(manifest);
            }
        }

        public Task<EvidenceCatalogCommitResult> CommitDatasetAsync(
            EvidenceDatasetManifest manifest,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                CommitCalls++;
                if (FailNextCommit)
                {
                    FailNextCommit = false;
                    throw new IOException("Synthetic catalog commit failure.");
                }

                if (byLogicalKey.TryGetValue(
                        manifest.LogicalDatasetKey,
                        out var existingId))
                {
                    return Task.FromResult(
                        new EvidenceCatalogCommitResult(existingId, true));
                }

                byLogicalKey.Add(manifest.LogicalDatasetKey, manifest.DatasetId);
                byId.Add(manifest.DatasetId, manifest);
                return Task.FromResult(
                    new EvidenceCatalogCommitResult(manifest.DatasetId, false));
            }
        }

        public Task<EvidenceCatalogCommitResult> RegisterCollectionPlanAsync(
            EvidenceCollectionPlan plan,
            CancellationToken cancellationToken = default) => Unused<EvidenceCatalogCommitResult>();
        public Task<EvidenceCollectionPlan?> GetCollectionPlanAsync(
            string jobId,
            CancellationToken cancellationToken = default) => Unused<EvidenceCollectionPlan?>();
        public Task SaveCollectionCheckpointAsync(
            EvidenceCollectionCheckpoint checkpoint,
            CancellationToken cancellationToken = default) => Unused();
        public Task<EvidenceCollectionCheckpoint?> GetCollectionCheckpointAsync(
            string jobId,
            CancellationToken cancellationToken = default) => Unused<EvidenceCollectionCheckpoint?>();
        public Task SaveRequestCursorCheckpointAsync(
            EvidenceRequestCursorCheckpoint checkpoint,
            CancellationToken cancellationToken = default) => Unused();
        public Task<EvidenceRequestCursorCheckpoint?> GetRequestCursorCheckpointAsync(
            string jobId,
            string requestId,
            CancellationToken cancellationToken = default) => Unused<EvidenceRequestCursorCheckpoint?>();
        public Task<EvidenceCatalogCommitResult> RegisterSourceObservationAsync(
            EvidenceSourceObservation observation,
            CancellationToken cancellationToken = default) => Unused<EvidenceCatalogCommitResult>();
        public Task<EvidenceSourceObservation?> GetSourceObservationAsync(
            string observationId,
            CancellationToken cancellationToken = default) => Unused<EvidenceSourceObservation?>();
        public Task<IReadOnlyList<EvidenceSourceObservation>> FindSourceObservationsAsync(
            string jobId,
            string requestId,
            CancellationToken cancellationToken = default) => Unused<IReadOnlyList<EvidenceSourceObservation>>();
        public Task<IReadOnlyList<EvidenceDatasetManifest>> FindDatasetsAsync(
            EvidenceDatasetQuery query,
            CancellationToken cancellationToken = default) => Unused<IReadOnlyList<EvidenceDatasetManifest>>();
        public Task<EvidenceCatalogCommitResult> RegisterResearchRunAsync(
            EvidenceResearchRunManifest manifest,
            CancellationToken cancellationToken = default) => Unused<EvidenceCatalogCommitResult>();
        public Task<EvidenceResearchRunManifest?> GetResearchRunAsync(
            string researchRunId,
            CancellationToken cancellationToken = default) => Unused<EvidenceResearchRunManifest?>();
        public Task<EvidenceHoldoutConsumption?> GetHoldoutConsumptionAsync(
            string holdoutId,
            CancellationToken cancellationToken = default) => Unused<EvidenceHoldoutConsumption?>();

        public Task<EvidenceCatalogCommitResult> ReserveHoldoutAsync(
            EvidenceHoldoutConsumption consumption,
            CancellationToken cancellationToken = default) =>
            Unused<EvidenceCatalogCommitResult>();
        public Task<EvidenceCatalogCommitResult> RegisterQuarantineAsync(
            EvidenceQuarantineRecord quarantine,
            CancellationToken cancellationToken = default) => Unused<EvidenceCatalogCommitResult>();
        public Task<EvidenceQuarantineEntry?> GetQuarantineAsync(
            string quarantineId,
            CancellationToken cancellationToken = default) => Unused<EvidenceQuarantineEntry?>();
        public Task<IReadOnlyList<EvidenceQuarantineEntry>> FindQuarantinesAsync(
            EvidenceQuarantineStatus status,
            CancellationToken cancellationToken = default) => Unused<IReadOnlyList<EvidenceQuarantineEntry>>();
        public Task ResolveQuarantineAsync(
            EvidenceQuarantineResolution resolution,
            CancellationToken cancellationToken = default) => Unused();
        public Task<IReadOnlyList<EvidenceArtifactReference>> ResolveReferenceClosureAsync(
            EvidencePinSubject subject,
            CancellationToken cancellationToken = default) => Unused<IReadOnlyList<EvidenceArtifactReference>>();
        public Task<EvidenceRetentionPin> PinAsync(
            EvidencePinSubject subject,
            string reason,
            CancellationToken cancellationToken = default) => Unused<EvidenceRetentionPin>();

        private static Task Unused() => throw new NotSupportedException();
        private static Task<T> Unused<T>() => throw new NotSupportedException();
    }
}

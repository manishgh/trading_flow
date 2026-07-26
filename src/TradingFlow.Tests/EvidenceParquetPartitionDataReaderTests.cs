using System.Security.Cryptography;
using System.Text;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class EvidenceParquetPartitionDataReaderTests
{
    private const string RunId = "reader-run";
    private const string ConfigHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string CodeVersion = "commit-reader";
    private const string NormalizerVersion = "normalizer-reader-v1";
    private const string SourceHashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SourceHashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task SipQuotes_AreDeterministicAndExactAfterRoundTrip()
    {
        var codec = new EvidenceParquetCodec();
        var later = Quote(
            Utc(2026, 7, 24, 14, 30, 2),
            "observation-b",
            SourceHashB);
        var earlier = Quote(
            Utc(2026, 7, 24, 14, 30, 1),
            "observation-a",
            SourceHashA);

        var first = await codec.WriteSipQuotesAsync(
            [later, earlier],
            NormalizerVersion);
        var second = await codec.WriteSipQuotesAsync(
            [earlier, later],
            NormalizerVersion);
        var decoded = await codec.ReadSipQuotesAsync(first);

        Assert.Equal(first, second);
        Assert.Equal(EvidenceDatasetKind.SipQuotes, decoded.DatasetKind);
        Assert.Equal(NormalizerVersion, decoded.Metadata["tradingflow.normalizer_version"]);
        Assert.Equal(
            [EvidenceCanonicalJson.ComputeSha256(earlier), EvidenceCanonicalJson.ComputeSha256(later)],
            decoded.Rows.Select(EvidenceCanonicalJson.ComputeSha256));
    }

    [Fact]
    public async Task Reader_VerifiesAndCombinesMultiplePartitionsInCanonicalOrder()
    {
        var codec = new EvidenceParquetCodec();
        var store = new InMemoryArtifactStore();
        var later = Bar(
            Utc(2026, 7, 24, 14, 31),
            "observation-b",
            SourceHashB);
        var earlier = Bar(
            Utc(2026, 7, 24, 14, 30),
            "observation-a",
            SourceHashA);
        var laterPartition = await PartitionAsync(
            codec,
            store,
            "partition-b",
            EvidenceDatasetKind.MarketBarsAsTraded,
            [later],
            SourceReference("observation-b", SourceHashB));
        var earlierPartition = await PartitionAsync(
            codec,
            store,
            "partition-a",
            EvidenceDatasetKind.MarketBarsAsTraded,
            [earlier],
            SourceReference("observation-a", SourceHashA));
        var manifest = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            [laterPartition, earlierPartition]);

        var reader = new ParquetEvidencePartitionDataReader(codec, store);
        var rows = await reader.ReadMarketBarsAsync(manifest);

        Assert.Equal([earlier.BarStartUtc, later.BarStartUtc], rows.Select(row => row.BarStartUtc));
        Assert.Equal(2, store.VerifiedArtifacts.Count);
        Assert.Equal(2, store.OpenedArtifacts.Count);
        Assert.All(
            store.OpenedArtifacts,
            artifact => Assert.Contains(artifact, store.VerifiedArtifacts));
    }

    [Fact]
    public async Task Reader_RejectsTamperedArtifactBeforeOpeningIt()
    {
        var codec = new EvidenceParquetCodec();
        var store = new InMemoryArtifactStore();
        var row = Bar(
            Utc(2026, 7, 24, 14, 30),
            "observation-a",
            SourceHashA);
        var partition = await PartitionAsync(
            codec,
            store,
            "partition",
            EvidenceDatasetKind.MarketBarsAsTraded,
            [row],
            SourceReference("observation-a", SourceHashA));
        store.Tamper(partition.Artifact);
        var reader = new ParquetEvidencePartitionDataReader(codec, store);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadMarketBarsAsync(Manifest(
                EvidenceDatasetKind.MarketBarsAsTraded,
                [partition])));
        Assert.Empty(store.OpenedArtifacts);
    }

    [Fact]
    public async Task SentimentPublisherAndReader_VerifyImmutableArtifactAndExactRow()
    {
        var codec = new EvidenceParquetCodec();
        var store = new InMemoryArtifactStore();
        var news = News();
        var assessment = Assessment(news);
        var publisher = new EvidenceParquetPartitionPublisher(codec, store);
        var provenance = new EvidencePartitionProvenance(
            "local-finbert",
            "local/finbert",
            "finbert",
            "none",
            "USD",
            null,
            [],
            [],
            ["MSFT"],
            "news",
            Utc(2026, 7, 24, 12),
            Utc(2026, 7, 24, 13));
        var partition = await publisher.PublishSentimentAssessmentsAsync(
            new EvidenceParquetPartitionRequest(
                "sentiment-partition",
                EvidenceDatasetKind.SentimentAssessments,
                SentimentAssessmentEvidenceRow.ContractSchemaVersion,
                provenance,
                [SourceReference("news-observation", SourceHashA)],
                RunId,
                ConfigHash,
                NormalizerVersion,
                CodeVersion,
                new EvidenceQualityReport(),
                new EvidenceObjectNamespace("normalized/sentiment"),
                Dimensions: new Dictionary<string, string>
                {
                    ["model_artifact_id"] = assessment.ModelArtifactId,
                    ["model_artifact_sha256"] = assessment.ModelArtifactSha256,
                    ["model_training_data_cutoff_utc"] =
                        assessment.ModelTrainingDataCutoffUtc.ToString("O")
                }),
            [assessment]);
        var manifest = EvidenceParquetPartitionPublisher.BuildDatasetManifest(
            EvidenceDatasetKind.SentimentAssessments,
            SentimentAssessmentEvidenceRow.ContractSchemaVersion,
            Utc(2026, 7, 24, 13),
            "sentiment-job",
            Hash("sentiment-plan"),
            ConfigHash,
            CodeVersion,
            NormalizerVersion,
            "finbert",
            [partition],
            new EvidenceQualityReport());
        var reader = new ParquetEvidencePartitionDataReader(codec, store);

        var decoded = Assert.Single(await reader.ReadSentimentAssessmentsAsync(manifest));

        Assert.Equal(
            EvidenceCanonicalJson.ComputeSha256(assessment),
            EvidenceCanonicalJson.ComputeSha256(decoded));
        Assert.Contains(partition.Artifact, store.VerifiedArtifacts);
        Assert.Contains(partition.Artifact, store.OpenedArtifacts);

        var openedBeforeTamper = store.OpenedArtifacts.Count;
        store.Tamper(partition.Artifact);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadSentimentAssessmentsAsync(manifest));
        Assert.Equal(openedBeforeTamper, store.OpenedArtifacts.Count);
    }

    [Fact]
    public async Task Reader_RejectsWrongReaderKind()
    {
        var codec = new EvidenceParquetCodec();
        var store = new InMemoryArtifactStore();
        var row = Bar(
            Utc(2026, 7, 24, 14, 30),
            "observation-a",
            SourceHashA);
        var partition = await PartitionAsync(
            codec,
            store,
            "partition",
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            [row with { }],
            SourceReference("observation-a", SourceHashA),
            adjustment: "all");
        var reader = new ParquetEvidencePartitionDataReader(codec, store);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadSipQuotesAsync(Manifest(
                EvidenceDatasetKind.MarketBarsResearchAdjusted,
                [partition])));
        Assert.Empty(store.VerifiedArtifacts);
    }

    [Fact]
    public async Task Reader_RejectsRowSourceAbsentFromManifest()
    {
        var codec = new EvidenceParquetCodec();
        var store = new InMemoryArtifactStore();
        var row = Bar(
            Utc(2026, 7, 24, 14, 30),
            "observation-a",
            SourceHashA);
        var partition = await PartitionAsync(
            codec,
            store,
            "partition",
            EvidenceDatasetKind.MarketBarsAsTraded,
            [row],
            SourceReference("observation-b", SourceHashB));
        var reader = new ParquetEvidencePartitionDataReader(codec, store);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadMarketBarsAsync(Manifest(
                EvidenceDatasetKind.MarketBarsAsTraded,
                [partition])));
    }

    [Fact]
    public async Task Reader_RejectsWrongRunAndNormalizerLineage()
    {
        var codec = new EvidenceParquetCodec();
        var store = new InMemoryArtifactStore();
        var row = Bar(
            Utc(2026, 7, 24, 14, 30),
            "observation-a",
            SourceHashA);
        var partition = await PartitionAsync(
            codec,
            store,
            "partition",
            EvidenceDatasetKind.MarketBarsAsTraded,
            [row],
            SourceReference("observation-a", SourceHashA),
            dimensions: Dimensions("another-run", "another-normalizer"));
        var manifest = Manifest(
            EvidenceDatasetKind.MarketBarsAsTraded,
            [partition],
            normalizerVersion: "another-normalizer");
        var reader = new ParquetEvidencePartitionDataReader(codec, store);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadMarketBarsAsync(manifest));
    }

    [Fact]
    public async Task Reader_RejectsDuplicateLogicalKeysAcrossPartitions()
    {
        var codec = new EvidenceParquetCodec();
        var store = new InMemoryArtifactStore();
        var row = Bar(
            Utc(2026, 7, 24, 14, 30),
            "observation-a",
            SourceHashA);
        var first = await PartitionAsync(
            codec,
            store,
            "partition-a",
            EvidenceDatasetKind.MarketBarsAsTraded,
            [row],
            SourceReference("observation-a", SourceHashA));
        var second = await PartitionAsync(
            codec,
            store,
            "partition-b",
            EvidenceDatasetKind.MarketBarsAsTraded,
            [row],
            SourceReference("observation-a", SourceHashA));
        var reader = new ParquetEvidencePartitionDataReader(codec, store);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadMarketBarsAsync(Manifest(
                EvidenceDatasetKind.MarketBarsAsTraded,
                [first, second])));
    }

    private static async Task<EvidenceDatasetPartitionManifest> PartitionAsync(
        EvidenceParquetCodec codec,
        InMemoryArtifactStore store,
        string partitionId,
        EvidenceDatasetKind kind,
        IReadOnlyCollection<MarketBarEvidenceRow> rows,
        EvidenceSourceReference source,
        string adjustment = "raw",
        IReadOnlyDictionary<string, string>? dimensions = null)
    {
        var content = kind switch
        {
            EvidenceDatasetKind.MarketBarsAsTraded =>
                await codec.WriteMarketBarsAsTradedAsync(rows, NormalizerVersion),
            EvidenceDatasetKind.MarketBarsResearchAdjusted =>
                await codec.WriteResearchAdjustedMarketBarsAsync(rows, NormalizerVersion),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var artifact = store.Add(content);
        return new(
            partitionId,
            kind,
            1,
            MarketProvenance(adjustment),
            rows.Min(row => row.SourceTimestampUtc),
            rows.Max(row => row.SourceTimestampUtc),
            rows.Count,
            artifact,
            [source],
            dimensions?["normalizer_version"] ?? NormalizerVersion,
            CodeVersion,
            new EvidenceQualityReport(),
            dimensions: dimensions ?? Dimensions());
    }

    private static EvidenceDatasetManifest Manifest(
        EvidenceDatasetKind kind,
        IReadOnlyList<EvidenceDatasetPartitionManifest> partitions,
        string normalizerVersion = NormalizerVersion,
        string dataFeed = "sip") =>
        new(
            kind,
            1,
            Utc(2026, 7, 24, 15),
            "collection-job",
            Hash("plan"),
            ConfigHash,
            CodeVersion,
            normalizerVersion,
            dataFeed,
            partitions,
            new EvidenceQualityReport());

    private static EvidencePartitionProvenance MarketProvenance(string adjustment) =>
        new(
            "alpaca",
            "/v2/stocks/bars",
            "sip",
            adjustment,
            "USD",
            new DateOnly(2026, 7, 24),
            ["security-msft"],
            ["issuer-microsoft"],
            ["MSFT"],
            "1m",
            Utc(2026, 7, 24, 14, 30),
            Utc(2026, 7, 24, 14, 35));

    private static IReadOnlyDictionary<string, string> Dimensions(
        string runId = RunId,
        string normalizerVersion = NormalizerVersion) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["run_id"] = runId,
            ["config_hash"] = ConfigHash,
            ["code_version"] = CodeVersion,
            ["normalizer_version"] = normalizerVersion,
            ["data_feed"] = "sip"
        };

    private static MarketBarEvidenceRow Bar(
        DateTimeOffset start,
        string observationId,
        string observationHash) =>
        new(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "sip",
            "security-msft",
            "MSFT",
            start,
            start.AddMinutes(1),
            "1m",
            EvidenceFixedDecimal.ToPriceUnits(500m),
            EvidenceFixedDecimal.ToPriceUnits(501m),
            EvidenceFixedDecimal.ToPriceUnits(499m),
            EvidenceFixedDecimal.ToPriceUnits(500.5m),
            EvidenceFixedDecimal.ToPriceUnits(500.25m),
            1_000,
            25,
            "raw",
            "USD",
            start,
            start.AddMilliseconds(100),
            [new EvidenceRowSourceAddress(observationId, observationHash)]);

    private static SipQuoteEvidenceRow Quote(
        DateTimeOffset timestamp,
        string observationId,
        string observationHash) =>
        new(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "security-msft",
            "MSFT",
            timestamp,
            EvidenceFixedDecimal.ToPriceUnits(500.10m),
            EvidenceFixedDecimal.ToPriceUnits(500.12m),
            200,
            150,
            "Q",
            "P",
            "sip",
            "USD",
            ["R"],
            timestamp,
            timestamp.AddMilliseconds(50),
            [new EvidenceRowSourceAddress(observationId, observationHash)]);

    private static NewsRevisionEvidenceRow News()
    {
        var publishedAt = Utc(2026, 7, 24, 12);
        return new(
            SentimentAssessmentEvidenceRow.ContractSchemaVersion,
            RunId,
            ConfigHash,
            CodeVersion,
            "alpaca",
            NewsAvailabilityEvidence.ObservedReceiptTime,
            "alpaca",
            "article-msft",
            "article-msft-r1",
            "Microsoft raises guidance",
            "Management raised its outlook.",
            "https://example.test/article-msft",
            ["MSFT"],
            ["guidance"],
            publishedAt,
            publishedAt,
            publishedAt,
            publishedAt.AddSeconds(1),
            [new EvidenceRowSourceAddress("news-observation", SourceHashA)]);
    }

    private static SentimentAssessmentEvidenceRow Assessment(
        NewsRevisionEvidenceRow news)
    {
        var input =
            SentimentAssessmentEvidenceRow.CreateNewsRevisionInputContent(news);
        return new(
            SentimentAssessmentEvidenceRow.ContractSchemaVersion,
            RunId,
            ConfigHash,
            CodeVersion,
            "finbert",
            "assessment-msft-r1",
            news.Provider,
            news.ProviderArticleId,
            news.RevisionId,
            input,
            SentimentAssessmentEvidenceRow.ComputeInputContentSha256(input),
            "local-finbert",
            "ProsusAI/finbert",
            "model-v1",
            "hf:ProsusAI/finbert@model-v1",
            Hash("finbert-model-artifact"),
            news.AvailabilityTimestampUtc.AddDays(-30),
            "stage-v1",
            0.75m,
            news.AvailabilityTimestampUtc,
            news.AvailabilityTimestampUtc,
            news.Sources);
    }

    private static EvidenceSourceReference SourceReference(string id, string hash) =>
        new(
            id,
            new EvidenceArtifactReference(
                new EvidenceContentAddress(hash, 1, "application/json"),
                new EvidenceObjectNamespace("raw/test")),
            Utc(2026, 7, 24, 14, 35));

    private static DateTimeOffset Utc(
        int year,
        int month,
        int day,
        int hour = 0,
        int minute = 0,
        int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class InMemoryArtifactStore : IImmutableArtifactStore
    {
        private readonly Dictionary<string, byte[]> content = new(StringComparer.Ordinal);

        public List<EvidenceArtifactReference> VerifiedArtifacts { get; } = [];

        public List<EvidenceArtifactReference> OpenedArtifacts { get; } = [];

        public EvidenceArtifactReference Add(byte[] bytes)
        {
            var artifact = new EvidenceArtifactReference(
                new EvidenceContentAddress(
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    bytes.LongLength,
                    EvidenceParquetCodec.MediaType),
                new EvidenceObjectNamespace("normalized/test"));
            content[artifact.Content.Sha256] = bytes.ToArray();
            return artifact;
        }

        public void Tamper(EvidenceArtifactReference artifact)
        {
            var bytes = content[artifact.Content.Sha256].ToArray();
            bytes[0] ^= 0xff;
            content[artifact.Content.Sha256] = bytes;
        }

        public Task<ImmutableArtifactReceipt> PutIfAbsentAsync(
            ImmutableArtifactWriteRequest request,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existed = content.ContainsKey(request.Artifact.Content.Sha256);
            content.TryAdd(request.Artifact.Content.Sha256, bytes.ToArray());
            return Task.FromResult(new ImmutableArtifactReceipt(
                request.Artifact,
                request.Artifact.Content.Sha256,
                existed));
        }

        public Task<Stream> OpenReadAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenedArtifacts.Add(artifact);
            return Task.FromResult<Stream>(
                new MemoryStream(content[artifact.Content.Sha256], writable: false));
        }

        public Task<ImmutableArtifactVerification> VerifyAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifiedArtifacts.Add(artifact);
            var bytes = content[artifact.Content.Sha256];
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var valid = bytes.LongLength == artifact.Content.ByteLength &&
                        actualHash.Equals(artifact.Content.Sha256, StringComparison.Ordinal);
            return Task.FromResult(new ImmutableArtifactVerification(
                valid,
                artifact,
                bytes.LongLength,
                actualHash,
                valid ? null : "content_hash_mismatch"));
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using Parquet;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;

namespace TradingFlow.Tests;

public sealed class EvidenceParquetCodecTests : IDisposable
{
    private const string SourceHashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SourceHashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ConfigHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string RunId = "research-run-20260724";
    private const string CodeVersion = "commit-abc";
    private const string NormalizerVersion = "normalizer-v1";
    private readonly string artifactRoot = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-parquet-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AdjustedBars_AreDeterministicSortedAndExactAfterRoundTrip()
    {
        var codec = new EvidenceParquetCodec();
        var later = Bar(
            "security-msft",
            "MSFT",
            Utc(2026, 7, 24, 14, 31),
            Source("observation-b", SourceHashB),
            close: 514.123456m);
        var earlier = Bar(
            "security-msft",
            "MSFT",
            Utc(2026, 7, 24, 14, 30),
            Source("observation-a", SourceHashA),
            close: 513.987654m);

        var first = await codec.WriteResearchAdjustedMarketBarsAsync([later, earlier], NormalizerVersion);
        var second = await codec.WriteResearchAdjustedMarketBarsAsync([earlier, later], NormalizerVersion);
        var decoded = await codec.ReadResearchAdjustedMarketBarsAsync(first);

        Assert.Equal(first, second);
        Assert.Equal(EvidenceDatasetKind.MarketBarsResearchAdjusted, decoded.DatasetKind);
        Assert.Equal(1, decoded.SchemaVersion);
        Assert.Equal(
            [EvidenceCanonicalJson.ComputeSha256(earlier), EvidenceCanonicalJson.ComputeSha256(later)],
            decoded.Rows.Select(EvidenceCanonicalJson.ComputeSha256));
        Assert.Equal("unix_microseconds_utc", decoded.Metadata["tradingflow.timestamp_encoding"]);
        Assert.Equal("6", decoded.Metadata["tradingflow.price_scale"]);
        Assert.Equal(
            513.987654m,
            EvidenceFixedDecimal.FromPriceUnits(decoded.Rows[0].ClosePriceUnits));
        Assert.Equal(RunId, decoded.Rows[0].RunId);
        Assert.Equal(ConfigHash, decoded.Rows[0].ConfigHash);
        Assert.Equal(CodeVersion, decoded.Rows[0].CodeVersion);
        Assert.Equal("sip", decoded.Rows[0].DataFeed);
        Assert.Equal(NormalizerVersion, decoded.Metadata["tradingflow.normalizer_version"]);
    }

    [Fact]
    public async Task AsTradedAndResearchAdjustedBars_HaveDistinctKindsAndCannotBeConfused()
    {
        var codec = new EvidenceParquetCodec();
        var source = Source("observation-a", SourceHashA);
        var asTraded = Bar(
            "security-msft",
            "MSFT",
            Utc(2026, 7, 24, 14, 30),
            source,
            adjustment: "raw");
        var researchAdjusted = Bar(
            "security-msft",
            "MSFT",
            Utc(2026, 7, 24, 14, 30),
            source,
            adjustment: "all");

        var asTradedBytes = await codec.WriteMarketBarsAsTradedAsync([asTraded], NormalizerVersion);
        var adjustedBytes = await codec.WriteResearchAdjustedMarketBarsAsync([researchAdjusted], NormalizerVersion);
        var asTradedDocument = await codec.ReadMarketBarsAsTradedAsync(asTradedBytes);
        var adjustedDocument = await codec.ReadResearchAdjustedMarketBarsAsync(adjustedBytes);

        Assert.NotEqual(Hash(asTradedBytes), Hash(adjustedBytes));
        Assert.Equal(EvidenceDatasetKind.MarketBarsAsTraded, asTradedDocument.DatasetKind);
        Assert.Equal(
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            adjustedDocument.DatasetKind);
        Assert.Equal("raw", Assert.Single(asTradedDocument.Rows).Adjustment);
        Assert.Equal("all", Assert.Single(adjustedDocument.Rows).Adjustment);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            codec.ReadResearchAdjustedMarketBarsAsync(asTradedBytes));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            codec.ReadMarketBarsAsTradedAsync(adjustedBytes));
    }

    [Fact]
    public async Task NewsRevisions_PreserveProviderTimelineAndCanonicalCollections()
    {
        var codec = new EvidenceParquetCodec();
        var row = new NewsRevisionEvidenceRow(
            2,
            RunId,
            ConfigHash,
            CodeVersion,
            "benzinga",
            NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly,
            "Alpaca",
            "article-42",
            "revision-2",
            "Company raises guidance",
            "Updated guidance is above consensus.",
            "https://example.com/news/42",
            ["MSFT", "AAPL", "MSFT"],
            ["guidance", "earnings", "guidance"],
            Utc(2026, 7, 24, 12, 0),
            Utc(2026, 7, 24, 12, 0, 1),
            Utc(2026, 7, 24, 12, 4),
            Utc(2026, 7, 24, 12, 4, 1),
            [Source("news-observation", SourceHashA)]);

        var content = await codec.WriteNewsRevisionsAsync([row], NormalizerVersion);
        var decoded = await codec.ReadNewsRevisionsAsync(content);

        var actual = Assert.Single(decoded.Rows);
        Assert.Equal(
            EvidenceCanonicalJson.ComputeSha256(row),
            EvidenceCanonicalJson.ComputeSha256(actual));
        Assert.Equal(["AAPL", "MSFT"], actual.Symbols);
        Assert.Equal(["earnings", "guidance"], actual.Categories);
        Assert.Equal(row.ProviderCreatedAtUtc, actual.ProviderCreatedAtUtc);
        Assert.Equal(row.ProviderUpdatedAtUtc, actual.ProviderUpdatedAtUtc);
        Assert.Equal(row.ReceivedAtUtc, actual.ReceivedAtUtc);
        Assert.Equal(
            NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly,
            actual.AvailabilityEvidence);
        Assert.Equal(actual.ProviderUpdatedAtUtc, actual.AvailabilityTimestampUtc);
        Assert.Equal(
            "ProviderUpdatedTimestampOnly",
            actual.AvailabilityEvidence.ToString());
    }

    [Fact]
    public async Task Schemas_ContainBindingProvenanceAndAvailabilityColumns()
    {
        var codec = new EvidenceParquetCodec();
        var source = Source("observation-a", SourceHashA);
        var barColumns = await ColumnsAsync(
            await codec.WriteMarketBarsAsTradedAsync([
                Bar(
                    "security-msft",
                    "MSFT",
                    Utc(2026, 7, 24, 14, 30),
                    source,
                    adjustment: "raw")
            ], NormalizerVersion));
        var newsColumns = await ColumnsAsync(
            await codec.WriteNewsRevisionsAsync([
                News(
                    NewsAvailabilityEvidence.ProviderTimestampOnly,
                    Utc(2026, 7, 24, 12, 0),
                    Utc(2026, 7, 24, 12, 1),
                    Utc(2026, 7, 24, 12, 2))
            ], NormalizerVersion));
        var securityMasterColumns = await ColumnsAsync(
            await codec.WriteSecurityMasterAsync([
                SecurityMaster()
            ], NormalizerVersion));
        var universeColumns = await ColumnsAsync(
            await codec.WriteUniverseMembershipAsync([
                UniverseMembership(
                    included: true,
                    rank: 1,
                    snapshotHash: SourceHashA,
                    sourceHash: SourceHashA)
            ], NormalizerVersion));

        string[] common = ["schema_version", "run_id", "config_hash", "code_version", "data_feed", "sources_json"];
        Assert.All(common, column => Assert.Contains(column, barColumns));
        Assert.All(common, column => Assert.Contains(column, newsColumns));
        Assert.All(common, column => Assert.Contains(column, securityMasterColumns));
        Assert.All(common, column => Assert.Contains(column, universeColumns));
        Assert.Contains("availability_evidence", newsColumns);
        Assert.Contains("provider_query", universeColumns);
        Assert.Contains("snapshot_content_sha256", universeColumns);
        Assert.Contains("effective_session_day_number", universeColumns);
    }

    [Fact]
    public async Task SecurityMasterAndUniverseMembership_RoundTripPointInTimeIdentity()
    {
        var codec = new EvidenceParquetCodec();
        var source = Source("master-observation", SourceHashA);
        var security = new SecurityMasterEvidenceRow(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "alpaca_reference",
            "alpaca",
            "security-aapl",
            "issuer-apple",
            "AAPL",
            "NASDAQ",
            "us_equity",
            "USD",
            "active",
            Utc(2026, 1, 1),
            null,
            Utc(2026, 7, 24, 10, 0),
            Utc(2026, 7, 24, 10, 0, 1),
            [source]);
        var membership = new UniverseMembershipEvidenceRow(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "finviz_elite",
            "finviz",
            "mega-cap-swing",
            "snapshot-20260724-1000",
            "v=111&f=cap_mega",
            SourceHashA,
            new DateOnly(2026, 7, 24),
            "security-aapl",
            "issuer-apple",
            "AAPL",
            Utc(2026, 7, 24, 10, 1),
            true,
            1,
            "market_cap_over_100b",
            Utc(2026, 7, 24, 10, 0),
            Utc(2026, 7, 24, 10, 0, 2),
            [source]);

        var securityBytes = await codec.WriteSecurityMasterAsync([security], NormalizerVersion);
        var universeBytes = await codec.WriteUniverseMembershipAsync([membership], NormalizerVersion);

        Assert.Equal(
            EvidenceCanonicalJson.ComputeSha256(security),
            EvidenceCanonicalJson.ComputeSha256(
                Assert.Single((await codec.ReadSecurityMasterAsync(securityBytes)).Rows)));
        Assert.Equal(
            EvidenceCanonicalJson.ComputeSha256(membership),
            EvidenceCanonicalJson.ComputeSha256(
                Assert.Single((await codec.ReadUniverseMembershipAsync(universeBytes)).Rows)));
        var snapshot = membership.ToProviderSnapshot();
        Assert.Equal(membership.Provider, snapshot.ProviderName);
        Assert.Equal(membership.ReceivedAtUtc, snapshot.ObservedAtUtc);
        Assert.Equal(membership.EffectiveSessionDate, snapshot.EffectiveSessionDate);
        Assert.Equal(membership.ProviderQuery, snapshot.Query);
        Assert.Equal(membership.SnapshotContentSha256, snapshot.ContentSha256);
    }

    [Fact]
    public async Task Writer_RejectsDuplicateIdentityAndMixedSchemaVersions()
    {
        var codec = new EvidenceParquetCodec();
        var source = Source("observation-a", SourceHashA);
        var first = Bar(
            "security-msft",
            "MSFT",
            Utc(2026, 7, 24, 14, 30),
            source);
        var duplicate = Bar(
            "security-msft",
            "MSFT",
            Utc(2026, 7, 24, 14, 30),
            Source("observation-b", SourceHashB));
        var differentSchema = new MarketBarEvidenceRow(
            2,
            first.RunId,
            first.ConfigHash,
            first.CodeVersion,
            first.DataFeed,
            first.SecurityId,
            first.Symbol,
            first.BarStartUtc.AddMinutes(1),
            first.BarEndUtc.AddMinutes(1),
            first.Timeframe,
            first.OpenPriceUnits,
            first.HighPriceUnits,
            first.LowPriceUnits,
            first.ClosePriceUnits,
            first.VwapPriceUnits,
            first.Volume,
            first.TradeCount,
            first.Adjustment,
            first.Currency,
            first.ProviderTimestampUtc.AddMinutes(1),
            first.ReceivedAtUtc.AddMinutes(1),
            [source]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            codec.WriteResearchAdjustedMarketBarsAsync([first, duplicate], NormalizerVersion));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            codec.WriteResearchAdjustedMarketBarsAsync([first, differentSchema], NormalizerVersion));

        var differentRun = new MarketBarEvidenceRow(
            1,
            "another-run",
            first.ConfigHash,
            first.CodeVersion,
            first.DataFeed,
            first.SecurityId,
            first.Symbol,
            first.BarStartUtc.AddMinutes(1),
            first.BarEndUtc.AddMinutes(1),
            first.Timeframe,
            first.OpenPriceUnits,
            first.HighPriceUnits,
            first.LowPriceUnits,
            first.ClosePriceUnits,
            first.VwapPriceUnits,
            first.Volume,
            first.TradeCount,
            first.Adjustment,
            first.Currency,
            first.ProviderTimestampUtc.AddMinutes(1),
            first.ReceivedAtUtc.AddMinutes(1),
            [source]);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            codec.WriteResearchAdjustedMarketBarsAsync([first, differentRun], NormalizerVersion));
    }

    [Fact]
    public async Task Reader_RejectsMalformedOrWrongDatasetBytes()
    {
        var codec = new EvidenceParquetCodec();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            codec.ReadResearchAdjustedMarketBarsAsync(Encoding.UTF8.GetBytes("not parquet")));

        var news = new NewsRevisionEvidenceRow(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "benzinga",
            NewsAvailabilityEvidence.ProviderTimestampOnly,
            "alpaca",
            "article",
            "revision",
            "Headline",
            null,
            "https://example.com/article",
            ["MSFT"],
            null,
            Utc(2026, 7, 24, 12, 0),
            Utc(2026, 7, 24, 12, 0),
            Utc(2026, 7, 24, 12, 0),
            Utc(2026, 7, 24, 12, 0, 1),
            [Source("observation", SourceHashA)]);
        var newsBytes = await codec.WriteNewsRevisionsAsync([news], NormalizerVersion);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            codec.ReadResearchAdjustedMarketBarsAsync(newsBytes));
    }

    [Fact]
    public async Task Publisher_PublishesVerifiedArtifactAndBuildsCatalogNativeManifest()
    {
        var codec = new EvidenceParquetCodec();
        var store = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(artifactRoot));
        var publisher = new EvidenceParquetPartitionPublisher(codec, store);
        var rawArtifact = new EvidenceArtifactReference(
            new EvidenceContentAddress(SourceHashA, 100, "application/json"),
            new EvidenceObjectNamespace("raw/alpaca"));
        var sourceReference = new EvidenceSourceReference(
            "observation-a",
            rawArtifact,
            Utc(2026, 7, 24, 14, 31));
        var row = Bar(
            "security-msft",
            "MSFT",
            Utc(2026, 7, 24, 14, 30),
            Source("observation-a", SourceHashA));
        var request = new EvidenceParquetPartitionRequest(
            "msft-2026-07-24-1m",
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            1,
            new EvidencePartitionProvenance(
                "alpaca",
                "/v2/stocks/bars",
                "sip",
                "all",
                "USD",
                new DateOnly(2026, 7, 24),
                ["security-msft"],
                ["issuer-microsoft"],
                ["MSFT"],
                "1m",
                Utc(2026, 7, 24, 14, 30),
                Utc(2026, 7, 24, 14, 31)),
            [sourceReference],
            RunId,
            ConfigHash,
            "normalizer-v1",
            CodeVersion,
            new EvidenceQualityReport(),
            new EvidenceObjectNamespace("normalized/market-bars"),
            Dimensions: new Dictionary<string, string>
            {
                ["date"] = "2026-07-24",
                ["symbol"] = "MSFT",
                ["timeframe"] = "1m"
            });

        var partition = await publisher.PublishResearchAdjustedMarketBarsAsync(request, [row]);
        var manifest = EvidenceParquetPartitionPublisher.BuildDatasetManifest(
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            1,
            Utc(2026, 7, 24, 15, 0),
            "evidence-job",
            Hash("plan"),
            ConfigHash,
            CodeVersion,
            "normalizer-v1",
            "sip",
            [partition],
            new EvidenceQualityReport());

        Assert.Equal(EvidenceParquetCodec.MediaType, partition.Artifact.Content.MediaType);
        Assert.True((await store.VerifyAsync(partition.Artifact)).IsValid);
        Assert.Equal(row.SourceTimestampUtc, partition.MinimumSourceTimestampUtc);
        Assert.Equal(row.SourceTimestampUtc, partition.MaximumSourceTimestampUtc);
        Assert.Single(partition.SourceObservations);
        Assert.Equal(partition.PartitionId, Assert.Single(manifest.Partitions).PartitionId);
        Assert.False(String.IsNullOrWhiteSpace(manifest.DatasetId));
    }

    [Fact]
    public async Task Publisher_PublishesAsTradedBarsWithRawAdjustmentKind()
    {
        var codec = new EvidenceParquetCodec();
        var store = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(artifactRoot));
        var publisher = new EvidenceParquetPartitionPublisher(codec, store);
        var rawArtifact = new EvidenceArtifactReference(
            new EvidenceContentAddress(SourceHashA, 100, "application/json"),
            new EvidenceObjectNamespace("raw/alpaca"));
        var sourceReference = new EvidenceSourceReference(
            "observation-a",
            rawArtifact,
            Utc(2026, 7, 24, 14, 31));
        var row = Bar(
            "security-msft",
            "MSFT",
            Utc(2026, 7, 24, 14, 30),
            Source("observation-a", SourceHashA),
            adjustment: "raw");
        var request = new EvidenceParquetPartitionRequest(
            "msft-2026-07-24-1m-as-traded",
            EvidenceDatasetKind.MarketBarsAsTraded,
            1,
            new EvidencePartitionProvenance(
                "alpaca",
                "/v2/stocks/bars",
                "sip",
                "raw",
                "USD",
                new DateOnly(2026, 7, 24),
                ["security-msft"],
                ["issuer-microsoft"],
                ["MSFT"],
                "1m",
                Utc(2026, 7, 24, 14, 30),
                Utc(2026, 7, 24, 14, 31)),
            [sourceReference],
            RunId,
            ConfigHash,
            "normalizer-v1",
            CodeVersion,
            new EvidenceQualityReport(),
            new EvidenceObjectNamespace("normalized/market-bars-as-traded"));

        var partition = await publisher.PublishMarketBarsAsTradedAsync(request, [row]);

        Assert.Equal(EvidenceDatasetKind.MarketBarsAsTraded, partition.DatasetKind);
        Assert.Equal("raw", partition.Provenance.Adjustment);
        Assert.True((await store.VerifyAsync(partition.Artifact)).IsValid);
        await using var stream = await store.OpenReadAsync(partition.Artifact);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        var decoded = await codec.ReadMarketBarsAsTradedAsync(copy.ToArray());
        Assert.Equal("raw", Assert.Single(decoded.Rows).Adjustment);
    }

    [Fact]
    public async Task Publisher_RejectsLineageHashNotPresentInSourceReferences()
    {
        var codec = new EvidenceParquetCodec();
        var store = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(artifactRoot));
        var publisher = new EvidenceParquetPartitionPublisher(codec, store);
        var row = Bar(
            "security-msft",
            "MSFT",
            Utc(2026, 7, 24, 14, 30),
            Source("observation-a", SourceHashB));
        var request = new EvidenceParquetPartitionRequest(
            "partition",
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            1,
            new EvidencePartitionProvenance(
                "alpaca",
                "/bars",
                "sip",
                "all",
                "USD",
                new DateOnly(2026, 7, 24),
                ["security-msft"],
                ["issuer-microsoft"],
                ["MSFT"],
                "1m",
                Utc(2026, 7, 24, 14, 30),
                Utc(2026, 7, 24, 14, 31)),
            [
                new EvidenceSourceReference(
                    "observation-a",
                    new EvidenceArtifactReference(
                        new EvidenceContentAddress(SourceHashA, 10, "application/json"),
                        new EvidenceObjectNamespace("raw/alpaca")),
                    Utc(2026, 7, 24, 14, 31))
            ],
            RunId,
            ConfigHash,
            "normalizer",
            CodeVersion,
            new EvidenceQualityReport(),
            new EvidenceObjectNamespace("normalized/market-bars"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            publisher.PublishResearchAdjustedMarketBarsAsync(request, [row]));
    }

    [Fact]
    public void RowContracts_RejectInvalidPointInTimeAndPriceData()
    {
        Assert.Throws<ArgumentException>(() => new EvidenceRowSourceAddress(
            "observation",
            "not-a-hash"));
        Assert.Throws<ArgumentException>(() => new MarketBarEvidenceRow(
            1,
            RunId,
            "not-a-config-hash",
            CodeVersion,
            "sip",
            "security",
            "MSFT",
            Utc(2026, 7, 24, 14, 30),
            Utc(2026, 7, 24, 14, 31),
            "1m",
            100,
            100,
            100,
            100,
            null,
            100,
            1,
            "all",
            "USD",
            Utc(2026, 7, 24, 14, 30),
            Utc(2026, 7, 24, 14, 31),
            [Source("observation", SourceHashA)]));

        Assert.Throws<ArgumentException>(() => new MarketBarEvidenceRow(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "sip",
            "security",
            "MSFT",
            Utc(2026, 7, 24, 14, 30),
            Utc(2026, 7, 24, 14, 31),
            "1m",
            100,
            90,
            80,
            95,
            null,
            100,
            1,
            "all",
            "USD",
            Utc(2026, 7, 24, 14, 30),
            Utc(2026, 7, 24, 14, 31),
            [Source("observation", SourceHashA)]));

        Assert.Throws<ArgumentException>(() => new UniverseMembershipEvidenceRow(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "finviz_elite",
            "finviz",
            "universe",
            "snapshot",
            "v=111",
            SourceHashA,
            new DateOnly(2026, 7, 24),
            "security",
            "issuer",
            "MSFT",
            Utc(2026, 7, 24, 10, 0),
            true,
            1,
            "selected",
            Utc(2026, 7, 24, 10, 1),
            Utc(2026, 7, 24, 10, 2),
            [Source("observation", SourceHashA)]));
    }

    [Fact]
    public void UniverseMembership_EnforcesRankSemanticsAndExactSnapshotLineage()
    {
        Assert.Throws<ArgumentException>(() => UniverseMembership(
            included: true,
            rank: null,
            snapshotHash: SourceHashA,
            sourceHash: SourceHashA));
        Assert.Throws<ArgumentOutOfRangeException>(() => UniverseMembership(
            included: true,
            rank: 0,
            snapshotHash: SourceHashA,
            sourceHash: SourceHashA));
        Assert.Throws<ArgumentException>(() => UniverseMembership(
            included: false,
            rank: 1,
            snapshotHash: SourceHashA,
            sourceHash: SourceHashA));
        Assert.Throws<ArgumentException>(() => UniverseMembership(
            included: false,
            rank: null,
            snapshotHash: SourceHashB,
            sourceHash: SourceHashA));

        var excluded = UniverseMembership(
            included: false,
            rank: null,
            snapshotHash: SourceHashA,
            sourceHash: SourceHashA);
        Assert.False(excluded.Included);
        Assert.Null(excluded.Rank);
    }

    [Fact]
    public void NewsAvailabilityEvidence_IsClosedAndDoesNotInferReceiptAvailability()
    {
        var published = Utc(2026, 7, 24, 12, 0);
        var updated = published.AddMinutes(5);
        var received = updated.AddDays(10);
        var historicalRest = News(
            NewsAvailabilityEvidence.ProviderTimestampOnly,
            published,
            updated,
            received);
        var observedStream = News(
            NewsAvailabilityEvidence.ObservedReceiptTime,
            published,
            updated,
            received);

        Assert.Equal(published, historicalRest.AvailabilityTimestampUtc);
        Assert.NotEqual(received, historicalRest.AvailabilityTimestampUtc);
        Assert.Equal(received, observedStream.AvailabilityTimestampUtc);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            News((NewsAvailabilityEvidence)999, published, updated, received));
    }

    [Fact]
    public async Task SentimentAssessments_AreDeterministicAndExactAfterRoundTrip()
    {
        var codec = new EvidenceParquetCodec();
        var firstNews = News(
            NewsAvailabilityEvidence.ObservedReceiptTime,
            Utc(2026, 7, 24, 12, 0),
            Utc(2026, 7, 24, 12, 0),
            Utc(2026, 7, 24, 12, 0, 1));
        var secondNews = new NewsRevisionEvidenceRow(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "benzinga",
            NewsAvailabilityEvidence.ObservedReceiptTime,
            "alpaca",
            "article",
            "revision-2",
            "Updated headline",
            "A precise revised summary.",
            "https://example.com/article",
            ["MSFT"],
            ["guidance"],
            Utc(2026, 7, 24, 12, 0),
            Utc(2026, 7, 24, 12, 0),
            Utc(2026, 7, 24, 12, 1),
            Utc(2026, 7, 24, 12, 1, 1),
            [Source("news-observation", SourceHashA)]);
        var first = Assessment(firstNews, "assessment-1", 0.625001m);
        var second = Assessment(secondNews, "assessment-2", -0.125m);

        var bytesA = await codec.WriteSentimentAssessmentsAsync(
            [second, first],
            NormalizerVersion);
        var bytesB = await codec.WriteSentimentAssessmentsAsync(
            [first, second],
            NormalizerVersion);
        var decoded = await codec.ReadSentimentAssessmentsAsync(bytesA);

        Assert.Equal(bytesA, bytesB);
        Assert.Equal(EvidenceDatasetKind.SentimentAssessments, decoded.DatasetKind);
        Assert.Equal(
            [EvidenceCanonicalJson.ComputeSha256(first), EvidenceCanonicalJson.ComputeSha256(second)],
            decoded.Rows.Select(EvidenceCanonicalJson.ComputeSha256));
        Assert.Equal(0.625001m, decoded.Rows[0].Score);
        Assert.Equal(
            SentimentAssessmentEvidenceRow.CreateNewsRevisionInputContent(firstNews),
            decoded.Rows[0].InputContent);
        Assert.Equal(
            first.InputContentSha256,
            decoded.Rows[0].InputContentSha256);
    }

    [Fact]
    public void SentimentAssessment_RejectsTamperedInputHashAndInvalidScore()
    {
        var news = News(
            NewsAvailabilityEvidence.ObservedReceiptTime,
            Utc(2026, 7, 24, 12, 0),
            Utc(2026, 7, 24, 12, 0),
            Utc(2026, 7, 24, 12, 0, 1));
        var content =
            SentimentAssessmentEvidenceRow.CreateNewsRevisionInputContent(news);

        Assert.Throws<ArgumentException>(() => new SentimentAssessmentEvidenceRow(
            SentimentAssessmentEvidenceRow.ContractSchemaVersion,
            RunId,
            ConfigHash,
            CodeVersion,
            "finbert",
            "assessment",
            news.Provider,
            news.ProviderArticleId,
            news.RevisionId,
            content,
            SourceHashB,
            "local-finbert",
            "ProsusAI/finbert",
            "model-v1",
            "hf:ProsusAI/finbert@model-v1",
            Hash("finbert-model-artifact"),
            news.AvailabilityTimestampUtc.AddDays(-30),
            "stage-v1",
            0.5m,
            news.AvailabilityTimestampUtc,
            news.AvailabilityTimestampUtc,
            news.Sources));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Assessment(news, "assessment", 1.000001m));
        Assert.Throws<ArgumentException>(() =>
            Assessment(news, "assessment", 0.1234567m));
    }

    [Fact]
    public void SentimentAssessment_RejectsMissingModelHashAndCutoff()
    {
        var news = News(
            NewsAvailabilityEvidence.ObservedReceiptTime,
            Utc(2026, 7, 24, 12, 0),
            Utc(2026, 7, 24, 12, 0),
            Utc(2026, 7, 24, 12, 0, 1));

        Assert.Throws<ArgumentException>(() =>
            Assessment(
                news,
                "assessment-missing-hash",
                0.5m,
                modelArtifactSha256: String.Empty));
        Assert.Throws<ArgumentException>(() =>
            Assessment(
                news,
                "assessment-missing-cutoff",
                0.5m,
                modelTrainingDataCutoffUtc: default(DateTimeOffset)));
    }

    public void Dispose()
    {
        if (Directory.Exists(artifactRoot))
        {
            Directory.Delete(artifactRoot, recursive: true);
        }
    }

    private static MarketBarEvidenceRow Bar(
        string securityId,
        string symbol,
        DateTimeOffset start,
        EvidenceRowSourceAddress source,
        decimal close = 514.00m,
        string adjustment = "all") =>
        new(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "sip",
            securityId,
            symbol,
            start,
            start.AddMinutes(1),
            "1m",
            EvidenceFixedDecimal.ToPriceUnits(close - 0.25m),
            EvidenceFixedDecimal.ToPriceUnits(close + 0.50m),
            EvidenceFixedDecimal.ToPriceUnits(close - 0.50m),
            EvidenceFixedDecimal.ToPriceUnits(close),
            EvidenceFixedDecimal.ToPriceUnits(close - 0.05m),
            100_000,
            1_500,
            adjustment,
            "USD",
            start,
            start.AddSeconds(1),
            [source]);

    private static EvidenceRowSourceAddress Source(string id, string hash) => new(id, hash);

    private static UniverseMembershipEvidenceRow UniverseMembership(
        bool included,
        int? rank,
        string snapshotHash,
        string sourceHash) =>
        new(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "finviz_elite",
            "finviz",
            "universe",
            "snapshot",
            "v=111&f=cap_mega",
            snapshotHash,
            new DateOnly(2026, 7, 24),
            "security",
            "issuer",
            "MSFT",
            Utc(2026, 7, 24, 10, 1),
            included,
            rank,
            included ? "selected" : "not_selected",
            Utc(2026, 7, 24, 10, 0),
            Utc(2026, 7, 24, 10, 0, 1),
            [Source("observation", sourceHash)]);

    private static SecurityMasterEvidenceRow SecurityMaster() =>
        new(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "alpaca_assets",
            "alpaca",
            "security",
            "issuer",
            "MSFT",
            "NASDAQ",
            "us_equity",
            "USD",
            "active",
            Utc(2026, 7, 24),
            null,
            Utc(2026, 7, 24),
            Utc(2026, 7, 24, 0, 0, 1),
            [Source("security-observation", SourceHashA)]);

    private static NewsRevisionEvidenceRow News(
        NewsAvailabilityEvidence availabilityEvidence,
        DateTimeOffset published,
        DateTimeOffset updated,
        DateTimeOffset received) =>
        new(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "benzinga",
            availabilityEvidence,
            "alpaca",
            "article",
            "revision",
            "Headline",
            null,
            "https://example.com/article",
            ["MSFT"],
            null,
            published,
            published,
            updated,
            received,
            [Source("news-observation", SourceHashA)]);

    private static SentimentAssessmentEvidenceRow Assessment(
        NewsRevisionEvidenceRow news,
        string assessmentId,
        decimal score,
        string? modelArtifactSha256 = null,
        DateTimeOffset? modelTrainingDataCutoffUtc = null)
    {
        var input =
            SentimentAssessmentEvidenceRow.CreateNewsRevisionInputContent(news);
        return new SentimentAssessmentEvidenceRow(
            SentimentAssessmentEvidenceRow.ContractSchemaVersion,
            RunId,
            ConfigHash,
            CodeVersion,
            "finbert",
            assessmentId,
            news.Provider,
            news.ProviderArticleId,
            news.RevisionId,
            input,
            SentimentAssessmentEvidenceRow.ComputeInputContentSha256(input),
            "local-finbert",
            "ProsusAI/finbert",
            "model-v1",
            "hf:ProsusAI/finbert@model-v1",
            modelArtifactSha256 ?? Hash("finbert-model-artifact"),
            modelTrainingDataCutoffUtc ??
                news.AvailabilityTimestampUtc.AddDays(-30),
            "stage-v1",
            score,
            news.AvailabilityTimestampUtc,
            news.AvailabilityTimestampUtc,
            news.Sources);
    }

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

    private static string Hash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static async Task<IReadOnlyList<string>> ColumnsAsync(byte[] content)
    {
        await using var stream = new MemoryStream(content, writable: false);
        await using var reader = await ParquetReader.CreateAsync(stream);
        return reader.Schema.GetDataFields().Select(field => field.Name).ToArray();
    }
}

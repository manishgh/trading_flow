using System.Security.Cryptography;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class EvidenceIdentityParquetTests
{
    private const string RunId = "identity-parquet-run";
    private const string ConfigHash =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string CodeVersion = "identity-code-v1";
    private const string NormalizerVersion = "identity-normalizer-v1";
    private const string SourceHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task SecurityMasterSnapshots_AreDeterministicAndPreserveNullableReadiness()
    {
        var codec = new EvidenceParquetCodec();
        var later = SecuritySnapshot(
            "security-b",
            "BBB",
            Utc(2026, 7, 25, 10, 1),
            issuerId: "issuer-b",
            ambiguous: false,
            maintenanceMargin: 0.301234m);
        var earlier = SecuritySnapshot(
            "security-a",
            "AAA",
            Utc(2026, 7, 25, 10, 0),
            issuerId: null,
            ambiguous: true,
            maintenanceMargin: null);

        var first = await codec.WriteSecurityMasterSnapshotsAsync(
            [later, earlier],
            NormalizerVersion);
        var second = await codec.WriteSecurityMasterSnapshotsAsync(
            [earlier, later],
            NormalizerVersion);
        var decoded = await codec.ReadSecurityMasterSnapshotsAsync(first);

        Assert.Equal(first, second);
        Assert.Equal(EvidenceDatasetKind.SecurityMaster, decoded.DatasetKind);
        Assert.Equal(["AAA", "BBB"], decoded.Rows.Select(row => row.Symbol));
        Assert.Equal(
            [EvidenceCanonicalJson.ComputeSha256(earlier), EvidenceCanonicalJson.ComputeSha256(later)],
            decoded.Rows.Select(EvidenceCanonicalJson.ComputeSha256));
        Assert.Null(decoded.Rows[0].IssuerId);
        Assert.Null(decoded.Rows[0].MaintenanceMarginRequirement);
        Assert.Equal(
            [
                IdentityEvidenceReadinessFailure.IssuerIdentityUnavailable,
                IdentityEvidenceReadinessFailure.SymbolAmbiguousInSnapshot
            ],
            decoded.Rows[0].ReadinessFailures);
        Assert.Equal(0.301234m, decoded.Rows[1].MaintenanceMarginRequirement);
        Assert.Equal("observation-security-a", decoded.Rows[0].Sources.Single().ObservationId);
    }

    [Fact]
    public async Task SymbolIntervals_AreDeterministicAndCannotBeReadAsSecurityMaster()
    {
        var codec = new EvidenceParquetCodec();
        var later = SymbolInterval(
            "security-b",
            "BBB",
            Utc(2026, 7, 25, 10, 1),
            issuerId: "issuer-b",
            ambiguous: false);
        var earlier = SymbolInterval(
            "security-a",
            "AAA",
            Utc(2026, 7, 25, 10, 0),
            issuerId: null,
            ambiguous: true);

        var first = await codec.WriteSymbolIntervalsAsync(
            [later, earlier],
            NormalizerVersion);
        var second = await codec.WriteSymbolIntervalsAsync(
            [earlier, later],
            NormalizerVersion);
        var decoded = await codec.ReadSymbolIntervalsAsync(first);

        Assert.Equal(first, second);
        Assert.Equal(EvidenceDatasetKind.SymbolIntervals, decoded.DatasetKind);
        Assert.Equal(["AAA", "BBB"], decoded.Rows.Select(row => row.Symbol));
        Assert.Null(decoded.Rows[0].IssuerId);
        Assert.False(decoded.Rows[0].IsPointInTimeReady);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            codec.ReadSecurityMasterSnapshotsAsync(first));
    }

    [Fact]
    public async Task CorporateActions_PreserveNullableIdentityProviderFieldsAndExactDates()
    {
        var codec = new EvidenceParquetCodec();
        var later = CorporateAction(
            "action-b",
            CorporateActionEvidenceType.CashDividend,
            new DateOnly(2026, 7, 26),
            securityId: "security-b",
            issuerId: "issuer-b",
            primarySymbol: "BBB",
            primaryCusip: null,
            providerFields: new Dictionary<string, string>
            {
                ["rate"] = "0.125000",
                ["currency"] = "USD"
            },
            receivedAtUtc: Utc(2026, 7, 25, 10, 1));
        var earlier = CorporateAction(
            "action-a",
            CorporateActionEvidenceType.ReverseSplit,
            new DateOnly(2026, 7, 25),
            securityId: null,
            issuerId: null,
            primarySymbol: null,
            primaryCusip: "123456789",
            providerFields: new Dictionary<string, string>
            {
                ["old_rate"] = "10",
                ["new_rate"] = "1"
            },
            receivedAtUtc: Utc(2026, 7, 25, 10, 0));

        var first = await codec.WriteCorporateActionsAsync(
            [later, earlier],
            NormalizerVersion);
        var second = await codec.WriteCorporateActionsAsync(
            [earlier, later],
            NormalizerVersion);
        var decoded = await codec.ReadCorporateActionsAsync(first);

        Assert.Equal(first, second);
        Assert.Equal(EvidenceDatasetKind.CorporateActions, decoded.DatasetKind);
        Assert.Equal(["action-a", "action-b"], decoded.Rows.Select(row => row.ProviderActionId));
        Assert.Equal(
            [EvidenceCanonicalJson.ComputeSha256(earlier), EvidenceCanonicalJson.ComputeSha256(later)],
            decoded.Rows.Select(EvidenceCanonicalJson.ComputeSha256));
        Assert.Null(decoded.Rows[0].SecurityId);
        Assert.Null(decoded.Rows[0].IssuerId);
        Assert.Null(decoded.Rows[0].PrimarySymbol);
        Assert.Equal("123456789", decoded.Rows[0].PrimaryCusip);
        Assert.Equal(
            [
                IdentityEvidenceReadinessFailure.SecurityIdentityUnresolved,
                IdentityEvidenceReadinessFailure.IssuerIdentityUnavailable
            ],
            decoded.Rows[0].ReadinessFailures);
        Assert.Equal(
            ["new_rate", "old_rate"],
            decoded.Rows[0].ProviderFields.Keys);
        Assert.Equal(new DateOnly(2026, 7, 26), decoded.Rows[1].ProcessDate);
        Assert.Equal(new DateOnly(2026, 7, 29), decoded.Rows[1].PayableDate);
    }

    [Fact]
    public async Task PublisherAndReader_VerifyArtifactKindLineageAndCanonicalOrder()
    {
        var codec = new EvidenceParquetCodec();
        var store = new IdentityArtifactStore();
        var publisher = new EvidenceParquetPartitionPublisher(codec, store);
        var later = SymbolInterval(
            "security-b",
            "BBB",
            Utc(2026, 7, 25, 10, 1),
            "issuer-b",
            false);
        var earlier = SymbolInterval(
            "security-a",
            "AAA",
            Utc(2026, 7, 25, 10, 0),
            null,
            true);
        var request = PartitionRequest(
            EvidenceDatasetKind.SymbolIntervals,
            ["security-a", "security-b"],
            ["issuer-b"],
            ["AAA", "BBB"],
            Utc(2026, 7, 25, 9),
            Utc(2026, 7, 25, 11));
        var partition = await publisher.PublishSymbolIntervalsAsync(
            request,
            [later, earlier]);
        var manifest = Manifest(
            EvidenceDatasetKind.SymbolIntervals,
            partition);
        var reader = new ParquetEvidencePartitionDataReader(codec, store);

        var rows = await reader.ReadSymbolIntervalsAsync(manifest);

        Assert.Equal(["AAA", "BBB"], rows.Select(row => row.Symbol));
        Assert.True(store.Verified.Count >= 2);
        Assert.Single(store.Opened);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadCorporateActionsAsync(manifest));

        store.Tamper(partition.Artifact);
        var openedBeforeTamper = store.Opened.Count;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            reader.ReadSymbolIntervalsAsync(manifest));
        Assert.Equal(openedBeforeTamper, store.Opened.Count);
    }

    private static SecurityMasterSnapshotEvidenceRow SecuritySnapshot(
        string securityId,
        string symbol,
        DateTimeOffset observedAtUtc,
        string? issuerId,
        bool ambiguous,
        decimal? maintenanceMargin) =>
        new(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "sip",
            "alpaca",
            securityId,
            issuerId,
            symbol,
            $"{symbol} Corporation",
            "NASDAQ",
            "us_equity",
            "USD",
            "active",
            true,
            true,
            true,
            true,
            true,
            null,
            maintenanceMargin,
            0.5m,
            null,
            ["marginable", "active"],
            observedAtUtc,
            ambiguous,
            [Source($"observation-{securityId}")]);

    private static SymbolIntervalEvidenceRow SymbolInterval(
        string securityId,
        string symbol,
        DateTimeOffset observedAtUtc,
        string? issuerId,
        bool ambiguous) =>
        new(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "sip",
            "alpaca",
            securityId,
            issuerId,
            symbol,
            observedAtUtc,
            ambiguous,
            [Source($"observation-{securityId}")]);

    private static CorporateActionEvidenceRow CorporateAction(
        string actionId,
        CorporateActionEvidenceType type,
        DateOnly processDate,
        string? securityId,
        string? issuerId,
        string? primarySymbol,
        string? primaryCusip,
        IReadOnlyDictionary<string, string> providerFields,
        DateTimeOffset receivedAtUtc) =>
        new(
            1,
            RunId,
            ConfigHash,
            CodeVersion,
            "sip",
            "alpaca",
            type,
            actionId,
            securityId,
            issuerId,
            primarySymbol,
            primaryCusip,
            processDate,
            processDate.AddDays(1),
            processDate.AddDays(1),
            processDate.AddDays(2),
            processDate.AddDays(3),
            providerFields,
            receivedAtUtc,
            [Source($"observation-{actionId}")]);

    private static EvidenceParquetPartitionRequest PartitionRequest(
        EvidenceDatasetKind kind,
        IReadOnlyList<string> securityIds,
        IReadOnlyList<string> issuerIds,
        IReadOnlyList<string> symbols,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc) =>
        new(
            "identity-partition",
            kind,
            1,
            new EvidencePartitionProvenance(
                "alpaca",
                "v2/assets",
                "sip",
                "none",
                "USD",
                DateOnly.FromDateTime(startUtc.UtcDateTime),
                securityIds,
                issuerIds,
                symbols,
                "snapshot",
                startUtc,
                endUtc),
            symbols.Select((symbol, index) =>
                SourceReference($"observation-security-{symbol.ToLowerInvariant()[0]}"))
                .ToArray(),
            RunId,
            ConfigHash,
            NormalizerVersion,
            CodeVersion,
            new EvidenceQualityReport(),
            new EvidenceObjectNamespace("normalized/identity"));

    private static EvidenceDatasetManifest Manifest(
        EvidenceDatasetKind kind,
        EvidenceDatasetPartitionManifest partition) =>
        EvidenceParquetPartitionPublisher.BuildDatasetManifest(
            kind,
            1,
            Utc(2026, 7, 25, 11),
            "identity-job",
            Hash("identity-plan"),
            ConfigHash,
            CodeVersion,
            NormalizerVersion,
            "sip",
            [partition],
            new EvidenceQualityReport());

    private static EvidenceRowSourceAddress Source(string observationId) =>
        new(observationId, SourceHash);

    private static EvidenceSourceReference SourceReference(string observationId)
        => new(
            observationId,
            new EvidenceArtifactReference(
                new EvidenceContentAddress(
                    SourceHash,
                    1,
                    "application/json"),
                new EvidenceObjectNamespace("raw/identity")),
            Utc(2026, 7, 25, 10));

    private static DateTimeOffset Utc(
        int year,
        int month,
        int day,
        int hour,
        int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed class IdentityArtifactStore : IImmutableArtifactStore
    {
        private readonly Dictionary<string, byte[]> content = new(StringComparer.Ordinal);

        public List<EvidenceArtifactReference> Verified { get; } = [];
        public List<EvidenceArtifactReference> Opened { get; } = [];

        public Task<ImmutableArtifactReceipt> PutIfAbsentAsync(
            ImmutableArtifactWriteRequest request,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default)
        {
            var bytes = value.ToArray();
            var existed = content.ContainsKey(request.Artifact.Content.Sha256);
            content.TryAdd(request.Artifact.Content.Sha256, bytes);
            return Task.FromResult(new ImmutableArtifactReceipt(
                request.Artifact,
                request.Artifact.Content.Sha256,
                existed));
        }

        public Task<ImmutableArtifactVerification> VerifyAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default)
        {
            Verified.Add(artifact);
            var valid = content.TryGetValue(artifact.Content.Sha256, out var bytes) &&
                        bytes.LongLength == artifact.Content.ByteLength &&
                        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
                            .Equals(artifact.Content.Sha256, StringComparison.Ordinal);
            return Task.FromResult(new ImmutableArtifactVerification(
                valid,
                artifact,
                bytes?.LongLength,
                bytes is null
                    ? null
                    : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                valid ? null : "content mismatch"));
        }

        public Task<Stream> OpenReadAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default)
        {
            Opened.Add(artifact);
            return Task.FromResult<Stream>(
                new MemoryStream(content[artifact.Content.Sha256], writable: false));
        }

        public void Tamper(EvidenceArtifactReference artifact) =>
            content[artifact.Content.Sha256][0] ^= 0x01;
    }
}

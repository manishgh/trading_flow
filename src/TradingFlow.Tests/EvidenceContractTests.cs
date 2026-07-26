using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;
using TradingFlow.Research.Momentum;

namespace TradingFlow.Tests;

public sealed class EvidenceContractTests
{
    private static readonly DateTimeOffset SourceTime =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ContentAddress_NormalizesAndValidatesSha256()
    {
        var address = new EvidenceContentAddress(new string('A', 64), 42, "application/json");

        Assert.Equal(new string('a', 64), address.Sha256);
        Assert.Throws<ArgumentException>(() =>
            new EvidenceContentAddress("not-a-hash", 42, "application/json"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvidenceContentAddress(new string('a', 64), -1, "application/json"));
    }

    [Fact]
    public void RowLineage_IsCanonicalAndRejectsConflictingObservationContent()
    {
        var first = Source("page-b", 'b');
        var second = Source("page-a", 'a');
        var lineage = new EvidenceRowLineage([first, second, second]);
        var reordered = new EvidenceRowLineage([second, first]);

        Assert.Equal(["page-a", "page-b"], lineage.Sources.Select(source => source.ObservationId));
        Assert.Equal(lineage.LineageHash, reordered.LineageHash);
        Assert.Throws<ArgumentException>(() =>
            new EvidenceRowLineage([second, Source("page-a", 'c')]));
    }

    [Fact]
    public void DatasetManifest_CopiesAndCanonicalizesMutableInputs()
    {
        var partitions = new List<EvidenceDatasetPartitionManifest>
        {
            Partition("part-b", 'b'),
            Partition("part-a", 'a')
        };
        var attributes = new Dictionary<string, string> { ["z"] = "last", ["a"] = "first" };
        var manifest = new EvidenceDatasetManifest(
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            1,
            SourceTime,
            "run-1",
            Hash('f'),
            Hash('c'),
            "commit-1",
            "normalizer-1",
            "sip",
            partitions,
            new EvidenceQualityReport(),
            attributes);

        partitions.Clear();
        attributes.Clear();

        Assert.Equal(["part-a", "part-b"], manifest.Partitions.Select(partition => partition.PartitionId));
        Assert.Equal(["a", "z"], manifest.Attributes.Keys);
        Assert.True(manifest.Quality.Passed);
    }

    [Fact]
    public void DatasetManifest_RejectsMixedKindsAndDuplicatePartitions()
    {
        var valid = Partition("part-a", 'a');
        var wrongKind = new EvidenceDatasetPartitionManifest(
            "part-b",
            EvidenceDatasetKind.SipQuotes,
            1,
            Provenance("part-b", "sip"),
            SourceTime,
            SourceTime.AddMinutes(1),
            1,
            Artifact('b', "partitions"),
            [Source("page-b", 'b')],
            "normalizer-1",
            "commit-1",
            new EvidenceQualityReport());

        Assert.Throws<ArgumentException>(() => Dataset([valid, wrongKind]));
        Assert.Throws<ArgumentException>(() => Dataset([valid, valid]));
    }

    [Fact]
    public void Contracts_RequireUtcTimestamps()
    {
        var localTime = SourceTime.ToOffset(TimeSpan.FromHours(2));

        Assert.Throws<ArgumentException>(() =>
            new EvidenceSourceReference(
                "page-a",
                Artifact('a', "raw"),
                localTime));
        Assert.Throws<ArgumentException>(() =>
            new EvidenceCollectionCheckpoint("job-1", EvidenceCollectionState.Planned, localTime));
    }

    [Fact]
    public void SourceObservation_PreservesMissingAndClockSkewedProviderTimestamps()
    {
        var content = new EvidenceContentAddress(Hash('a'), 1, "application/json");
        var missing = new EvidenceSourceObservation(
            "observation-missing",
            Job('e'),
            Hash('f'),
            Hash('e'),
            "request-1",
            "page-1",
            "alpaca",
            "/v2/stocks/bars",
            EvidenceTransportKind.Http,
            200,
            ["AAPL"],
            SourceTime.AddDays(-1),
            SourceTime,
            "sip",
            "all",
            "USD",
            new DateOnly(2026, 7, 25),
            null,
            null,
            null,
            SourceTime,
            new EvidenceArtifactReference(content, new EvidenceObjectNamespace("raw")),
            "run-1",
            Hash('b'),
            "commit-1");
        var clockSkewed = new EvidenceSourceObservation(
            "observation-skewed",
            Job('e'),
            Hash('f'),
            Hash('e'),
            "request-2",
            "page-1",
            "alpaca",
            "/v2/stocks/bars",
            EvidenceTransportKind.Http,
            200,
            ["AAPL"],
            SourceTime.AddDays(-1),
            SourceTime,
            "sip",
            "all",
            "USD",
            new DateOnly(2026, 7, 25),
            null,
            SourceTime.AddSeconds(5),
            null,
            SourceTime,
            new EvidenceArtifactReference(content, new EvidenceObjectNamespace("raw")),
            "run-1",
            Hash('b'),
            "commit-1");

        Assert.Null(missing.ProviderCreatedAtUtc);
        Assert.True(clockSkewed.ProviderCreatedAtUtc > clockSkewed.ReceivedAtUtc);
    }

    [Fact]
    public void SourceObservation_RequiresPlanDerivedJobAndHttpStatus()
    {
        Assert.Throws<ArgumentException>(() => Observation("wrong-job", 200));
        Assert.Throws<ArgumentException>(() => Observation(Job('e'), null));
    }

    [Fact]
    public void RowProvenance_PreservesNegativeObservedLatency()
    {
        var provenance = new EvidenceRowProvenance(
            1,
            "run-1",
            Hash('a'),
            "commit-1",
            "sip",
            SourceTime.AddSeconds(5),
            SourceTime,
            new EvidenceRowLineage([Source("page-a", 'a')]));

        Assert.True(provenance.ReceivedAtUtc < provenance.SourceTimestampUtc);
    }

    [Theory]
    [InlineData(EvidenceCollectionState.Planned, EvidenceCollectionState.Collecting)]
    [InlineData(EvidenceCollectionState.Collecting, EvidenceCollectionState.Normalizing)]
    [InlineData(EvidenceCollectionState.Normalizing, EvidenceCollectionState.Validating)]
    [InlineData(EvidenceCollectionState.Validating, EvidenceCollectionState.Committed)]
    [InlineData(EvidenceCollectionState.Incomplete, EvidenceCollectionState.Collecting)]
    public void CollectionTransitions_AllowOnlyDocumentedProgression(
        EvidenceCollectionState current,
        EvidenceCollectionState next)
    {
        Assert.True(EvidenceCollectionTransitions.CanTransition(current, next));
        EvidenceCollectionTransitions.EnsureAllowed(current, next);
    }

    [Theory]
    [InlineData(EvidenceCollectionState.Planned, EvidenceCollectionState.Committed)]
    [InlineData(EvidenceCollectionState.Committed, EvidenceCollectionState.Collecting)]
    [InlineData(EvidenceCollectionState.Cancelled, EvidenceCollectionState.Collecting)]
    [InlineData(EvidenceCollectionState.Quarantined, EvidenceCollectionState.Validating)]
    public void CollectionTransitions_RejectIllegalProgression(
        EvidenceCollectionState current,
        EvidenceCollectionState next)
    {
        Assert.False(EvidenceCollectionTransitions.CanTransition(current, next));
        Assert.Throws<InvalidOperationException>(() =>
            EvidenceCollectionTransitions.EnsureAllowed(current, next));
    }

    [Fact]
    public void CollectionCheckpoint_RequiresReasonForFailedState()
    {
        Assert.Throws<ArgumentException>(() =>
            new EvidenceCollectionCheckpoint(
                "job-1",
                EvidenceCollectionState.Incomplete,
                SourceTime));
    }

    [Fact]
    public void Contracts_RejectUndefinedEnumValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvidenceQualityIssue(
                "bad",
                (EvidenceQualitySeverity)999,
                "Undefined severity."));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvidenceCollectionCheckpoint(
                "job-1",
                (EvidenceCollectionState)999,
                SourceTime));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvidencePinSubject(
                (EvidencePinSubjectKind)999,
                "subject-1"));
    }

    [Fact]
    public void HoldoutIdentity_IsCanonicalAcrossDatasetOrdering()
    {
        var first = new EvidenceDatasetReference("dataset-a", Artifact('a', "manifests"));
        var second = new EvidenceDatasetReference("dataset-b", Artifact('b', "manifests"));
        var identity = Holdout([first, second]);
        var reordered = Holdout([second, first]);

        Assert.Equal(identity.HoldoutId, reordered.HoldoutId);
        Assert.Equal(["dataset-a", "dataset-b"], identity.Datasets.Select(dataset => dataset.DatasetId));
    }

    [Fact]
    public void HoldoutIdentity_IsBoundToUniverseLedgerBytes()
    {
        var dataset = new EvidenceDatasetReference("dataset-a", Artifact('a', "manifests"));
        var first = new EvidenceHoldoutIdentity(
            "momentum",
            SourceTime,
            SourceTime.AddDays(30),
            [dataset],
            "ledger-1",
            Artifact('a', "ledgers"),
            Artifact('d', "partitions"));
        var changedLedger = new EvidenceHoldoutIdentity(
            "momentum",
            SourceTime,
            SourceTime.AddDays(30),
            [dataset],
            "ledger-1",
            Artifact('b', "ledgers"),
            Artifact('d', "partitions"));

        Assert.NotEqual(first.HoldoutId, changedLedger.HoldoutId);
    }

    [Fact]
    public void ResearchManifest_EnforcesReadinessConsistency()
    {
        Assert.Throws<ArgumentException>(() =>
            new EvidenceResearchRunManifest(
                "research-1",
                "momentum",
                SourceTime,
                [new EvidenceDatasetReference("dataset-a", Artifact('a', "manifests"))],
                "ledger-1",
                Artifact('a', "ledgers"),
                Artifact('b', "configs"),
                "commit-1",
                StudyPartitions(),
                Assumptions(),
                holdout: null,
                evidenceReady: true,
                readinessFailures: ["missing_quotes"],
                outputs: []));
    }

    [Fact]
    public void ResearchManifest_RejectsHoldoutDatasetOrUniverseMismatch()
    {
        var input = new EvidenceDatasetReference("dataset-a", Artifact('a', "manifests"));
        var other = new EvidenceDatasetReference("dataset-b", Artifact('b', "manifests"));

        Assert.Throws<ArgumentException>(() =>
            new EvidenceResearchRunManifest(
                "research-1",
                "momentum",
                SourceTime,
                [input],
                "ledger-1",
                Artifact('a', "ledgers"),
                Artifact('b', "configs"),
                "commit-1",
                StudyPartitions(),
                Assumptions(),
                new EvidenceHoldoutIdentity(
                    "momentum",
                    SourceTime.AddDays(60),
                    SourceTime.AddDays(90),
                    [other],
                    "ledger-1",
                    Artifact('a', "ledgers"),
                    Artifact('d', "partitions")),
                evidenceReady: true,
                readinessFailures: [],
                outputs: []));
    }

    [Fact]
    public void RetentionPin_RequiresNonEmptyTransitiveClosure()
    {
        Assert.Throws<ArgumentException>(() =>
            new EvidenceRetentionPin(
                "pin-1",
                new EvidencePinSubject(EvidencePinSubjectKind.ResearchRun, "research-1"),
                "hold",
                SourceTime,
                []));
    }

    [Fact]
    public void PromotionAuthority_IsNotExposedByEvidenceCatalog()
    {
        var evidenceMethods = typeof(IEvidenceCatalog).GetMethods().Select(method => method.Name).ToArray();
        var promotionMethods = typeof(IPromotionRegistry).GetMethods().Select(method => method.Name).ToArray();

        Assert.DoesNotContain(evidenceMethods, name =>
            name.Contains("Promotion", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Decision", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("RegisterDecisionAsync", promotionMethods);
    }

    [Fact]
    public void ResearchAssembly_DoesNotDependOnPromotionAuthority()
    {
        var forbidden = typeof(IPromotionRegistry);
        var violations = typeof(CrossSectionalMomentumResearchAnalyzer).Assembly
            .GetTypes()
            .SelectMany(type =>
                type.GetConstructors().SelectMany(constructor => constructor.GetParameters())
                    .Concat(type.GetMethods().SelectMany(method => method.GetParameters()))
                    .Concat(type.GetProperties().Select(property => property.PropertyType)
                        .Select(propertyType => new SyntheticParameterInfo(propertyType)))
                    .Concat(type.GetFields().Select(field => field.FieldType)
                        .Select(fieldType => new SyntheticParameterInfo(fieldType))))
            .Where(parameter => parameter.ParameterType == forbidden)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void CatalogAndArtifactInterfaces_ExposeRecoveryLifecycle()
    {
        var catalogMethods = typeof(IEvidenceCatalog).GetMethods().Select(method => method.Name).ToHashSet();
        var artifactMethods = typeof(IImmutableArtifactStore).GetMethods().Select(method => method.Name).ToHashSet();
        var scannerMethods = typeof(IImmutableArtifactMaintenanceScanner).GetMethods()
            .Select(method => method.Name)
            .ToHashSet();
        var retentionMethods = typeof(IEvidenceRetentionService).GetMethods()
            .Select(method => method.Name)
            .ToHashSet();

        Assert.Contains("RegisterCollectionPlanAsync", catalogMethods);
        Assert.Contains("SaveCollectionCheckpointAsync", catalogMethods);
        Assert.Contains("RegisterSourceObservationAsync", catalogMethods);
        Assert.Contains("RegisterResearchRunAsync", catalogMethods);
        Assert.Contains("ResolveReferenceClosureAsync", catalogMethods);
        Assert.Contains("SaveRequestCursorCheckpointAsync", catalogMethods);
        Assert.Contains("RegisterQuarantineAsync", catalogMethods);
        Assert.Contains("ResolveQuarantineAsync", catalogMethods);
        Assert.DoesNotContain("EnumerateAsync", artifactMethods);
        Assert.Contains("ScanAsync", scannerMethods);
        Assert.DoesNotContain("DeleteAsync", scannerMethods);
        Assert.Contains("RunAsync", retentionMethods);
        Assert.DoesNotContain("DeleteIfExistsAsync", artifactMethods);
    }

    [Fact]
    public void NamespaceAndCanonicalMetadata_FailClosed()
    {
        Assert.Throws<ArgumentException>(() => new EvidenceObjectNamespace("../raw"));
        Assert.Throws<ArgumentException>(() => new EvidenceObjectNamespace("raw\\..\\secret"));
        Assert.Throws<ArgumentException>(() => new EvidenceObjectNamespace("raw/con"));
        Assert.Throws<ArgumentException>(() => new EvidenceObjectNamespace("nul"));
        Assert.Throws<ArgumentException>(() =>
            new EvidenceCollectionRequest(
                "request-1",
                "alpaca",
                "/v2/stocks/bars",
                ["AAPL"],
                SourceTime,
                SourceTime.AddDays(1),
                "sip",
                "all",
                "USD",
                new DateOnly(2026, 7, 25),
                new Dictionary<string, string>
                {
                    ["feed"] = "sip",
                    [" feed "] = "iex"
                }));
        Assert.Throws<ArgumentException>(() =>
            Observation(
                Job('e'),
                200,
                responseHeaders: new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer should-not-persist"
                }));
        Assert.Throws<ArgumentException>(() =>
            new EvidenceCollectionRequest(
                "request-secret",
                "alpaca",
                "/v2/stocks/bars",
                ["AAPL"],
                SourceTime,
                SourceTime.AddDays(1),
                "sip",
                "all",
                "USD",
                new DateOnly(2026, 7, 25),
                new Dictionary<string, string> { ["api_key"] = "secret" }));
    }

    [Theory]
    [InlineData("https://elite.finviz.com/export.ashx?auth=secret")]
    [InlineData("https://example.test/data?api_key=secret")]
    [InlineData("https://user:password@example.test/data")]
    [InlineData("/export.ashx?v=111&token=secret")]
    public void CollectionRequest_RejectsAuthenticationMaterialInEndpoint(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => new EvidenceCollectionRequest(
            "request-secret-endpoint",
            "finviz",
            endpoint,
            [],
            null,
            null,
            "finviz-elite",
            "raw",
            "USD",
            new DateOnly(2026, 7, 25)));
    }

    [Fact]
    public void CollectionPlanIdentity_IgnoresCreationAndRunTimestamps()
    {
        var request = new EvidenceCollectionRequest(
            "request-1",
            "alpaca",
            "/v2/stocks/bars",
            ["AAPL"],
            SourceTime,
            SourceTime.AddDays(1),
            "sip",
            "all",
            "USD",
            new DateOnly(2026, 7, 25));
        var first = new EvidenceCollectionPlan(
            SourceTime,
            "run-1",
            Hash('a'),
            "commit-1",
            "collection-v1",
            "monthly-v1",
            [request]);
        var retry = new EvidenceCollectionPlan(
            SourceTime.AddMinutes(10),
            "run-2",
            Hash('a'),
            "commit-1",
            "collection-v1",
            "monthly-v1",
            [request]);

        Assert.Equal(first.LogicalPlanHash, retry.LogicalPlanHash);
        Assert.NotEqual(first.CollectionAttemptHash, retry.CollectionAttemptHash);
        Assert.NotEqual(first.JobId, retry.JobId);
    }

    [Fact]
    public void DatasetIdentity_IsDeterministicForEquivalentPublicationAttempts()
    {
        var first = Dataset([Partition("part-a", 'a')], SourceTime);
        var retry = Dataset([Partition("part-a", 'a')], SourceTime.AddMinutes(5));

        Assert.Equal(first.LogicalDatasetKey, retry.LogicalDatasetKey);
        Assert.NotEqual(first.DatasetId, retry.DatasetId);
        Assert.NotEqual(
            EvidenceCanonicalJson.ComputeSha256(first),
            EvidenceCanonicalJson.ComputeSha256(retry));
    }

    [Fact]
    public void RetentionPolicy_EnforcesTwentyFourMonthEvidenceFloor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvidenceRetentionPolicy(23, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvidenceRetentionPolicy(24, 23));

        var policy = new EvidenceRetentionPolicy(24, 24);
        Assert.Equal(24, policy.RawObservationMonths);
        Assert.Equal(24, policy.NormalizedDatasetMonths);
    }

    [Fact]
    public void CursorCheckpoint_RejectsRepeatedTokenAfterRestart()
    {
        var consumedTokenHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes("page-token")))
            .ToLowerInvariant();

        Assert.Throws<ArgumentException>(() =>
            new EvidenceRequestCursorCheckpoint(
                "job-1",
                "request-1",
                2,
                "page-token",
                [consumedTokenHash],
                "observation-1",
                2,
                exhausted: false,
                SourceTime));
    }

    [Fact]
    public void CanonicalJson_SerializesEnumsByStableName()
    {
        var json = System.Text.Encoding.UTF8.GetString(
            EvidenceCanonicalJson.SerializeToUtf8Bytes(
                new EvidenceQualityIssue(
                    "warning",
                    EvidenceQualitySeverity.Warning,
                    "Warning.")));

        Assert.Contains("\"severity\":\"warning\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalJson_RoundTripsPersistedDatasetManifest()
    {
        var original = Dataset([Partition("part-a", 'a')]);
        var bytes = EvidenceCanonicalJson.SerializeToUtf8Bytes(original);
        var restored = EvidenceCanonicalJson.Deserialize<EvidenceDatasetManifest>(bytes);

        Assert.Equal(original.DatasetId, restored.DatasetId);
        Assert.Equal(original.Kind, restored.Kind);
        Assert.Equal(original.Partitions.Single().Provenance.SecurityIds,
            restored.Partitions.Single().Provenance.SecurityIds);
        Assert.Equal(
            EvidenceCanonicalJson.ComputeSha256(original),
            EvidenceCanonicalJson.ComputeSha256(restored));
    }

    [Fact]
    public void DatasetManifest_RejectsFailedPartitionOrMismatchedProvenance()
    {
        var failedPartition = Partition(
            "part-failed",
            'f',
            quality: new EvidenceQualityReport(
                [new EvidenceQualityIssue("bad_rows", EvidenceQualitySeverity.Failure, "Rows failed validation.")]));
        Assert.Throws<ArgumentException>(() => Dataset([failedPartition]));

        var mismatchedFeed = Partition("part-feed", 'e', dataFeed: "iex");
        Assert.Throws<ArgumentException>(() => Dataset([mismatchedFeed]));
    }

    [Fact]
    public void CanonicalSerialization_IsStableForEquivalentManifestInputs()
    {
        var first = Dataset([Partition("part-b", 'b'), Partition("part-a", 'a')]);
        var second = Dataset([Partition("part-a", 'a'), Partition("part-b", 'b')]);

        Assert.Equal(
            EvidenceCanonicalJson.ComputeSha256(first),
            EvidenceCanonicalJson.ComputeSha256(second));
    }

    private static EvidenceDatasetManifest Dataset(
        IReadOnlyList<EvidenceDatasetPartitionManifest> partitions,
        DateTimeOffset? createdAtUtc = null) =>
        new(
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            1,
            createdAtUtc ?? SourceTime,
            "run-1",
            Hash('f'),
            Hash('c'),
            "commit-1",
            "normalizer-1",
            "sip",
            partitions,
            new EvidenceQualityReport());

    private static EvidenceDatasetPartitionManifest Partition(
        string id,
        char hashCharacter,
        string dataFeed = "sip",
        EvidenceQualityReport? quality = null) =>
        new(
            id,
            EvidenceDatasetKind.MarketBarsResearchAdjusted,
            1,
            Provenance(id, dataFeed),
            SourceTime,
            SourceTime.AddMinutes(1),
            1,
            Artifact(hashCharacter, "partitions"),
            [Source($"source-{id}", hashCharacter)],
            "normalizer-1",
            "commit-1",
            quality ?? new EvidenceQualityReport());

    private static EvidencePartitionProvenance Provenance(string id, string dataFeed) =>
        new(
            "alpaca",
            $"/v2/stocks/bars/{id}",
            dataFeed,
            "all",
            "USD",
            new DateOnly(2026, 7, 25),
            ["security-aapl"],
            ["issuer-apple"],
            ["AAPL"],
            "1d",
            SourceTime.AddDays(-1),
            SourceTime.AddDays(1));

    private static EvidenceSourceReference Source(string id, char hashCharacter) =>
        new(
            id,
            Artifact(hashCharacter, "raw"),
            SourceTime.AddSeconds(1));

    private static EvidenceSourceObservation Observation(
        string jobId,
        int? statusCode,
        IReadOnlyDictionary<string, string>? responseHeaders = null) =>
        new(
            "observation-1",
            jobId,
            Hash('f'),
            Hash('e'),
            "request-1",
            "page-1",
            "alpaca",
            "/v2/stocks/bars",
            EvidenceTransportKind.Http,
            statusCode,
            ["AAPL"],
            SourceTime.AddDays(-1),
            SourceTime,
            "sip",
            "all",
            "USD",
            new DateOnly(2026, 7, 25),
            null,
            null,
            null,
            SourceTime,
            Artifact('a', "raw"),
            "run-1",
            Hash('b'),
            "commit-1",
            responseHeaders);

    private static EvidenceHoldoutIdentity Holdout(
        IReadOnlyList<EvidenceDatasetReference> datasets) =>
        new(
            "cross-sectional-momentum",
            SourceTime,
            SourceTime.AddDays(30),
            datasets,
            "ledger-1",
            Artifact('a', "ledgers"),
            Artifact('d', "partitions"));

    private static string Hash(char character) => new(character, 64);

    private static string Job(char planHashCharacter) =>
        $"evidence-{Hash(planHashCharacter)[..24]}";

    private static EvidenceArtifactReference Artifact(char hashCharacter, string objectNamespace) =>
        new(
            new EvidenceContentAddress(Hash(hashCharacter), 1, "application/octet-stream"),
            new EvidenceObjectNamespace(objectNamespace));

    private static EvidenceStudyPartitions StudyPartitions() =>
        new(
            new EvidenceStudyWindow("development", SourceTime, SourceTime.AddDays(30)),
            new EvidenceStudyWindow("validation", SourceTime.AddDays(30), SourceTime.AddDays(60)),
            new EvidenceStudyWindow("holdout", SourceTime.AddDays(60), SourceTime.AddDays(90)));

    private static EvidenceSimulationAssumptions Assumptions() =>
        new(
            Artifact('1', "assumptions"),
            Artifact('2', "assumptions"),
            Artifact('3', "assumptions"),
            Artifact('4', "assumptions"),
            Artifact('5', "assumptions"));

    private sealed class SyntheticParameterInfo(Type parameterType) : System.Reflection.ParameterInfo
    {
        public override Type ParameterType { get; } = parameterType;
    }
}

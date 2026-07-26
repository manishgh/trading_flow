using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;
using TradingFlow.Engine.Storage;
using TradingFlow.Research.Orchestration;
using TradingFlow.Research.Universe;

namespace TradingFlow.Tests;

public sealed class IdentityUniversePublicationCoordinatorTests : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset IdentityObservedAt =
        new(2026, 7, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SnapshotProviderAt =
        new(2026, 7, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SnapshotReceivedAt =
        SnapshotProviderAt.AddSeconds(1);
    private static readonly DateTimeOffset DecisionAt =
        SnapshotReceivedAt.AddMinutes(5);
    private static readonly DateOnly SessionDate = new(2026, 7, 2);

    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-identity-universe-tests",
        Guid.NewGuid().ToString("N"));
    private FileSystemImmutableArtifactStore physicalStore = null!;
    private FaultInjectingArtifactStore store = null!;
    private SqliteEvidenceCatalog catalog = null!;
    private EvidenceParquetPartitionPublisher publisher = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(rootPath);
        physicalStore = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(Path.Combine(rootPath, "objects")));
        store = new FaultInjectingArtifactStore(physicalStore);
        catalog = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(
                Path.Combine(rootPath, "evidence.db"),
                EvidenceCatalogOpenMode.BootstrapNew),
            store);
        publisher = new EvidenceParquetPartitionPublisher(
            new EvidenceParquetCodec(),
            store);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task IdentityPublication_ConcurrentSameRequestCommitsOneAtomicBundle()
    {
        var fixture = await CreateIdentityCollectionAsync("concurrent");
        var coordinator = IdentityCoordinator();
        var request = IdentityRequest(fixture.Plan);

        var results = await Task.WhenAll(
            coordinator.PublishAsync(request),
            coordinator.PublishAsync(request));

        Assert.Equal(results[0].SecurityMasterDatasetId, results[1].SecurityMasterDatasetId);
        Assert.Equal(results[0].SymbolIntervalsDatasetId, results[1].SymbolIntervalsDatasetId);
        Assert.Equal(results[0].CorporateActionsDatasetId, results[1].CorporateActionsDatasetId);
        Assert.Equal(3, await CountIdentityDatasetsAsync(fixture.Plan.JobId));
        Assert.Equal(
            EvidenceCollectionState.Committed,
            (await catalog.GetCollectionCheckpointAsync(fixture.Plan.JobId))!.State);
        Assert.Contains(results, result => !result.AlreadyCommitted);
        Assert.Contains(results, result => result.AlreadyCommitted);
    }

    [Fact]
    public async Task IdentityPublication_ManifestFailureHasZeroVisibilityAndRestartRecovers()
    {
        var fixture = await CreateIdentityCollectionAsync("restart");
        store.FailDatasetManifestWriteNumber = 2;
        var coordinator = IdentityCoordinator();
        var request = IdentityRequest(fixture.Plan);

        await Assert.ThrowsAsync<IOException>(() => coordinator.PublishAsync(request));

        Assert.Equal(0, await CountIdentityDatasetsAsync(fixture.Plan.JobId));
        Assert.Equal(
            EvidenceCollectionState.Validating,
            (await catalog.GetCollectionCheckpointAsync(fixture.Plan.JobId))!.State);

        var recovered = await coordinator.PublishAsync(request);

        Assert.Equal(3, await CountIdentityDatasetsAsync(fixture.Plan.JobId));
        Assert.NotNull(await catalog.GetDatasetAsync(recovered.SecurityMasterDatasetId));
        Assert.NotNull(await catalog.GetDatasetAsync(recovered.SymbolIntervalsDatasetId));
        Assert.NotNull(await catalog.GetDatasetAsync(recovered.CorporateActionsDatasetId));
    }

    [Fact]
    public async Task IdentityPublication_TamperedRawEvidenceCommitsNothing()
    {
        var fixture = await CreateIdentityCollectionAsync("tamper");
        store.InvalidHashes.Add(fixture.AssetObservation.Artifact.Content.Sha256);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IdentityCoordinator().PublishAsync(IdentityRequest(fixture.Plan)));

        Assert.Equal(0, await CountIdentityDatasetsAsync(fixture.Plan.JobId));
    }

    [Fact]
    public async Task UniversePublication_MissingIssuerFailsClosedWithoutMembershipArtifact()
    {
        var identity = await CreateIdentityCollectionAsync("not-ready");
        var identityResult = await IdentityCoordinator().PublishAsync(
            IdentityRequest(identity.Plan));
        var snapshot = await CreateUniverseSnapshotAsync("not-ready");
        var request = UniverseRequest(identityResult, snapshot);
        var objectsBefore = store.Published.Count;

        var result = await UniverseCoordinator().PublishAsync(request);

        Assert.False(result.Published);
        Assert.Null(result.DatasetId);
        Assert.Empty(await catalog.FindDatasetsAsync(
            new EvidenceDatasetQuery(EvidenceDatasetKind.UniverseMembership, "finviz")));
        Assert.Contains(
            result.Readiness.Failures,
            failure =>
                failure.Code ==
                PointInTimeUniverseReadinessFailureCode.IssuerIdentityUnavailable);
        Assert.Equal(objectsBefore, store.Published.Count);
    }

    [Fact]
    public async Task UniversePublication_ReadyInputsAreIdempotentAndConcurrent()
    {
        var identity = await CreateIdentityCollectionAsync("ready");
        var readyIds = await PublishReadyIdentityAsync(identity);
        var snapshot = await CreateUniverseSnapshotAsync("ready");
        var request = UniverseRequest(readyIds, snapshot);
        var coordinator = UniverseCoordinator();

        var results = await Task.WhenAll(
            coordinator.PublishAsync(request),
            coordinator.PublishAsync(request));

        Assert.All(results, result =>
        {
            Assert.True(result.Published);
            Assert.Equal(1, result.MembershipCount);
            Assert.Empty(result.PublicationBlockers);
        });
        Assert.Equal(results[0].DatasetId, results[1].DatasetId);
        Assert.Contains(results, result => !result.AlreadyCommitted);
        Assert.Contains(results, result => result.AlreadyCommitted);
        Assert.Single(await catalog.FindDatasetsAsync(
            new EvidenceDatasetQuery(EvidenceDatasetKind.UniverseMembership, "finviz")));
    }

    [Fact]
    public async Task UniversePublication_TamperedCommittedIdentityPartitionIsRejected()
    {
        var identity = await CreateIdentityCollectionAsync("input-tamper");
        var readyIds = await PublishReadyIdentityAsync(identity);
        var security = await catalog.GetDatasetAsync(readyIds.SecurityMasterDatasetId);
        store.InvalidHashes.Add(security!.Partitions[0].Artifact.Content.Sha256);
        var snapshot = await CreateUniverseSnapshotAsync("input-tamper");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UniverseCoordinator().PublishAsync(UniverseRequest(readyIds, snapshot)));

        Assert.Empty(await catalog.FindDatasetsAsync(
            new EvidenceDatasetQuery(EvidenceDatasetKind.UniverseMembership, "finviz")));
    }

    private IdentityEvidencePublicationCoordinator IdentityCoordinator() =>
        new(
            catalog,
            catalog,
            store,
            publisher,
            timeProvider: new FixedTimeProvider(DecisionAt));

    private UniverseMembershipPublicationCoordinator UniverseCoordinator() =>
        new(
            catalog,
            new ParquetEvidencePartitionDataReader(
                new EvidenceParquetCodec(),
                store),
            store,
            publisher);

    private static IdentityEvidencePublicationRequest IdentityRequest(
        EvidenceCollectionPlan plan) =>
        new(
            plan.JobId,
            1,
            "alpaca-identity-v1",
            new EvidenceObjectNamespace("normalized/identity"));

    private async Task<IdentityFixture> CreateIdentityCollectionAsync(string suffix)
    {
        var plan = new EvidenceCollectionPlan(
            CreatedAt,
            $"identity-{suffix}",
            Hash($"identity-config-{suffix}"),
            "identity-code-v1",
            "collector-v1",
            "partition-v1",
            [
                new EvidenceCollectionRequest(
                    "assets",
                    "alpaca",
                    "/v2/assets",
                    [],
                    null,
                    null,
                    "alpaca-trading",
                    "raw",
                    "USD",
                    SessionDate),
                new EvidenceCollectionRequest(
                    "actions",
                    "alpaca",
                    "/v1/corporate-actions",
                    [],
                    new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                    "alpaca-market-data",
                    "raw",
                    "USD",
                    SessionDate)
            ]);
        await catalog.RegisterCollectionPlanAsync(plan);
        var assetRequest = plan.Requests.Single(request =>
            request.RequestId.Equals("assets", StringComparison.Ordinal));
        var actionRequest = plan.Requests.Single(request =>
            request.RequestId.Equals("actions", StringComparison.Ordinal));

        await catalog.SaveCollectionCheckpointAsync(
            new EvidenceCollectionCheckpoint(
                plan.JobId,
                EvidenceCollectionState.Planned,
                CreatedAt));
        await catalog.SaveCollectionCheckpointAsync(
            new EvidenceCollectionCheckpoint(
                plan.JobId,
                EvidenceCollectionState.Collecting,
                CreatedAt.AddSeconds(1)));
        var asset = await RegisterObservationAsync(
            plan,
            assetRequest,
            AssetPayload(),
            IdentityObservedAt);
        var action = await RegisterObservationAsync(
            plan,
            actionRequest,
            CorporateActionPayload(),
            IdentityObservedAt.AddMinutes(1));
        await SaveTerminalCursorAsync(plan, assetRequest, asset);
        await SaveTerminalCursorAsync(plan, actionRequest, action);
        return new IdentityFixture(plan, assetRequest, actionRequest, asset, action);
    }

    private async Task<UniverseSnapshotFixture> CreateUniverseSnapshotAsync(string suffix)
    {
        var payload = Encoding.UTF8.GetBytes(
            """{"snapshot":"AAPL","score":99.5,"selection_reasons":["mega_cap_screen"]}""");
        var plan = new EvidenceCollectionPlan(
            CreatedAt,
            $"universe-snapshot-{suffix}",
            Hash($"universe-config-{suffix}"),
            "universe-code-v1",
            "collector-v1",
            "partition-v1",
            [
                new EvidenceCollectionRequest(
                    "snapshot",
                    "finviz",
                    "/export.ashx",
                    ["AAPL"],
                    SnapshotProviderAt.AddMinutes(-1),
                    SnapshotProviderAt.AddMinutes(1),
                    "finviz",
                    "raw",
                    "USD",
                    SessionDate)
            ]);
        await catalog.RegisterCollectionPlanAsync(plan);
        var observation = await RegisterObservationAsync(
            plan,
            plan.Requests[0],
            payload,
            SnapshotReceivedAt,
            SnapshotProviderAt);
        return new UniverseSnapshotFixture(
            observation,
            new PointInTimeUniverseCandidate(
                "AAPL",
                99.5m,
                SnapshotProviderAt,
                SnapshotReceivedAt,
                ["mega_cap_screen"]));
    }

    private UniverseMembershipPublicationRequest UniverseRequest(
        IdentityEvidencePublicationResult identity,
        UniverseSnapshotFixture snapshot) =>
        new(
            identity.SecurityMasterDatasetId,
            identity.SymbolIntervalsDatasetId,
            identity.CorporateActionsDatasetId,
            1,
            "universe-publication-run",
            Hash("universe-publication-config"),
            "universe-code-v1",
            "point-in-time-universe-v1",
            "finviz",
            "finviz",
            "USD",
            "mega-cap-us",
            "snapshot-20260702",
            "cap_mega",
            snapshot.Observation.Artifact.Content.Sha256,
            SessionDate,
            DecisionAt,
            SnapshotProviderAt,
            SnapshotReceivedAt,
            [snapshot.Candidate],
            [new EvidenceRowSourceAddress(
                snapshot.Observation.ObservationId,
                snapshot.Observation.Artifact.Content.Sha256)],
            new EvidenceObjectNamespace("normalized/universe"),
            maximumMembers: 100);

    private async Task<IdentityEvidencePublicationResult> PublishReadyIdentityAsync(
        IdentityFixture fixture)
    {
        const string securityId = "b0b6dd9d-8b9b-48a9-ba46-b9d54906e415";
        const string issuerId = "issuer-apple";
        var assetSource = new EvidenceRowSourceAddress(
            fixture.AssetObservation.ObservationId,
            fixture.AssetObservation.Artifact.Content.Sha256);
        var actionSource = new EvidenceRowSourceAddress(
            fixture.ActionObservation.ObservationId,
            fixture.ActionObservation.Artifact.Content.Sha256);
        var security = new SecurityMasterSnapshotEvidenceRow(
            1,
            fixture.Plan.RunId,
            fixture.Plan.ConfigHash,
            fixture.Plan.CodeVersion,
            "alpaca-trading",
            "alpaca",
            securityId,
            issuerId,
            "AAPL",
            "Apple Inc.",
            "NASDAQ",
            "us_equity",
            "USD",
            "active",
            true,
            true,
            true,
            true,
            true,
            "easy_to_borrow",
            30m,
            30m,
            30m,
            [],
            IdentityObservedAt,
            false,
            [assetSource]);
        var interval = new SymbolIntervalEvidenceRow(
            1,
            fixture.Plan.RunId,
            fixture.Plan.ConfigHash,
            fixture.Plan.CodeVersion,
            "alpaca-trading",
            "alpaca",
            securityId,
            issuerId,
            "AAPL",
            IdentityObservedAt,
            false,
            [assetSource]);
        var action = new CorporateActionEvidenceRow(
            1,
            fixture.Plan.RunId,
            fixture.Plan.ConfigHash,
            fixture.Plan.CodeVersion,
            "alpaca-market-data",
            "alpaca",
            CorporateActionEvidenceType.CashDividend,
            "1dbc7685-9517-4a77-a236-8527d49cefdc",
            null,
            null,
            "AAPL",
            "037833100",
            new DateOnly(2026, 6, 15),
            null,
            null,
            null,
            null,
            new Dictionary<string, string> { ["symbol"] = "AAPL", ["rate"] = "0.25" },
            IdentityObservedAt.AddMinutes(1),
            [actionSource]);
        var assetProvenance = new EvidencePartitionProvenance(
            "alpaca",
            "/v2/assets",
            "alpaca-trading",
            "raw",
            "USD",
            SessionDate,
            [securityId],
            [issuerId],
            ["AAPL"],
            "snapshot",
            IdentityObservedAt,
            IdentityObservedAt.AddTicks(1));
        var securityPartition = await publisher.PublishSecurityMasterSnapshotsAsync(
            PartitionRequest(
                fixture.Plan,
                EvidenceDatasetKind.SecurityMaster,
                "ready-security",
                assetProvenance,
                [fixture.AssetObservation.ToReference()],
                "ready-identity-v1"),
            [security]);
        var intervalPartition = await publisher.PublishSymbolIntervalsAsync(
            PartitionRequest(
                fixture.Plan,
                EvidenceDatasetKind.SymbolIntervals,
                "ready-interval",
                assetProvenance,
                [fixture.AssetObservation.ToReference()],
                "ready-identity-v1"),
            [interval]);
        var actionPartition = await publisher.PublishCorporateActionsAsync(
            PartitionRequest(
                fixture.Plan,
                EvidenceDatasetKind.CorporateActions,
                "ready-action",
                new EvidencePartitionProvenance(
                    "alpaca",
                    "/v1/corporate-actions",
                    "alpaca-market-data",
                    "raw",
                    "USD",
                    SessionDate,
                    [],
                    [],
                    ["AAPL"],
                    "event",
                    fixture.ActionRequest.RequestedStartUtc!.Value,
                    fixture.ActionRequest.RequestedEndUtc!.Value),
                [fixture.ActionObservation.ToReference()],
                "ready-identity-v1"),
            [action]);
        var quality = new EvidenceQualityReport();
        var manifests = new[]
        {
            Dataset(
                fixture.Plan,
                EvidenceDatasetKind.SecurityMaster,
                "alpaca-trading",
                securityPartition,
                "ready-identity-v1",
                quality),
            Dataset(
                fixture.Plan,
                EvidenceDatasetKind.SymbolIntervals,
                "alpaca-trading",
                intervalPartition,
                "ready-identity-v1",
                quality),
            Dataset(
                fixture.Plan,
                EvidenceDatasetKind.CorporateActions,
                "alpaca-market-data",
                actionPartition,
                "ready-identity-v1",
                quality)
        };
        var commits = await catalog.CommitDatasetsAtomicallyAsync(manifests);
        return new IdentityEvidencePublicationResult(
            fixture.Plan.JobId,
            commits[0].Id,
            commits[1].Id,
            commits[2].Id,
            commits.All(value => value.AlreadyCommitted),
            1,
            1,
            1);
    }

    private static EvidenceParquetPartitionRequest PartitionRequest(
        EvidenceCollectionPlan plan,
        EvidenceDatasetKind kind,
        string partitionId,
        EvidencePartitionProvenance provenance,
        IReadOnlyList<EvidenceSourceReference> sources,
        string normalizerVersion) =>
        new(
            partitionId,
            kind,
            1,
            provenance,
            sources,
            plan.RunId,
            plan.ConfigHash,
            normalizerVersion,
            plan.CodeVersion,
            new EvidenceQualityReport(),
            new EvidenceObjectNamespace("normalized/ready-identity"));

    private static EvidenceDatasetManifest Dataset(
        EvidenceCollectionPlan plan,
        EvidenceDatasetKind kind,
        string feed,
        EvidenceDatasetPartitionManifest partition,
        string normalizerVersion,
        EvidenceQualityReport quality) =>
        EvidenceParquetPartitionPublisher.BuildDatasetManifest(
            kind,
            1,
            plan.CreatedAtUtc,
            plan.JobId,
            plan.LogicalPlanHash,
            plan.ConfigHash,
            plan.CodeVersion,
            normalizerVersion,
            feed,
            [partition],
            quality);

    private async Task<EvidenceSourceObservation> RegisterObservationAsync(
        EvidenceCollectionPlan plan,
        EvidenceCollectionRequest request,
        byte[] bytes,
        DateTimeOffset receivedAtUtc,
        DateTimeOffset? providerUpdatedAtUtc = null)
    {
        var artifact = Artifact(
            bytes,
            "raw/provider",
            "application/json");
        await store.PutIfAbsentAsync(new(artifact), bytes);
        var observation = new EvidenceSourceObservation(
            $"observation-{request.RequestId}-{artifact.Content.Sha256[..16]}",
            plan.JobId,
            plan.LogicalPlanHash,
            plan.CollectionAttemptHash,
            request.RequestId,
            "page-1",
            request.Provider,
            request.Endpoint,
            EvidenceTransportKind.Http,
            200,
            request.Symbols,
            request.RequestedStartUtc,
            request.RequestedEndUtc,
            request.DataFeed,
            request.Adjustment,
            request.Currency,
            request.AsOfDate,
            null,
            null,
            providerUpdatedAtUtc,
            receivedAtUtc,
            artifact,
            plan.RunId,
            plan.ConfigHash,
            plan.CodeVersion,
            requestAttributes: new Dictionary<string, string>
            {
                ["attempt"] = "1",
                ["page_ordinal"] = "1"
            });
        await catalog.RegisterSourceObservationAsync(observation);
        return observation;
    }

    private async Task SaveTerminalCursorAsync(
        EvidenceCollectionPlan plan,
        EvidenceCollectionRequest request,
        EvidenceSourceObservation observation)
    {
        await catalog.SaveRequestCursorCheckpointAsync(
            new EvidenceRequestCursorCheckpoint(
                plan.JobId,
                request.RequestId,
                1,
                null,
                [],
                observation.ObservationId,
                1,
                true,
                observation.ReceivedAtUtc.AddSeconds(1)));
    }

    private async Task<int> CountIdentityDatasetsAsync(string jobId)
    {
        var count = 0;
        foreach (var kind in new[]
                 {
                     EvidenceDatasetKind.SecurityMaster,
                     EvidenceDatasetKind.SymbolIntervals,
                     EvidenceDatasetKind.CorporateActions
                 })
        {
            count += (await catalog.FindDatasetsAsync(new EvidenceDatasetQuery(kind)))
                .Count(dataset =>
                    dataset.CollectionJobId.Equals(jobId, StringComparison.Ordinal));
        }

        return count;
    }

    private static byte[] AssetPayload() => Encoding.UTF8.GetBytes(
        """
        [
          {
            "id":"b0b6dd9d-8b9b-48a9-ba46-b9d54906e415",
            "class":"us_equity",
            "exchange":"NASDAQ",
            "symbol":"AAPL",
            "name":"Apple Inc. Common Stock",
            "status":"active",
            "tradable":true,
            "marginable":true,
            "maintenance_margin_requirement":30,
            "margin_requirement_long":30,
            "margin_requirement_short":30,
            "shortable":true,
            "easy_to_borrow":true,
            "fractionable":true,
            "borrow_status":"easy_to_borrow",
            "attributes":["has_options","overnight_tradable"]
          }
        ]
        """);

    private static byte[] CorporateActionPayload() => Encoding.UTF8.GetBytes(
        """
        {
          "corporate_actions": {
            "cash_dividends": [{
              "id":"1dbc7685-9517-4a77-a236-8527d49cefdc",
              "process_date":"2026-06-15",
              "symbol":"AAPL",
              "cusip":"037833100",
              "rate":0.25,
              "foreign":false,
              "special":false
            }]
          },
          "next_page_token": null
        }
        """);

    private static EvidenceArtifactReference Artifact(
        ReadOnlySpan<byte> bytes,
        string objectNamespace,
        string mediaType) =>
        new(
            new EvidenceContentAddress(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length,
                mediaType),
            new EvidenceObjectNamespace(objectNamespace));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed record IdentityFixture(
        EvidenceCollectionPlan Plan,
        EvidenceCollectionRequest AssetRequest,
        EvidenceCollectionRequest ActionRequest,
        EvidenceSourceObservation AssetObservation,
        EvidenceSourceObservation ActionObservation);

    private sealed record UniverseSnapshotFixture(
        EvidenceSourceObservation Observation,
        PointInTimeUniverseCandidate Candidate);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FaultInjectingArtifactStore(IImmutableArtifactStore inner)
        : IImmutableArtifactStore
    {
        private int datasetManifestWrites;
        private int failedManifestWrite;

        public int? FailDatasetManifestWriteNumber { get; set; }

        public HashSet<string> InvalidHashes { get; } = new(StringComparer.Ordinal);

        public List<EvidenceArtifactReference> Published { get; } = [];

        public Task<ImmutableArtifactReceipt> PutIfAbsentAsync(
            ImmutableArtifactWriteRequest request,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            Published.Add(request.Artifact);
            if (request.Artifact.ObjectNamespace.Value.Equals(
                    EvidenceObjectNamespaces.DatasetManifests.Value,
                    StringComparison.Ordinal) &&
                Interlocked.Increment(ref datasetManifestWrites) ==
                    FailDatasetManifestWriteNumber &&
                Interlocked.Exchange(ref failedManifestWrite, 1) == 0)
            {
                throw new IOException("Injected atomic manifest publication failure.");
            }

            return inner.PutIfAbsentAsync(request, content, cancellationToken);
        }

        public Task<Stream> OpenReadAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(artifact, cancellationToken);

        public Task<ImmutableArtifactVerification> VerifyAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default)
        {
            if (InvalidHashes.Contains(artifact.Content.Sha256))
            {
                return Task.FromResult(new ImmutableArtifactVerification(
                    false,
                    artifact,
                    artifact.Content.ByteLength,
                    artifact.Content.Sha256,
                    "Injected tamper."));
            }

            return inner.VerifyAsync(artifact, cancellationToken);
        }
    }
}

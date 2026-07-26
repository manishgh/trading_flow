using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Tests;

public sealed class EvidenceNormalizationOrchestratorTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start =
        new(2026, 6, 1, 13, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddMinutes(10);
    private static readonly DateTimeOffset ReceiptTime = End.AddMinutes(1);
    private static readonly DateOnly AsOfDate = new(2026, 6, 2);
    private static readonly string ConfigHash = Hash("normalization-config");

    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-normalization-tests",
        Guid.NewGuid().ToString("N"));
    private FileSystemImmutableArtifactStore physicalStore = null!;
    private TrackingArtifactStore store = null!;
    private SqliteEvidenceCatalog catalog = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(rootPath);
        physicalStore = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(Path.Combine(rootPath, "objects")));
        store = new TrackingArtifactStore(physicalStore);
        catalog = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(
                Path.Combine(rootPath, "evidence.db"),
                EvidenceCatalogOpenMode.BootstrapNew),
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
    public async Task NormalizeAsync_VerifiesRetriesAndCommitsEverySuccessfulPageOnce()
    {
        var plan = await CreatePlanAsync("retry-and-pages");
        await RegisterObservationAsync(
            plan,
            "retry-page-1",
            1,
            1,
            429,
            """{"message":"slow down"}""");
        var page1 = await RegisterObservationAsync(
            plan,
            "success-page-1",
            1,
            2,
            200,
            BarsPayload(Start, "next"));
        var page2 = await RegisterObservationAsync(
            plan,
            "success-page-2",
            2,
            3,
            200,
            BarsPayload(Start.AddMinutes(1), null));
        await SaveTwoPageCursorAsync(plan, page1, page2);
        var orchestrator = CreateOrchestrator();
        var job = Job(plan);

        var results = await Task.WhenAll(
            orchestrator.NormalizeAsync(job),
            orchestrator.NormalizeAsync(job));

        Assert.Equal(results[0].DatasetId, results[1].DatasetId);
        Assert.All(results, result =>
        {
            Assert.Equal(1, result.PartitionCount);
            Assert.Equal(2, result.RowCount);
        });
        var checkpoint = await catalog.GetCollectionCheckpointAsync(plan.JobId);
        Assert.Equal(EvidenceCollectionState.Committed, checkpoint!.State);
        var dataset = await catalog.GetDatasetAsync(results[0].DatasetId);
        Assert.Equal("3", dataset!.Attributes["raw_receipt_count"]);
        Assert.Equal("1", dataset.Attributes["retry_receipt_count"]);
        Assert.Equal(
            [page1.ObservationId, page2.ObservationId],
            dataset.Partitions[0].SourceObservations
                .Select(source => source.ObservationId)
                .OrderBy(value => value, StringComparer.Ordinal));
        var rawObservations = await catalog.FindSourceObservationsAsync(plan.JobId, "bars");
        Assert.Contains(
            store.OpenedHashes,
            hash => hash.Equals(
                rawObservations[0].Artifact.Content.Sha256,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task NormalizeAsync_ManifestWriteCrashLeavesNoDatasetAndResumes()
    {
        var plan = await CreatePlanAsync("manifest-crash");
        var page = await RegisterObservationAsync(
            plan,
            "only-page",
            1,
            1,
            200,
            BarsPayload(Start, null));
        await SaveOnePageCursorAsync(plan, page);
        store.FailNextDatasetManifestWrite = true;
        var orchestrator = CreateOrchestrator();
        var job = Job(plan);

        await Assert.ThrowsAsync<IOException>(() => orchestrator.NormalizeAsync(job));

        Assert.Empty(await catalog.FindDatasetsAsync(
            new(EvidenceDatasetKind.MarketBarsAsTraded, "sip")));
        Assert.Equal(
            EvidenceCollectionState.Validating,
            (await catalog.GetCollectionCheckpointAsync(plan.JobId))!.State);
        Assert.Contains(
            store.PublishedNamespaces,
            value => value.Equals("normalized/alpaca-bars", StringComparison.Ordinal));

        var resumed = await orchestrator.NormalizeAsync(job);

        Assert.NotNull(await catalog.GetDatasetAsync(resumed.DatasetId));
        Assert.Equal(
            EvidenceCollectionState.Committed,
            (await catalog.GetCollectionCheckpointAsync(plan.JobId))!.State);
    }

    [Fact]
    public async Task NormalizeAsync_MalformedFirstPageStillReadsEveryRawPageBeforeParsing()
    {
        var plan = await CreatePlanAsync("verify-before-parse");
        var page1 = await RegisterObservationAsync(
            plan,
            "malformed-page-1",
            1,
            1,
            200,
            """{"bars": """);
        var page2 = await RegisterObservationAsync(
            plan,
            "valid-page-2",
            2,
            2,
            200,
            BarsPayload(Start.AddMinutes(1), null));
        await SaveTwoPageCursorAsync(plan, page1, page2, pageOneToken: "next");
        var expectedHashes = (await catalog.FindSourceObservationsAsync(plan.JobId, "bars"))
            .Select(observation => observation.Artifact.Content.Sha256)
            .ToHashSet(StringComparer.Ordinal);

        await Assert.ThrowsAsync<EvidenceNormalizationPipelineException>(() =>
            CreateOrchestrator().NormalizeAsync(Job(plan)));

        Assert.True(expectedHashes.SetEquals(store.OpenedHashes));
        Assert.Empty(await catalog.FindDatasetsAsync(
            new(EvidenceDatasetKind.MarketBarsAsTraded, "sip")));
        Assert.Equal(
            EvidenceCollectionState.Quarantined,
            (await catalog.GetCollectionCheckpointAsync(plan.JobId))!.State);
    }

    [Fact]
    public async Task NormalizeAsync_RejectsReceiptOutsideExhaustedPageChain()
    {
        var plan = await CreatePlanAsync("rogue-page");
        var page = await RegisterObservationAsync(
            plan,
            "terminal-page",
            1,
            1,
            200,
            BarsPayload(Start, null));
        await SaveOnePageCursorAsync(plan, page);
        await RegisterObservationAsync(
            plan,
            "uncommitted-page-2",
            2,
            2,
            200,
            BarsPayload(Start.AddMinutes(1), null));

        var error = await Assert.ThrowsAsync<EvidenceNormalizationPipelineException>(() =>
            CreateOrchestrator().NormalizeAsync(Job(plan)));

        Assert.Equal(
            EvidenceNormalizationPipelineFailureCode.IncompletePagination,
            error.Code);
        Assert.Empty(await catalog.FindDatasetsAsync(
            new(EvidenceDatasetKind.MarketBarsAsTraded, "sip")));
        Assert.Equal(
            EvidenceCollectionState.Quarantined,
            (await catalog.GetCollectionCheckpointAsync(plan.JobId))!.State);
    }

    [Fact]
    public async Task NormalizeAsync_OutOfRangeBarQuarantinesWithoutDatasetVisibility()
    {
        var plan = await CreatePlanAsync("out-of-range");
        var page = await RegisterObservationAsync(
            plan,
            "out-of-range-page",
            1,
            1,
            200,
            BarsPayload(End, null));
        await SaveOnePageCursorAsync(plan, page);

        var error = await Assert.ThrowsAsync<EvidenceNormalizationException>(() =>
            CreateOrchestrator().NormalizeAsync(Job(plan)));

        Assert.Equal(
            EvidenceNormalizationFailureCode.TimestampOutsideRequestedRange,
            error.Code);
        Assert.Empty(await catalog.FindDatasetsAsync(
            new(EvidenceDatasetKind.MarketBarsAsTraded, "sip")));
        Assert.Single(await catalog.FindQuarantinesAsync(EvidenceQuarantineStatus.Open));
        Assert.Equal(
            EvidenceCollectionState.Quarantined,
            (await catalog.GetCollectionCheckpointAsync(plan.JobId))!.State);
    }

    private EvidenceNormalizationOrchestrator CreateOrchestrator() =>
        new(
            catalog,
            store,
            new EvidenceParquetPartitionPublisher(
                new EvidenceParquetCodec(),
                store),
            timeProvider: new FixedTimeProvider(ReceiptTime.AddHours(1)));

    private static EvidenceNormalizationJob Job(EvidenceCollectionPlan plan) =>
        new(
            plan.JobId,
            EvidenceDatasetKind.MarketBarsAsTraded,
            1,
            "alpaca-bars-v1",
            [new EvidenceSecurityIdentity("security-aapl", "AAPL")],
            new EvidenceObjectNamespace("normalized/alpaca-bars"));

    private async Task<EvidenceCollectionPlan> CreatePlanAsync(string runId)
    {
        var plan = new EvidenceCollectionPlan(
            Start.AddDays(-1),
            runId,
            ConfigHash,
            "test-code",
            "collector-v1",
            "partition-v1",
            [
                new EvidenceCollectionRequest(
                    "bars",
                    "alpaca",
                    "/v2/stocks/bars",
                    ["AAPL"],
                    Start,
                    End,
                    "sip",
                    "raw",
                    "USD",
                    AsOfDate,
                    new Dictionary<string, string> { ["timeframe"] = "1Min" })
            ]);
        await catalog.RegisterCollectionPlanAsync(plan);
        await catalog.SaveCollectionCheckpointAsync(
            new EvidenceCollectionCheckpoint(
                plan.JobId,
                EvidenceCollectionState.Planned,
                Start.AddDays(-1)));
        await catalog.SaveCollectionCheckpointAsync(
            new EvidenceCollectionCheckpoint(
                plan.JobId,
                EvidenceCollectionState.Collecting,
                Start.AddDays(-1).AddSeconds(1)));
        return plan;
    }

    private async Task<EvidenceSourceObservation> RegisterObservationAsync(
        EvidenceCollectionPlan plan,
        string receiptId,
        int pageOrdinal,
        int attempt,
        int statusCode,
        string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var artifact = new EvidenceArtifactReference(
            new EvidenceContentAddress(
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                bytes.Length,
                "application/json"),
            new EvidenceObjectNamespace("raw/alpaca"));
        await store.PutIfAbsentAsync(new(artifact), bytes);
        var observation = new EvidenceSourceObservation(
            $"observation-{receiptId}",
            plan.JobId,
            plan.LogicalPlanHash,
            plan.CollectionAttemptHash,
            "bars",
            $"page-{pageOrdinal}",
            "alpaca",
            "/v2/stocks/bars",
            EvidenceTransportKind.Http,
            statusCode,
            ["AAPL"],
            Start,
            End,
            "sip",
            "raw",
            "USD",
            AsOfDate,
            null,
            null,
            null,
            ReceiptTime.AddSeconds(attempt),
            artifact,
            plan.RunId,
            plan.ConfigHash,
            plan.CodeVersion,
            requestAttributes: new Dictionary<string, string>
            {
                ["page_ordinal"] = pageOrdinal.ToString(),
                ["attempt"] = attempt.ToString(),
                ["receipt_id"] = receiptId
            });
        await catalog.RegisterSourceObservationAsync(observation);
        return observation;
    }

    private async Task SaveOnePageCursorAsync(
        EvidenceCollectionPlan plan,
        EvidenceSourceObservation page)
    {
        await catalog.SaveRequestCursorCheckpointAsync(
            new EvidenceRequestCursorCheckpoint(
                plan.JobId,
                "bars",
                1,
                null,
                [],
                page.ObservationId,
                1,
                true,
                ReceiptTime.AddMinutes(1)));
    }

    private async Task SaveTwoPageCursorAsync(
        EvidenceCollectionPlan plan,
        EvidenceSourceObservation page1,
        EvidenceSourceObservation page2,
        string pageOneToken = "next")
    {
        await catalog.SaveRequestCursorCheckpointAsync(
            new EvidenceRequestCursorCheckpoint(
                plan.JobId,
                "bars",
                1,
                pageOneToken,
                [],
                page1.ObservationId,
                2,
                false,
                ReceiptTime.AddMinutes(1)));
        await catalog.SaveRequestCursorCheckpointAsync(
            new EvidenceRequestCursorCheckpoint(
                plan.JobId,
                "bars",
                2,
                null,
                [Hash(pageOneToken)],
                page2.ObservationId,
                3,
                true,
                ReceiptTime.AddMinutes(2)));
    }

    private static string BarsPayload(DateTimeOffset timestamp, string? nextPageToken)
    {
        var token = nextPageToken is null
            ? "null"
            : $"\"{nextPageToken}\"";
        return $$"""
            {
              "bars": {
                "AAPL": [
                  {
                    "t":"{{timestamp:yyyy-MM-ddTHH:mm:ssZ}}",
                    "o":100,
                    "h":104,
                    "l":99,
                    "c":102,
                    "v":1200,
                    "n":40,
                    "vw":101.5
                  }
                ]
              },
              "next_page_token": {{token}}
            }
            """;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TrackingArtifactStore(IImmutableArtifactStore inner)
        : IImmutableArtifactStore
    {
        private int failNextDatasetManifestWrite;

        public bool FailNextDatasetManifestWrite
        {
            get => Volatile.Read(ref failNextDatasetManifestWrite) == 1;
            set => Volatile.Write(ref failNextDatasetManifestWrite, value ? 1 : 0);
        }

        public HashSet<string> OpenedHashes { get; } = new(StringComparer.Ordinal);

        public List<string> PublishedNamespaces { get; } = [];

        public Task<ImmutableArtifactReceipt> PutIfAbsentAsync(
            ImmutableArtifactWriteRequest request,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            PublishedNamespaces.Add(request.Artifact.ObjectNamespace.Value);
            if (request.Artifact.ObjectNamespace.Value.Equals(
                    EvidenceObjectNamespaces.DatasetManifests.Value,
                    StringComparison.Ordinal) &&
                Interlocked.Exchange(ref failNextDatasetManifestWrite, 0) == 1)
            {
                throw new IOException("Injected dataset manifest write failure.");
            }

            return inner.PutIfAbsentAsync(request, content, cancellationToken);
        }

        public async Task<Stream> OpenReadAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default)
        {
            OpenedHashes.Add(artifact.Content.Sha256);
            return await inner.OpenReadAsync(artifact, cancellationToken);
        }

        public Task<ImmutableArtifactVerification> VerifyAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default) =>
            inner.VerifyAsync(artifact, cancellationToken);
    }
}

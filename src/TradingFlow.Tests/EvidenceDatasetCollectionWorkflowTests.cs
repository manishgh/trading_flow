using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Collection;
using TradingFlow.Data.Evidence.Normalization;
using TradingFlow.Data.Evidence.Parquet;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Storage;
using TradingFlow.Research.Catalysts;
using TradingFlow.Research.Orchestration;

namespace TradingFlow.Tests;

public sealed class EvidenceDatasetCollectionWorkflowTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 1, 13, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddMinutes(10);
    private static readonly DateTimeOffset Now = End.AddMinutes(1).AddTicks(9);

    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-evidence-workflow-tests",
        Guid.NewGuid().ToString("N"));
    private TrackingArtifactStore store = null!;
    private SqliteEvidenceCatalog catalog = null!;
    private EvidencePublicationCoordinator publicationCoordinator = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(rootPath);
        var physicalStore = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(Path.Combine(rootPath, "objects")));
        store = new TrackingArtifactStore(physicalStore);
        catalog = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(
                Path.Combine(rootPath, "evidence.db"),
                EvidenceCatalogOpenMode.BootstrapNew),
            store);
        publicationCoordinator = new EvidencePublicationCoordinator(
            catalog,
            store,
            new FixedTimeProvider(Now));
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
    public async Task RunAsync_CollectsBeforeNormalizingAndReturnsCommittedDataset()
    {
        var handler = new CountingHandler(
            _ => Response(HttpStatusCode.OK, BarsPayload()));
        var workflow = CreateWorkflow(handler);
        var plan = Plan("success");

        var result = await workflow.RunAsync(plan, Job(plan));

        Assert.False(result.AlreadyCommitted);
        Assert.False(String.IsNullOrWhiteSpace(result.DatasetId));
        Assert.Equal(1, result.PartitionCount);
        Assert.Equal(1, result.RowCount);
        Assert.Equal(
            EvidenceCollectionState.Committed,
            (await catalog.GetCollectionCheckpointAsync(plan.JobId))!.State);
        Assert.NotNull(await catalog.GetDatasetAsync(result.DatasetId));
        var rawIndex = store.PublishedNamespaces.FindIndex(value =>
            value.StartsWith("raw/", StringComparison.Ordinal));
        var normalizedIndex = store.PublishedNamespaces.FindIndex(value =>
            value.Equals("normalized/workflow-bars", StringComparison.Ordinal));
        Assert.True(rawIndex >= 0, "The raw provider receipt was not published.");
        Assert.True(
            normalizedIndex > rawIndex,
            "Normalized evidence was published before the raw provider receipt.");
    }

    [Fact]
    public async Task RunAsync_IncompleteCollectionPreventsNormalization()
    {
        var handler = new CountingHandler(
            _ => Response(HttpStatusCode.InternalServerError, """{"error":"temporary"}"""));
        var workflow = CreateWorkflow(handler, maximumAttempts: 1);
        var plan = Plan("incomplete");

        var error = await Assert.ThrowsAsync<EvidenceDatasetCollectionWorkflowException>(
            () => workflow.RunAsync(plan, Job(plan)));

        Assert.Equal(EvidenceCollectionState.Incomplete, error.CollectionState);
        Assert.Equal(plan.JobId, error.JobId);
        Assert.DoesNotContain(
            store.PublishedNamespaces,
            value => value.Equals("normalized/workflow-bars", StringComparison.Ordinal));
        Assert.Empty(await catalog.FindDatasetsAsync(
            new(EvidenceDatasetKind.MarketBarsAsTraded, "sip")));
    }

    [Fact]
    public async Task RunAsync_CancellationStopsBeforeNormalization()
    {
        var handler = new HangingHandler();
        var workflow = CreateWorkflow(handler);
        var plan = Plan("cancelled");
        using var cancellation = new CancellationTokenSource();
        var run = workflow.RunAsync(plan, Job(plan), cancellation.Token);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run);

        var checkpoint = await catalog.GetCollectionCheckpointAsync(plan.JobId);
        Assert.Equal(EvidenceCollectionState.Incomplete, checkpoint!.State);
        Assert.Contains("may be resumed", checkpoint.FailureReason);
        Assert.DoesNotContain(
            store.PublishedNamespaces,
            value => value.Equals("normalized/workflow-bars", StringComparison.Ordinal));
        Assert.Empty(await catalog.FindDatasetsAsync(
            new(EvidenceDatasetKind.MarketBarsAsTraded, "sip")));
    }

    [Fact]
    public async Task RunAsync_ReplayReturnsExistingDatasetWithoutAnotherProviderCall()
    {
        var handler = new CountingHandler(
            _ => Response(HttpStatusCode.OK, BarsPayload()));
        var workflow = CreateWorkflow(handler);
        var plan = Plan("replay");
        var job = Job(plan);

        var first = await workflow.RunAsync(plan, job);
        var replay = await workflow.RunAsync(plan, job);

        Assert.Equal(first.DatasetId, replay.DatasetId);
        Assert.True(replay.AlreadyCommitted);
        Assert.Equal(1, handler.CallCount);
        var datasets = await catalog.FindDatasetsAsync(
            new(EvidenceDatasetKind.MarketBarsAsTraded, "sip"));
        Assert.Single(datasets);
    }

    [Fact]
    public async Task RunAsync_CommittedReplayRejectsMismatchedNormalizationContract()
    {
        var handler = new CountingHandler(
            _ => Response(HttpStatusCode.OK, BarsPayload()));
        var workflow = CreateWorkflow(handler);
        var plan = Plan("contract-mismatch");
        var committedJob = Job(plan);
        await workflow.RunAsync(plan, committedJob);
        var mismatchedJob = new EvidenceNormalizationJob(
            plan.JobId,
            committedJob.DatasetKind,
            committedJob.SchemaVersion,
            committedJob.NormalizerVersion,
            committedJob.Securities,
            new EvidenceObjectNamespace("normalized/different-contract"),
            committedJob.NewsAvailabilityEvidence,
            committedJob.Attributes);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => workflow.RunAsync(plan, mismatchedJob));

        Assert.Contains("normalization contract", error.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task CalendarRun_PublishesCompleteCoverageAndResolvesOpenAndClosedDates()
    {
        var handler = new CountingHandler(
            _ => Response(HttpStatusCode.OK, CalendarPayload()));
        var workflow = CreateCalendarWorkflow(handler);
        var plan = CalendarPlan();

        var result = await workflow.RunAsync(plan, CalendarJob(plan));
        var manifest = await catalog.GetDatasetAsync(result.DatasetId);
        var partition = Assert.Single(manifest!.Partitions);
        Assert.Equal("2026-07-01", partition.Dimensions["calendar_coverage_start_date"]);
        Assert.Equal(
            "2026-07-08",
            partition.Dimensions["calendar_coverage_end_date_exclusive"]);
        Assert.Equal("true", partition.Dimensions["calendar_source_complete"]);

        var resolver = await new CatalogExchangeSessionResolverFactory(
                catalog,
                new ParquetEvidencePartitionDataReader(
                    new EvidenceParquetCodec(),
                    store))
            .CreateAsync(result.DatasetId, Now.AddMinutes(2));

        Assert.Equal(
            EquityTradingSession.Regular,
            resolver.Resolve(new DateTimeOffset(2026, 7, 2, 15, 0, 0, TimeSpan.Zero)).Session);
        Assert.Equal(
            EquityTradingSession.Closed,
            resolver.Resolve(new DateTimeOffset(2026, 7, 3, 15, 0, 0, TimeSpan.Zero)).Session);
        Assert.Equal(
            EquityTradingSession.Closed,
            resolver.Resolve(new DateTimeOffset(2026, 7, 4, 15, 0, 0, TimeSpan.Zero)).Session);
        Assert.Throws<InvalidDataException>(() =>
            resolver.Resolve(new DateTimeOffset(2026, 7, 8, 15, 0, 0, TimeSpan.Zero)));
    }

    private EvidenceDatasetCollectionWorkflow CreateWorkflow(
        HttpMessageHandler handler,
        int maximumAttempts = 2)
    {
        var collector = new EvidenceHttpCollector(
            new HttpClient(handler),
            catalog,
            publicationCoordinator,
            [new AlpacaBarsEvidenceRequestAdapter(new Uri("https://data.example.test"))],
            new EvidenceHttpCollectionOptions(
                workerCount: 1,
                boundedCapacity: 2,
                maximumAttempts: maximumAttempts,
                requestTimeout: TimeSpan.FromSeconds(2),
                initialRetryDelay: TimeSpan.FromMilliseconds(1),
                maximumRetryDelay: TimeSpan.FromMilliseconds(2)),
            new NoDelayScheduler(),
            new FixedTimeProvider(Now),
            new EvidenceProviderRateBudget(new FixedTimeProvider(Now)));
        var normalizer = new EvidenceNormalizationOrchestrator(
            catalog,
            store,
            new EvidenceParquetPartitionPublisher(
                new EvidenceParquetCodec(),
                store),
            timeProvider: new FixedTimeProvider(Now.AddMinutes(1)));
        return new EvidenceDatasetCollectionWorkflow(collector, normalizer);
    }

    private EvidenceDatasetCollectionWorkflow CreateCalendarWorkflow(
        HttpMessageHandler handler)
    {
        var collector = new EvidenceHttpCollector(
            new HttpClient(handler),
            catalog,
            publicationCoordinator,
            [
                new AlpacaExchangeCalendarEvidenceRequestAdapter(
                    new Uri("https://paper-api.alpaca.test"))
            ],
            new EvidenceHttpCollectionOptions(
                workerCount: 1,
                boundedCapacity: 2,
                maximumAttempts: 2,
                requestTimeout: TimeSpan.FromSeconds(2),
                initialRetryDelay: TimeSpan.FromMilliseconds(1),
                maximumRetryDelay: TimeSpan.FromMilliseconds(2)),
            new NoDelayScheduler(),
            new FixedTimeProvider(Now),
            new EvidenceProviderRateBudget(new FixedTimeProvider(Now)));
        var normalizer = new EvidenceNormalizationOrchestrator(
            catalog,
            store,
            new EvidenceParquetPartitionPublisher(
                new EvidenceParquetCodec(),
                store),
            timeProvider: new FixedTimeProvider(Now.AddMinutes(1)));
        return new EvidenceDatasetCollectionWorkflow(collector, normalizer);
    }

    private static EvidenceCollectionPlan Plan(string runId) =>
        new(
            Start.AddDays(-1),
            runId,
            Hash($"config-{runId}"),
            "workflow-tests",
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
                    new DateOnly(2026, 7, 2),
                    new Dictionary<string, string>
                    {
                        ["timeframe"] = "1Min"
                    })
            ]);

    private static EvidenceNormalizationJob Job(EvidenceCollectionPlan plan) =>
        new(
            plan.JobId,
            EvidenceDatasetKind.MarketBarsAsTraded,
            1,
            "workflow-normalizer-v1",
            [new EvidenceSecurityIdentity("security-aapl", "AAPL")],
            new EvidenceObjectNamespace("normalized/workflow-bars"));

    private static EvidenceCollectionPlan CalendarPlan() =>
        new(
            Start.AddDays(-1),
            "calendar-workflow",
            Hash("calendar-workflow-config"),
            "workflow-tests",
            "collector-v1",
            "partition-v1",
            [
                new EvidenceCollectionRequest(
                    "calendar",
                    "alpaca",
                    "/v2/calendar",
                    [],
                    new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.Zero),
                    "alpaca-trading",
                    "raw",
                    "USD",
                    new DateOnly(2026, 7, 8))
            ]);

    private static EvidenceNormalizationJob CalendarJob(EvidenceCollectionPlan plan) =>
        new(
            plan.JobId,
            EvidenceDatasetKind.ExchangeSessions,
            1,
            "calendar-normalizer-v1",
            [],
            new EvidenceObjectNamespace("normalized/workflow-calendar"));

    private static HttpResponseMessage Response(HttpStatusCode status, string content) =>
        new(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

    private static string BarsPayload() =>
        $$"""
          {
            "bars": {
              "AAPL": [
                {
                  "t":"{{Start:yyyy-MM-ddTHH:mm:ssZ}}",
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
            "next_page_token": null
          }
          """;

    private static string CalendarPayload() =>
        """
        [
          {"date":"2026-07-01","open":"09:30","close":"16:00"},
          {"date":"2026-07-02","open":"09:30","close":"16:00"},
          {"date":"2026-07-06","open":"09:30","close":"16:00"},
          {"date":"2026-07-07","open":"09:30","close":"16:00"}
        ]
        """;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed class CountingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class NoDelayScheduler : IEvidenceRetryScheduler
    {
        public Task DelayAsync(
            TimeSpan delay,
            int attempt,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TrackingArtifactStore(IImmutableArtifactStore inner)
        : IImmutableArtifactStore
    {
        public List<string> PublishedNamespaces { get; } = [];

        public Task<ImmutableArtifactReceipt> PutIfAbsentAsync(
            ImmutableArtifactWriteRequest request,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            PublishedNamespaces.Add(request.Artifact.ObjectNamespace.Value);
            return inner.PutIfAbsentAsync(request, content, cancellationToken);
        }

        public Task<Stream> OpenReadAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(artifact, cancellationToken);

        public Task<ImmutableArtifactVerification> VerifyAsync(
            EvidenceArtifactReference artifact,
            CancellationToken cancellationToken = default) =>
            inner.VerifyAsync(artifact, cancellationToken);
    }
}

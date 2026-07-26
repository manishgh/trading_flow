using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingFlow.Data.Evidence;
using TradingFlow.Data.Evidence.Collection;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;

namespace TradingFlow.Tests;

public sealed class EvidenceCollectionPipelineTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "trading-flow-collection-tests",
        Guid.NewGuid().ToString("N"));
    private FileSystemImmutableArtifactStore store = null!;
    private SqliteEvidenceCatalog catalog = null!;
    private EvidencePublicationCoordinator coordinator = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(rootPath);
        store = new FileSystemImmutableArtifactStore(
            new ImmutableArtifactStoreOptions(Path.Combine(rootPath, "objects")));
        catalog = new SqliteEvidenceCatalog(
            new EvidenceCatalogOptions(
                Path.Combine(rootPath, "evidence.db"),
                EvidenceCatalogOpenMode.BootstrapNew),
            store);
        coordinator = new EvidencePublicationCoordinator(catalog, store, new FixedTimeProvider(Now));
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
    public async Task CollectAsync_PublishesEveryReceiptBeforeParsingAndCompletesPageLedger()
    {
        var handler = new SequenceHandler(
            Response(HttpStatusCode.TooManyRequests, """{"message":"slow"}""", retryAfterSeconds: 2),
            Response(HttpStatusCode.OK, """{"bars":{"AAPL":[]},"next_page_token":"next-A"}"""),
            Response(HttpStatusCode.OK, """{"bars":{"AAPL":[]},"next_page_token":null}"""));
        var recording = new RecordingCoordinator(coordinator);
        var adapter = new PublicationAssertingAdapter(
            BarsAdapter(),
            catalog,
            store);
        var collector = Collector(handler, recording, [adapter], maximumAttempts: 3);
        var plan = Plan(BarsRequest());

        var result = await collector.CollectAsync(plan);

        Assert.Equal(EvidenceCollectionState.Normalizing, result.State);
        var requestResult = Assert.Single(result.Requests);
        Assert.True(requestResult.Completed);
        Assert.True(
            requestResult.PagesPublished == 2,
            $"Expected two published pages, got {requestResult.PagesPublished}.");
        Assert.Equal(3, requestResult.Attempts);
        Assert.Equal(3, recording.Published.Count);
        Assert.Equal("""{"message":"slow"}""", Encoding.UTF8.GetString(recording.Published[0].Content));
        Assert.True(adapter.ParseCount == 2, $"Expected two parses, got {adapter.ParseCount}.");
        var cursor = await catalog.GetRequestCursorCheckpointAsync(plan.JobId, "bars");
        Assert.NotNull(cursor);
        Assert.True(cursor!.Exhausted);
        Assert.True(cursor.PageOrdinal == 2, $"Expected cursor page two, got {cursor.PageOrdinal}.");
        Assert.Single(cursor.ConsumedPageTokenHashes);
        Assert.Equal(3, cursor.AttemptCount);
        Assert.Equal(3, handler.RequestUris.Count);
        Assert.DoesNotContain("page_token", handler.RequestUris[0].Query);
        Assert.DoesNotContain("page_token", handler.RequestUris[1].Query);
        Assert.Contains("page_token=next-A", handler.RequestUris[2].Query);
    }

    [Fact]
    public async Task CollectAsync_ResumesFromCommittedCursorWithoutRefetchingPageOne()
    {
        var plan = Plan(BarsRequest());
        var first = Collector(
            new SequenceHandler(
                Response(HttpStatusCode.OK, """{"bars":{},"next_page_token":"resume-me"}"""),
                new HttpRequestException("offline")),
            coordinator,
            [BarsAdapter()],
            maximumAttempts: 1);
        var interrupted = await first.CollectAsync(plan);
        Assert.Equal(EvidenceCollectionState.Incomplete, interrupted.State);

        var resumedHandler = new SequenceHandler(
            Response(HttpStatusCode.OK, """{"bars":{},"next_page_token":null}"""));
        var resumed = Collector(
            resumedHandler,
            coordinator,
            [BarsAdapter()],
            maximumAttempts: 1);
        var completed = await resumed.CollectAsync(plan);

        Assert.Equal(EvidenceCollectionState.Normalizing, completed.State);
        Assert.Single(resumedHandler.RequestUris);
        Assert.Contains("page_token=resume-me", resumedHandler.RequestUris[0].Query);
        var cursor = await catalog.GetRequestCursorCheckpointAsync(plan.JobId, "bars");
        Assert.True(cursor!.Exhausted);
        Assert.Equal(2, cursor.PageOrdinal);
    }

    [Fact]
    public async Task CollectAsync_QuarantinesRepeatedPaginationTokenAfterPublishingRawPage()
    {
        var handler = new SequenceHandler(
            Response(HttpStatusCode.OK, """{"bars":{},"next_page_token":"cycle"}"""),
            Response(HttpStatusCode.OK, """{"bars":{},"next_page_token":"cycle"}"""));
        var recording = new RecordingCoordinator(coordinator);
        var collector = Collector(
            handler,
            recording,
            [BarsAdapter()],
            maximumAttempts: 1);
        var plan = Plan(BarsRequest());

        var result = await collector.CollectAsync(plan);

        Assert.Equal(EvidenceCollectionState.Quarantined, result.State);
        Assert.Equal(2, recording.Published.Count);
        var quarantine = Assert.Single(
            await catalog.FindQuarantinesAsync(EvidenceQuarantineStatus.Open));
        Assert.Equal("pagination_token_cycle", quarantine.Record.ReasonCode);
        Assert.Equal(recording.Published[1].Observation.ObservationId,
            quarantine.Record.Lineage.Sources.Single().ObservationId);
    }

    [Fact]
    public async Task CollectAsync_QuarantinesPermanentHttpResponseAndRetainsItsExactBody()
    {
        const string raw = """{"code":40110000,"message":"invalid credentials"}""";
        var recording = new RecordingCoordinator(coordinator);
        var collector = Collector(
            new SequenceHandler(Response(HttpStatusCode.Unauthorized, raw)),
            recording,
            [BarsAdapter()],
            maximumAttempts: 3);
        var plan = Plan(BarsRequest());

        var result = await collector.CollectAsync(plan);

        Assert.Equal(EvidenceCollectionState.Quarantined, result.State);
        var receipt = Assert.Single(recording.Published);
        Assert.Equal(raw, Encoding.UTF8.GetString(receipt.Content));
        Assert.Equal(401, receipt.Observation.TransportStatusCode);
        Assert.Equal("http_non_retryable_status", Assert.Single(
            await catalog.FindQuarantinesAsync(EvidenceQuarantineStatus.Open))
            .Record.ReasonCode);
    }

    [Fact]
    public async Task CollectAsync_EnforcesWorkerBoundAcrossIndependentRequests()
    {
        var handler = new ConcurrencyHandler(TimeSpan.FromMilliseconds(30));
        using var client = new HttpClient(handler);
        var collector = new EvidenceHttpCollector(
            client,
            catalog,
            coordinator,
            [new FinvizRawEvidenceRequestAdapter()],
            new EvidenceHttpCollectionOptions(
                workerCount: 2,
                boundedCapacity: 2,
                maximumAttempts: 1),
            new NoDelayScheduler(),
            new FixedTimeProvider(Now));
        var requests = Enumerable.Range(1, 6)
            .Select(index => new EvidenceCollectionRequest(
                $"finviz-{index}",
                "finviz",
                "/export.ashx",
                [],
                null,
                null,
                "finviz-elite",
                "raw",
                "USD",
                new DateOnly(2026, 7, 25),
                new Dictionary<string, string> { ["v"] = index.ToString() }))
            .ToArray();

        var result = await collector.CollectAsync(Plan(requests));

        Assert.Equal(EvidenceCollectionState.Normalizing, result.State);
        Assert.Equal(6, result.Requests.Count);
        Assert.InRange(handler.MaximumConcurrency, 2, 2);
    }

    [Fact]
    public async Task CollectAsync_TimesOutWithBoundedAttemptsAndLeavesResumableState()
    {
        var collector = Collector(
            new HangingHandler(),
            coordinator,
            [BarsAdapter()],
            maximumAttempts: 2,
            timeout: TimeSpan.FromMilliseconds(20));
        var plan = Plan(BarsRequest());

        var result = await collector.CollectAsync(plan);

        Assert.Equal(EvidenceCollectionState.Incomplete, result.State);
        var request = Assert.Single(result.Requests);
        Assert.False(request.Completed);
        Assert.Equal(2, request.Attempts);
        Assert.Null(await catalog.GetRequestCursorCheckpointAsync(plan.JobId, "bars"));
    }

    [Fact]
    public async Task CollectAsync_CrashAfterObservationCommit_RetainsDistinctRetryReceiptAndRecovers()
    {
        var plan = Plan(BarsRequest());
        var firstRecording = new RecordingCoordinator(coordinator);
        var first = Collector(
            new SequenceHandler(
                Response(HttpStatusCode.OK, """{"bars":{},"next_page_token":null}""")),
            new PublishThenThrowCoordinator(firstRecording),
            [BarsAdapter()],
            maximumAttempts: 1);

        await Assert.ThrowsAsync<IOException>(() => first.CollectAsync(plan));
        Assert.Null(await catalog.GetRequestCursorCheckpointAsync(plan.JobId, "bars"));

        var retryRecording = new RecordingCoordinator(coordinator);
        var retry = Collector(
            new SequenceHandler(
                Response(HttpStatusCode.OK, """{"bars":{},"next_page_token":null}""")),
            retryRecording,
            [BarsAdapter()],
            maximumAttempts: 1);
        var result = await retry.CollectAsync(plan);

        Assert.Equal(EvidenceCollectionState.Normalizing, result.State);
        Assert.NotEqual(
            Assert.Single(firstRecording.Published).Observation.ObservationId,
            Assert.Single(retryRecording.Published).Observation.ObservationId);
        Assert.Equal(
            firstRecording.Published[0].Observation.Artifact,
            retryRecording.Published[0].Observation.Artifact);
        Assert.Equal(2, await SourceObservationCountAsync(plan.JobId));
    }

    [Fact]
    public async Task CollectAsync_TwoCollectorsForSamePlan_AdoptCompatibleCommittedCursor()
    {
        var plan = Plan(BarsRequest());
        var barrier = new BarrierResponseHandler(
            2,
            """{"bars":{},"next_page_token":null}""");
        var firstRecording = new RecordingCoordinator(coordinator);
        var secondRecording = new RecordingCoordinator(coordinator);
        var first = Collector(
            barrier,
            firstRecording,
            [BarsAdapter()],
            maximumAttempts: 1);
        var second = Collector(
            barrier,
            secondRecording,
            [BarsAdapter()],
            maximumAttempts: 1);

        var results = await Task.WhenAll(
            first.CollectAsync(plan),
            second.CollectAsync(plan));

        Assert.All(results, result =>
            Assert.Equal(EvidenceCollectionState.Normalizing, result.State));
        Assert.True((await catalog.GetRequestCursorCheckpointAsync(plan.JobId, "bars"))!.Exhausted);
        Assert.Equal(2, await SourceObservationCountAsync(plan.JobId));
        Assert.NotEqual(
            Assert.Single(firstRecording.Published).Observation.ObservationId,
            Assert.Single(secondRecording.Published).Observation.ObservationId);
    }

    [Fact]
    public async Task CollectAsync_RetryAfterUpdatesSharedProviderBudgetBeforeRetry()
    {
        var budget = new RecordingRateBudget();
        var collector = Collector(
            new SequenceHandler(
                Response(HttpStatusCode.TooManyRequests, "limited", retryAfterSeconds: 7),
                Response(HttpStatusCode.OK, """{"bars":{},"next_page_token":null}""")),
            coordinator,
            [BarsAdapter()],
            maximumAttempts: 2,
            rateBudget: budget);

        var result = await collector.CollectAsync(Plan(BarsRequest()));

        Assert.Equal(EvidenceCollectionState.Normalizing, result.State);
        Assert.Equal(2, budget.WaitCount);
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(budget.Deferrals));
        Assert.True(budget.WaitObservedAfterDeferral);
    }

    [Fact]
    public async Task CollectAsync_RejectsDeclaredOversizedResponseWithoutMaterializingIt()
    {
        var recording = new RecordingCoordinator(coordinator);
        var collector = Collector(
            new SequenceHandler(Response(HttpStatusCode.OK, new string('x', 128))),
            recording,
            [BarsAdapter()],
            maximumAttempts: 1,
            maximumResponseBytes: 32);

        var result = await collector.CollectAsync(Plan(BarsRequest()));

        Assert.Equal(EvidenceCollectionState.Quarantined, result.State);
        Assert.Contains("32 byte limit", Assert.Single(result.Requests).FailureReason);
        Assert.Empty(recording.Published);
    }

    [Fact]
    public async Task CollectAsync_RejectsChunkedOversizedResponseAtConfiguredBoundary()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(
                Encoding.UTF8.GetBytes(new string('x', 65))))
        };
        response.Content.Headers.ContentLength = null;
        var recording = new RecordingCoordinator(coordinator);
        var collector = Collector(
            new SequenceHandler(response),
            recording,
            [BarsAdapter()],
            maximumAttempts: 1,
            maximumResponseBytes: 64);

        var result = await collector.CollectAsync(Plan(BarsRequest()));

        Assert.Equal(EvidenceCollectionState.Quarantined, result.State);
        Assert.Empty(recording.Published);
    }

    private EvidenceHttpCollector Collector(
        HttpMessageHandler handler,
        IEvidencePublicationCoordinator publication,
        IReadOnlyList<IEvidenceHttpRequestAdapter> adapters,
        int maximumAttempts,
        TimeSpan? timeout = null,
        long maximumResponseBytes = 32L * 1024 * 1024,
        IEvidenceProviderRateBudget? rateBudget = null) =>
        new(
            new HttpClient(handler),
            catalog,
            publication,
            adapters,
            new EvidenceHttpCollectionOptions(
                workerCount: 1,
                boundedCapacity: 2,
                maximumAttempts: maximumAttempts,
                maximumResponseBytes: maximumResponseBytes,
                requestTimeout: timeout ?? TimeSpan.FromSeconds(2),
                initialRetryDelay: TimeSpan.FromMilliseconds(1),
                maximumRetryDelay: TimeSpan.FromMilliseconds(5)),
            new NoDelayScheduler(),
            new FixedTimeProvider(Now),
            rateBudget);

    private static EvidenceCollectionPlan Plan(params EvidenceCollectionRequest[] requests) =>
        new(
            Now,
            "collection-test",
            Hash("config"),
            "commit-collection",
            "raw-http-v1",
            "provider-request-v1",
            requests);

    private static EvidenceCollectionRequest BarsRequest() =>
        new(
            "bars",
            "alpaca",
            "/v2/stocks/bars",
            ["AAPL"],
            Now.AddDays(-2),
            Now.AddDays(-1),
            "sip",
            "raw",
            "USD",
            new DateOnly(2026, 7, 24),
            new Dictionary<string, string> { ["timeframe"] = "1Min" });

    private static HttpResponseMessage Response(
        HttpStatusCode status,
        string content,
        int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content))
        };
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        if (retryAfterSeconds is not null)
        {
            response.Headers.RetryAfter =
                new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(retryAfterSeconds.Value));
        }

        return response;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private async Task<long> SourceObservationCountAsync(string jobId)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(rootPath, "evidence.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM source_observations WHERE job_id = $job;";
        command.Parameters.AddWithValue("$job", jobId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class SequenceHandler(params object[] sequence) : HttpMessageHandler
    {
        private readonly Queue<object> sequence = new(sequence);

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            if (!sequence.TryDequeue(out var next))
            {
                throw new InvalidOperationException("No fake response remains.");
            }

            return next is Exception exception
                ? Task.FromException<HttpResponseMessage>(exception)
                : Task.FromResult((HttpResponseMessage)next);
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class BarrierResponseHandler(
        int participants,
        string content) : HttpMessageHandler
    {
        private readonly Barrier barrier = new(participants);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(5), cancellationToken))
            {
                throw new TimeoutException("Concurrent collectors did not reach the test barrier.");
            }

            return Task.FromResult(Response(HttpStatusCode.OK, content));
        }
    }

    private sealed class ConcurrencyHandler(TimeSpan delay) : HttpMessageHandler
    {
        private int active;
        private int maximum;

        public int MaximumConcurrency => Volatile.Read(ref maximum);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref active);
            while (true)
            {
                var observed = Volatile.Read(ref maximum);
                if (current <= observed ||
                    Interlocked.CompareExchange(ref maximum, current, observed) == observed)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(delay, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("ticker,price\nAAPL,100")
                };
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }

    private sealed class RecordingCoordinator(IEvidencePublicationCoordinator inner)
        : IEvidencePublicationCoordinator
    {
        public List<PublishedReceipt> Published { get; } = [];

        public async Task<EvidenceCatalogCommitResult> PublishSourceObservationAsync(
            EvidenceSourceObservation observation,
            ReadOnlyMemory<byte> rawContent,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.PublishSourceObservationAsync(
                observation,
                rawContent,
                cancellationToken);
            Published.Add(new PublishedReceipt(observation, rawContent.ToArray()));
            return result;
        }

        public Task<EvidencePublicationRecoveryResult> RecoverPendingPublicationsAsync(
            int maximumPublications,
            CancellationToken cancellationToken = default) =>
            inner.RecoverPendingPublicationsAsync(maximumPublications, cancellationToken);
    }

    private sealed class PublishThenThrowCoordinator(IEvidencePublicationCoordinator inner)
        : IEvidencePublicationCoordinator
    {
        private int thrown;

        public async Task<EvidenceCatalogCommitResult> PublishSourceObservationAsync(
            EvidenceSourceObservation observation,
            ReadOnlyMemory<byte> rawContent,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.PublishSourceObservationAsync(
                observation,
                rawContent,
                cancellationToken);
            if (Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw new IOException("Simulated crash after source observation commit.");
            }

            return result;
        }

        public Task<EvidencePublicationRecoveryResult> RecoverPendingPublicationsAsync(
            int maximumPublications,
            CancellationToken cancellationToken = default) =>
            inner.RecoverPendingPublicationsAsync(maximumPublications, cancellationToken);
    }

    private sealed record PublishedReceipt(
        EvidenceSourceObservation Observation,
        byte[] Content);

    private static AlpacaBarsEvidenceRequestAdapter BarsAdapter() =>
        new(new Uri("https://example.test"));

    private sealed class PublicationAssertingAdapter(
        IEvidenceHttpRequestAdapter inner,
        IEvidenceCatalog catalog,
        FileSystemImmutableArtifactStore store) : IEvidenceHttpRequestAdapter
    {
        public int ParseCount { get; private set; }

        public bool CanHandle(EvidenceCollectionRequest request) => inner.CanHandle(request);

        public HttpRequestMessage CreateRequest(
            EvidenceCollectionRequest request,
            string? pageToken) =>
            inner.CreateRequest(request, pageToken);

        public async ValueTask<EvidenceHttpPageMetadata> ReadPublishedPageMetadataAsync(
            EvidenceSourceObservation observation,
            ReadOnlyMemory<byte> rawContent,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(await catalog.GetSourceObservationAsync(
                observation.ObservationId,
                cancellationToken));
            await using var stream = await store.OpenReadAsync(observation.Artifact, cancellationToken);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            Assert.Equal(rawContent.ToArray(), memory.ToArray());
            ParseCount++;
            return await inner.ReadPublishedPageMetadataAsync(
                observation,
                rawContent,
                cancellationToken);
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

    private sealed class RecordingRateBudget : IEvidenceProviderRateBudget
    {
        private int deferred;

        public int WaitCount { get; private set; }

        public bool WaitObservedAfterDeferral { get; private set; }

        public List<TimeSpan> Deferrals { get; } = [];

        public Task WaitAsync(
            string provider,
            string endpoint,
            CancellationToken cancellationToken)
        {
            WaitCount++;
            WaitObservedAfterDeferral |= Volatile.Read(ref deferred) != 0;
            return Task.CompletedTask;
        }

        public void Defer(string provider, string endpoint, TimeSpan delay)
        {
            Deferrals.Add(delay);
            Volatile.Write(ref deferred, 1);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

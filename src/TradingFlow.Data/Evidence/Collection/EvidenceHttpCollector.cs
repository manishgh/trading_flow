using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using TradingFlow.Domain.Research;
using TradingFlow.Engine.Research;

namespace TradingFlow.Data.Evidence.Collection;

/// <summary>
/// Collects provider responses into immutable raw evidence. Requests are bounded and parallel,
/// while pages within one provider request remain sequential so cursor lineage is deterministic.
/// </summary>
public sealed class EvidenceHttpCollector
{
    private readonly HttpClient httpClient;
    private readonly IEvidenceCatalog catalog;
    private readonly IEvidencePublicationCoordinator publicationCoordinator;
    private readonly IReadOnlyList<IEvidenceHttpRequestAdapter> adapters;
    private readonly EvidenceHttpCollectionOptions options;
    private readonly IEvidenceRetryScheduler retryScheduler;
    private readonly IEvidenceProviderRateBudget rateBudget;
    private readonly TimeProvider timeProvider;

    public EvidenceHttpCollector(
        HttpClient httpClient,
        IEvidenceCatalog catalog,
        IEvidencePublicationCoordinator publicationCoordinator,
        IEnumerable<IEvidenceHttpRequestAdapter> adapters,
        EvidenceHttpCollectionOptions? options = null,
        IEvidenceRetryScheduler? retryScheduler = null,
        TimeProvider? timeProvider = null,
        IEvidenceProviderRateBudget? rateBudget = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.publicationCoordinator = publicationCoordinator
            ?? throw new ArgumentNullException(nameof(publicationCoordinator));
        this.adapters = (adapters ?? throw new ArgumentNullException(nameof(adapters))).ToArray();
        if (this.adapters.Count == 0)
        {
            throw new ArgumentException("At least one HTTP evidence adapter is required.", nameof(adapters));
        }

        this.options = options ?? new EvidenceHttpCollectionOptions();
        this.retryScheduler = retryScheduler ?? new EvidenceRetryScheduler(timeProvider);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.rateBudget = rateBudget ?? EvidenceProviderRateBudget.Shared;
    }

    public async Task<EvidenceHttpCollectionResult> CollectAsync(
        EvidenceCollectionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateAdapters(plan);
        await catalog.RegisterCollectionPlanAsync(plan, cancellationToken);
        var checkpoint = await EnsureCollectingCheckpointAsync(plan, cancellationToken);
        if (checkpoint.State != EvidenceCollectionState.Collecting)
        {
            return new EvidenceHttpCollectionResult(plan.JobId, checkpoint.State, []);
        }

        var completed = checkpoint.CompletedRequestIds.ToHashSet(StringComparer.Ordinal);
        var pending = plan.Requests
            .Where(request => !completed.Contains(request.RequestId))
            .OrderBy(request => request.RequestId, StringComparer.Ordinal)
            .ToArray();
        var channel = Channel.CreateBounded<EvidenceCollectionRequest>(
            new BoundedChannelOptions(options.BoundedCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
        var results = new System.Collections.Concurrent.ConcurrentBag<EvidenceHttpRequestResult>();
        using var pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var workers = Enumerable.Range(0, Math.Min(options.WorkerCount, Math.Max(1, pending.Length)))
            .Select(_ => ConsumeAsync(plan, channel.Reader, results, pipelineCancellation.Token))
            .ToArray();
        foreach (var worker in workers)
        {
            _ = worker.ContinueWith(
                _ => pipelineCancellation.Cancel(),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        var producer = ProduceAsync(channel.Writer, pending, pipelineCancellation.Token);

        try
        {
            await Task.WhenAll(workers.Append(producer));
        }
        catch (OperationCanceledException)
        {
            channel.Writer.TryComplete();
            await SaveInterruptedBestEffortAsync(plan, completed);
            throw;
        }
        catch (Exception exception)
        {
            channel.Writer.TryComplete(exception);
            await SaveIncompleteBestEffortAsync(plan, completed, exception);
            throw;
        }

        var ordered = results.OrderBy(result => result.RequestId, StringComparer.Ordinal).ToArray();
        foreach (var result in ordered.Where(result => result.Completed))
        {
            completed.Add(result.RequestId);
        }

        var quarantined = ordered.Where(result => result.Quarantined).ToArray();
        var incomplete = ordered.Where(result => !result.Completed && !result.Quarantined).ToArray();
        var nextState = quarantined.Length > 0
            ? EvidenceCollectionState.Quarantined
            : incomplete.Length > 0
                ? EvidenceCollectionState.Incomplete
                : EvidenceCollectionState.Normalizing;
        var reason = nextState is EvidenceCollectionState.Quarantined or EvidenceCollectionState.Incomplete
            ? String.Join(
                " | ",
                ordered.Where(result => result.FailureReason is not null)
                    .Select(result => $"{result.RequestId}: {result.FailureReason}"))
            : null;
        await catalog.SaveCollectionCheckpointAsync(
            new EvidenceCollectionCheckpoint(
                plan.JobId,
                nextState,
                timeProvider.GetUtcNow(),
                completed.ToArray(),
                reason),
            cancellationToken);
        return new EvidenceHttpCollectionResult(plan.JobId, nextState, ordered);
    }

    private async Task ConsumeAsync(
        EvidenceCollectionPlan plan,
        ChannelReader<EvidenceCollectionRequest> reader,
        System.Collections.Concurrent.ConcurrentBag<EvidenceHttpRequestResult> results,
        CancellationToken cancellationToken)
    {
        await foreach (var request in reader.ReadAllAsync(cancellationToken))
        {
            results.Add(await CollectRequestAsync(plan, request, cancellationToken));
        }
    }

    private static async Task ProduceAsync(
        ChannelWriter<EvidenceCollectionRequest> writer,
        IReadOnlyList<EvidenceCollectionRequest> requests,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var request in requests)
            {
                await writer.WriteAsync(request, cancellationToken);
            }

            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
            throw;
        }
    }

    private async Task<EvidenceHttpRequestResult> CollectRequestAsync(
        EvidenceCollectionPlan plan,
        EvidenceCollectionRequest request,
        CancellationToken cancellationToken)
    {
        var matchingAdapters = adapters.Where(candidate => candidate.CanHandle(request)).ToArray();
        if (matchingAdapters.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one evidence HTTP adapter for '{request.Provider} {request.Endpoint}', " +
                $"found {matchingAdapters.Length}.");
        }

        var adapter = matchingAdapters[0];
        var cursor = await catalog.GetRequestCursorCheckpointAsync(
            plan.JobId,
            request.RequestId,
            cancellationToken);
        if (cursor?.Exhausted == true)
        {
            return new EvidenceHttpRequestResult(
                request.RequestId,
                true,
                false,
                cursor.PageOrdinal,
                cursor.AttemptCount,
                null);
        }

        var pageOrdinal = (cursor?.PageOrdinal ?? 0) + 1;
        var pageToken = cursor?.NextPageToken;
        var consumedTokenHashes = cursor?.ConsumedPageTokenHashes.ToList() ?? [];
        if (pageToken is not null)
        {
            consumedTokenHashes.Add(Hash(pageToken));
        }

        var cumulativeAttempts = cursor?.AttemptCount ?? 0;
        var publishedPages = cursor?.PageOrdinal ?? 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EvidenceSourceObservation? terminalObservation = null;
            ReadOnlyMemory<byte> terminalContent = default;
            for (var attempt = 1; attempt <= options.MaximumAttempts; attempt++)
            {
                cumulativeAttempts++;
                HttpResponseMessage? response = null;
                try
                {
                    await rateBudget.WaitAsync(
                        request.Provider,
                        request.Endpoint,
                        cancellationToken);
                    using var message = adapter.CreateRequest(request, pageToken);
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    requestTimeout.CancelAfter(options.RequestTimeout);
                    response = await httpClient.SendAsync(
                        message,
                        HttpCompletionOption.ResponseHeadersRead,
                        requestTimeout.Token);
                    var rawContent = await ReadBoundedAsync(
                        response.Content,
                        options.MaximumResponseBytes,
                        requestTimeout.Token);
                    var receivedAtUtc = EvidenceTimestamp.ToMicrosecondPrecision(
                        timeProvider.GetUtcNow());
                    var receiptId = Guid.NewGuid().ToString("N");
                    var observation = CreateObservation(
                        plan,
                        request,
                        message.RequestUri!,
                        pageOrdinal,
                        cumulativeAttempts,
                        $"page-{pageOrdinal}",
                        receiptId,
                        receivedAtUtc,
                        response,
                        rawContent);
                    await publicationCoordinator.PublishSourceObservationAsync(
                        observation,
                        rawContent,
                        cancellationToken);

                    // Classification and parsing are deliberately downstream of the durable
                    // raw receipt. Every complete HTTP response is evidence, including errors.
                    var disposition = EvidenceHttpStatusClassifier.Classify(response.StatusCode);
                    if (disposition == EvidenceHttpStatusDisposition.Success)
                    {
                        terminalObservation = observation;
                        terminalContent = rawContent;
                        break;
                    }

                    if (disposition == EvidenceHttpStatusDisposition.Quarantine)
                    {
                        await RegisterQuarantineAsync(
                            observation,
                            "http_non_retryable_status",
                            $"Provider returned HTTP {(int)response.StatusCode}.",
                            retryable: false,
                            cancellationToken);
                        return new EvidenceHttpRequestResult(
                            request.RequestId,
                            false,
                            true,
                            publishedPages,
                            cumulativeAttempts,
                            $"HTTP {(int)response.StatusCode} is not retryable.");
                    }

                    var providerDelay = GetProviderRetryDelay(response.Headers.RetryAfter);
                    if (providerDelay > TimeSpan.Zero)
                    {
                        rateBudget.Defer(request.Provider, request.Endpoint, providerDelay.Value);
                    }

                    if (attempt == options.MaximumAttempts)
                    {
                        return new EvidenceHttpRequestResult(
                            request.RequestId,
                            false,
                            false,
                            publishedPages,
                            cumulativeAttempts,
                            $"HTTP {(int)response.StatusCode} remained retryable after " +
                            $"{options.MaximumAttempts} attempts.");
                    }

                    if (providerDelay is null)
                    {
                        await retryScheduler.DelayAsync(
                            ComputeBackoffDelay(attempt),
                            attempt,
                            cancellationToken);
                    }
                }
                catch (EvidenceResponseTooLargeException exception)
                {
                    return new EvidenceHttpRequestResult(
                        request.RequestId,
                        false,
                        true,
                        publishedPages,
                        cumulativeAttempts,
                        exception.Message);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (attempt == options.MaximumAttempts)
                    {
                        return new EvidenceHttpRequestResult(
                            request.RequestId,
                            false,
                            false,
                            publishedPages,
                            cumulativeAttempts,
                            $"Request timed out after {options.MaximumAttempts} attempts.");
                    }

                    await retryScheduler.DelayAsync(
                        ComputeBackoffDelay(attempt),
                        attempt,
                        cancellationToken);
                }
                catch (HttpRequestException exception)
                {
                    if (attempt == options.MaximumAttempts)
                    {
                        return new EvidenceHttpRequestResult(
                            request.RequestId,
                            false,
                            false,
                            publishedPages,
                            cumulativeAttempts,
                            $"Transport failed after {options.MaximumAttempts} attempts: " +
                            exception.GetType().Name);
                    }

                    await retryScheduler.DelayAsync(
                        ComputeBackoffDelay(attempt),
                        attempt,
                        cancellationToken);
                }
                finally
                {
                    response?.Dispose();
                }
            }

            if (terminalObservation is null)
            {
                throw new InvalidOperationException("HTTP evidence attempt loop ended without an outcome.");
            }

            EvidenceHttpPageMetadata metadata;
            try
            {
                metadata = await adapter.ReadPublishedPageMetadataAsync(
                    terminalObservation,
                    terminalContent,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await RegisterQuarantineAsync(
                    terminalObservation,
                    "response_metadata_invalid",
                    exception.GetType().Name,
                    retryable: false,
                    cancellationToken);
                return new EvidenceHttpRequestResult(
                    request.RequestId,
                    false,
                    true,
                    publishedPages,
                    cumulativeAttempts,
                    "Published response metadata could not be parsed.");
            }

            if (pageOrdinal == options.MaximumPagesPerRequest &&
                metadata.NextPageToken is not null)
            {
                await RegisterQuarantineAsync(
                    terminalObservation,
                    "pagination_page_limit_exceeded",
                    $"Provider returned more than {options.MaximumPagesPerRequest} pages.",
                    retryable: false,
                    cancellationToken);
                return new EvidenceHttpRequestResult(
                    request.RequestId,
                    false,
                    true,
                    publishedPages,
                    cumulativeAttempts,
                    $"Provider pagination exceeded {options.MaximumPagesPerRequest} pages.");
            }

            EvidenceRequestCursorCheckpoint nextCursor;
            try
            {
                nextCursor = new EvidenceRequestCursorCheckpoint(
                    plan.JobId,
                    request.RequestId,
                    pageOrdinal,
                    metadata.NextPageToken,
                    consumedTokenHashes,
                    terminalObservation.ObservationId,
                    cumulativeAttempts,
                    metadata.NextPageToken is null,
                    timeProvider.GetUtcNow());
            }
            catch (ArgumentException)
            {
                await RegisterQuarantineAsync(
                    terminalObservation,
                    "pagination_token_cycle",
                    "Provider returned a previously consumed pagination token.",
                    retryable: false,
                    cancellationToken);
                return new EvidenceHttpRequestResult(
                    request.RequestId,
                    false,
                    true,
                    publishedPages,
                    cumulativeAttempts,
                    "Provider pagination token cycle detected.");
            }

            var committedCursor = await SaveOrAdoptConcurrentCursorAsync(
                nextCursor,
                cancellationToken);
            publishedPages = Math.Max(publishedPages, committedCursor.PageOrdinal);
            if (committedCursor.Exhausted)
            {
                return new EvidenceHttpRequestResult(
                    request.RequestId,
                    true,
                    false,
                    publishedPages,
                    cumulativeAttempts,
                    null);
            }

            pageToken = committedCursor.NextPageToken;
            consumedTokenHashes = committedCursor.ConsumedPageTokenHashes
                .Append(Hash(pageToken!))
                .ToList();
            cumulativeAttempts = Math.Max(cumulativeAttempts, committedCursor.AttemptCount);
            pageOrdinal = committedCursor.PageOrdinal + 1;
        }
    }

    private async Task<EvidenceCollectionCheckpoint> EnsureCollectingCheckpointAsync(
        EvidenceCollectionPlan plan,
        CancellationToken cancellationToken)
    {
        var checkpoint = await catalog.GetCollectionCheckpointAsync(plan.JobId, cancellationToken);
        if (checkpoint is null)
        {
            checkpoint = new EvidenceCollectionCheckpoint(
                plan.JobId,
                EvidenceCollectionState.Planned,
                timeProvider.GetUtcNow());
            await catalog.SaveCollectionCheckpointAsync(checkpoint, cancellationToken);
        }

        if (checkpoint.State is EvidenceCollectionState.Planned or EvidenceCollectionState.Incomplete)
        {
            checkpoint = new EvidenceCollectionCheckpoint(
                plan.JobId,
                EvidenceCollectionState.Collecting,
                timeProvider.GetUtcNow(),
                checkpoint.CompletedRequestIds);
            await catalog.SaveCollectionCheckpointAsync(checkpoint, cancellationToken);
        }

        return checkpoint;
    }

    private void ValidateAdapters(EvidenceCollectionPlan plan)
    {
        foreach (var request in plan.Requests)
        {
            var matching = adapters.Where(adapter => adapter.CanHandle(request)).ToArray();
            if (matching.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Expected one evidence HTTP adapter for '{request.Provider} {request.Endpoint}', " +
                    $"found {matching.Length}.");
            }

            using var validationRequest = matching[0].CreateRequest(request, null);
            if (validationRequest.Method != HttpMethod.Get ||
                validationRequest.RequestUri is null ||
                !validationRequest.RequestUri.IsAbsoluteUri)
            {
                throw new InvalidOperationException(
                    $"Evidence request '{request.RequestId}' did not produce an absolute HTTP GET URI.");
            }
        }
    }

    private EvidenceSourceObservation CreateObservation(
        EvidenceCollectionPlan plan,
        EvidenceCollectionRequest request,
        Uri requestUri,
        int pageOrdinal,
        int cumulativeAttempt,
        string pageIdentity,
        string receiptId,
        DateTimeOffset receivedAtUtc,
        HttpResponseMessage response,
        ReadOnlyMemory<byte> rawContent)
    {
        var contentHash = Convert.ToHexString(SHA256.HashData(rawContent.Span)).ToLowerInvariant();
        var observationId = $"observation-{Hash(
            $"{plan.JobId}|{request.RequestId}|{pageOrdinal}|{receiptId}|{contentHash}")[..24]}";
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return new EvidenceSourceObservation(
            observationId,
            plan.JobId,
            plan.LogicalPlanHash,
            plan.CollectionAttemptHash,
            request.RequestId,
            pageIdentity,
            request.Provider,
            request.Endpoint,
            EvidenceTransportKind.Http,
            (int)response.StatusCode,
            request.Symbols,
            request.RequestedStartUtc,
            request.RequestedEndUtc,
            request.DataFeed,
            request.Adjustment,
            request.Currency,
            request.AsOfDate,
            null,
            null,
            null,
            receivedAtUtc,
            new EvidenceArtifactReference(
                new EvidenceContentAddress(contentHash, rawContent.Length, mediaType),
                new EvidenceObjectNamespace($"raw/{request.Provider}")),
            plan.RunId,
            plan.ConfigHash,
            plan.CodeVersion,
            SafeHeaders(response),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["attempt"] = cumulativeAttempt.ToString(CultureInfo.InvariantCulture),
                ["page_ordinal"] = pageOrdinal.ToString(CultureInfo.InvariantCulture),
                ["receipt_id"] = receiptId,
                ["request_uri_sha256"] = Hash(requestUri.AbsoluteUri)
            });
    }

    private async Task RegisterQuarantineAsync(
        EvidenceSourceObservation observation,
        string reasonCode,
        string diagnostic,
        bool retryable,
        CancellationToken cancellationToken)
    {
        var quarantineId = $"quarantine-{Hash(
            $"{observation.ObservationId}|{reasonCode}|{diagnostic}")[..24]}";
        await catalog.RegisterQuarantineAsync(
            new EvidenceQuarantineRecord(
                quarantineId,
                EvidenceQuarantineScope.Observation,
                reasonCode,
                $"{observation.CollectionJobId}:{observation.IngestionRequestId}:" +
                $"{observation.PageIdentity}",
                new EvidenceRowLineage([observation.ToReference()]),
                observation.Artifact,
                retryable,
                timeProvider.GetUtcNow()),
            cancellationToken);
    }

    private TimeSpan? GetProviderRetryDelay(RetryConditionHeaderValue? retryAfter)
    {
        TimeSpan? providerDelay = retryAfter?.Delta;
        if (providerDelay is null && retryAfter?.Date is { } date)
        {
            providerDelay = date - timeProvider.GetUtcNow();
        }

        if (providerDelay > TimeSpan.Zero)
        {
            return providerDelay;
        }

        return null;
    }

    private TimeSpan ComputeBackoffDelay(int attempt)
    {
        var exponent = Math.Min(attempt - 1, 30);
        var milliseconds = options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, exponent);
        return TimeSpan.FromMilliseconds(Math.Min(
            milliseconds,
            options.MaximumRetryDelay.TotalMilliseconds));
    }

    private async Task<EvidenceRequestCursorCheckpoint> SaveOrAdoptConcurrentCursorAsync(
        EvidenceRequestCursorCheckpoint candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            await catalog.SaveRequestCursorCheckpointAsync(candidate, cancellationToken);
            return candidate;
        }
        catch (InvalidOperationException)
        {
            var existing = await catalog.GetRequestCursorCheckpointAsync(
                candidate.JobId,
                candidate.RequestId,
                cancellationToken);
            if (existing is null || !CanAdoptConcurrentProgress(candidate, existing))
            {
                throw;
            }

            return existing;
        }
    }

    private static bool CanAdoptConcurrentProgress(
        EvidenceRequestCursorCheckpoint candidate,
        EvidenceRequestCursorCheckpoint existing)
    {
        if (existing.PageOrdinal < candidate.PageOrdinal)
        {
            return false;
        }

        if (existing.PageOrdinal == candidate.PageOrdinal)
        {
            return String.Equals(
                       existing.NextPageToken,
                       candidate.NextPageToken,
                       StringComparison.Ordinal) &&
                   existing.ConsumedPageTokenHashes.SequenceEqual(
                       candidate.ConsumedPageTokenHashes) &&
                   existing.Exhausted == candidate.Exhausted;
        }

        if (candidate.Exhausted || candidate.NextPageToken is null)
        {
            return false;
        }

        var consumedIndex = candidate.PageOrdinal - 1;
        return existing.ConsumedPageTokenHashes.Count > consumedIndex &&
               existing.ConsumedPageTokenHashes[consumedIndex].Equals(
                   Hash(candidate.NextPageToken),
                   StringComparison.Ordinal) &&
               existing.ConsumedPageTokenHashes
                   .Take(candidate.ConsumedPageTokenHashes.Count)
                   .SequenceEqual(candidate.ConsumedPageTokenHashes);
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declaredLength &&
            declaredLength > maximumBytes)
        {
            throw new EvidenceResponseTooLargeException(maximumBytes, declaredLength);
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream(
            content.Headers.ContentLength is > 0
                ? checked((int)Math.Min(content.Headers.ContentLength.Value, maximumBytes))
                : 0);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return destination.ToArray();
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new EvidenceResponseTooLargeException(maximumBytes, total);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static IReadOnlyDictionary<string, string> SafeHeaders(HttpResponseMessage response)
    {
        var allowed = new HashSet<string>(
            [
                "content-length",
                "content-type",
                "date",
                "etag",
                "last-modified",
                "retry-after",
                "x-ratelimit-limit",
                "x-ratelimit-remaining",
                "x-ratelimit-reset"
            ],
            StringComparer.OrdinalIgnoreCase);
        return response.Headers
            .Concat(response.Content.Headers)
            .Where(header => allowed.Contains(header.Key))
            .ToDictionary(
                header => header.Key.ToLowerInvariant(),
                header => String.Join(",", header.Value),
                StringComparer.Ordinal);
    }

    private async Task SaveInterruptedBestEffortAsync(
        EvidenceCollectionPlan plan,
        IReadOnlySet<string> completed)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await catalog.SaveCollectionCheckpointAsync(
                new EvidenceCollectionCheckpoint(
                    plan.JobId,
                    EvidenceCollectionState.Incomplete,
                    timeProvider.GetUtcNow(),
                    completed.ToArray(),
                    "Collection was interrupted and may be resumed from committed request checkpoints."),
                timeout.Token);
        }
        catch
        {
            // The caller's cancellation remains the primary outcome.
        }
    }

    private async Task SaveIncompleteBestEffortAsync(
        EvidenceCollectionPlan plan,
        IReadOnlySet<string> completed,
        Exception exception)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await catalog.SaveCollectionCheckpointAsync(
                new EvidenceCollectionCheckpoint(
                    plan.JobId,
                    EvidenceCollectionState.Incomplete,
                    timeProvider.GetUtcNow(),
                    completed.ToArray(),
                    $"Collector failed: {exception.GetType().Name}."),
                timeout.Token);
        }
        catch
        {
            // The original collector failure remains the primary outcome.
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed class EvidenceResponseTooLargeException(
        long maximumBytes,
        long observedBytes) : IOException(
        $"HTTP evidence response exceeded the configured {maximumBytes} byte limit " +
        $"(observed at least {observedBytes} bytes).");
}

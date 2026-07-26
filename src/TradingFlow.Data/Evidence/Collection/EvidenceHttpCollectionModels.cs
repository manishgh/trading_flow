using System.Net;
using System.Collections.Concurrent;
using TradingFlow.Domain.Research;

namespace TradingFlow.Data.Evidence.Collection;

public enum EvidenceHttpStatusDisposition
{
    Success = 1,
    Retryable = 2,
    Quarantine = 3
}

public sealed record EvidenceHttpCollectionOptions
{
    public EvidenceHttpCollectionOptions(
        int workerCount = 4,
        int boundedCapacity = 16,
        int maximumAttempts = 4,
        int maximumPagesPerRequest = 10_000,
        long maximumResponseBytes = 32L * 1024 * 1024,
        TimeSpan? requestTimeout = null,
        TimeSpan? initialRetryDelay = null,
        TimeSpan? maximumRetryDelay = null)
    {
        if (workerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workerCount));
        }

        if (boundedCapacity < workerCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(boundedCapacity),
                "Bounded capacity must be at least the worker count.");
        }

        if (maximumAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        if (maximumPagesPerRequest <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPagesPerRequest));
        }

        if (maximumResponseBytes <= 0 || maximumResponseBytes > Int32.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        }

        WorkerCount = workerCount;
        BoundedCapacity = boundedCapacity;
        MaximumAttempts = maximumAttempts;
        MaximumPagesPerRequest = maximumPagesPerRequest;
        MaximumResponseBytes = maximumResponseBytes;
        RequestTimeout = Positive(requestTimeout ?? TimeSpan.FromSeconds(30), nameof(requestTimeout));
        InitialRetryDelay = Positive(
            initialRetryDelay ?? TimeSpan.FromMilliseconds(250),
            nameof(initialRetryDelay));
        MaximumRetryDelay = Positive(
            maximumRetryDelay ?? TimeSpan.FromSeconds(15),
            nameof(maximumRetryDelay));
        if (MaximumRetryDelay < InitialRetryDelay)
        {
            throw new ArgumentException(
                "Maximum retry delay cannot be less than the initial retry delay.",
                nameof(maximumRetryDelay));
        }
    }

    public int WorkerCount { get; }

    public int BoundedCapacity { get; }

    public int MaximumAttempts { get; }

    public int MaximumPagesPerRequest { get; }

    public long MaximumResponseBytes { get; }

    public TimeSpan RequestTimeout { get; }

    public TimeSpan InitialRetryDelay { get; }

    public TimeSpan MaximumRetryDelay { get; }

    private static TimeSpan Positive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }
}

public interface IEvidenceProviderRateBudget
{
    Task WaitAsync(
        string provider,
        string endpoint,
        CancellationToken cancellationToken);

    void Defer(
        string provider,
        string endpoint,
        TimeSpan delay);
}

/// <summary>
/// Shares provider Retry-After boundaries across all workers owned by one collector.
/// The budget is keyed by provider and endpoint path so unrelated provider surfaces
/// can continue independently.
/// </summary>
public sealed class EvidenceProviderRateBudget(
    TimeProvider? timeProvider = null) : IEvidenceProviderRateBudget
{
    public static EvidenceProviderRateBudget Shared { get; } = new();

    private readonly ConcurrentDictionary<string, DateTimeOffset> blockedUntil =
        new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task WaitAsync(
        string provider,
        string endpoint,
        CancellationToken cancellationToken)
    {
        var key = Key(provider, endpoint);
        while (blockedUntil.TryGetValue(key, out var boundary))
        {
            var delay = boundary - timeProvider.GetUtcNow();
            if (delay <= TimeSpan.Zero)
            {
                blockedUntil.TryRemove(
                    new KeyValuePair<string, DateTimeOffset>(key, boundary));
                return;
            }

            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }

    public void Defer(string provider, string endpoint, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        var candidate = timeProvider.GetUtcNow() + delay;
        blockedUntil.AddOrUpdate(
            Key(provider, endpoint),
            candidate,
            (_, existing) => existing >= candidate ? existing : candidate);
    }

    private static string Key(string provider, string endpoint)
    {
        var normalizedProvider = String.IsNullOrWhiteSpace(provider)
            ? throw new ArgumentException("Provider is required.", nameof(provider))
            : provider.Trim().ToLowerInvariant();
        var normalizedEndpoint = NormalizeEndpoint(endpoint);
        return $"{normalizedProvider}|{normalizedEndpoint}";
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var absolute))
        {
            return absolute.AbsolutePath.ToLowerInvariant();
        }

        var query = endpoint.IndexOf('?', StringComparison.Ordinal);
        return (query < 0 ? endpoint : endpoint[..query]).Trim().ToLowerInvariant();
    }
}

public sealed record EvidenceHttpPageMetadata(string? NextPageToken)
{
    public string? NextPageToken { get; } =
        String.IsNullOrWhiteSpace(NextPageToken) ? null : NextPageToken.Trim();
}

public sealed record EvidenceHttpRequestResult(
    string RequestId,
    bool Completed,
    bool Quarantined,
    int PagesPublished,
    int Attempts,
    string? FailureReason);

public sealed record EvidenceHttpCollectionResult(
    string JobId,
    EvidenceCollectionState State,
    IReadOnlyList<EvidenceHttpRequestResult> Requests);

public interface IEvidenceHttpRequestAdapter
{
    bool CanHandle(EvidenceCollectionRequest request);

    HttpRequestMessage CreateRequest(
        EvidenceCollectionRequest request,
        string? pageToken);

    ValueTask<EvidenceHttpPageMetadata> ReadPublishedPageMetadataAsync(
        EvidenceSourceObservation observation,
        ReadOnlyMemory<byte> rawContent,
        CancellationToken cancellationToken);
}

public interface IEvidenceRetryScheduler
{
    Task DelayAsync(
        TimeSpan delay,
        int attempt,
        CancellationToken cancellationToken);
}

public sealed class EvidenceRetryScheduler(
    TimeProvider? timeProvider = null,
    Random? random = null) : IEvidenceRetryScheduler
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Random random = random ?? Random.Shared;

    public Task DelayAsync(
        TimeSpan delay,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        double jitter;
        lock (random)
        {
            jitter = random.NextDouble();
        }

        // Never shorten a provider Retry-After delay; jitter only spreads retries later.
        var jitterMultiplier = 1.0 + (jitter * 0.2);
        return Task.Delay(
            TimeSpan.FromMilliseconds(delay.TotalMilliseconds * jitterMultiplier),
            timeProvider,
            cancellationToken);
    }
}

public static class EvidenceHttpStatusClassifier
{
    public static EvidenceHttpStatusDisposition Classify(HttpStatusCode statusCode)
    {
        var numeric = (int)statusCode;
        if (numeric is >= 200 and <= 299)
        {
            return EvidenceHttpStatusDisposition.Success;
        }

        if (statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests ||
            numeric is 425 or >= 500 and <= 599)
        {
            return EvidenceHttpStatusDisposition.Retryable;
        }

        return EvidenceHttpStatusDisposition.Quarantine;
    }
}

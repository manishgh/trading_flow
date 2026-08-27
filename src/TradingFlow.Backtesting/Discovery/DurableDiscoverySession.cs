using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Discovery;

namespace TradingFlow.Backtesting.Discovery;

/// <summary>
/// Refreshes independent discovery sources into one durable run-scoped universe.
/// Source failures retain prior evidence until TTL; successful empty captures drop it.
/// </summary>
public sealed class DurableDiscoverySession : ILiveDiscoverySession
{
    private readonly IDiscoveryRepository repository;
    private readonly IReadOnlyList<IDiscoverySource> sources;
    private readonly IDiscoverySubscriptionSink subscriptions;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<DurableDiscoverySession> logger;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> nextRefreshBySource = new(StringComparer.Ordinal);
    private readonly HashSet<string> retainedExposureSymbols = new(StringComparer.Ordinal);
    private HashSet<string>? currentSymbols;
    private bool disposed;

    public DurableDiscoverySession(
        Guid scopeId,
        IDiscoveryRepository repository,
        IReadOnlyList<IDiscoverySource> sources,
        IDiscoverySubscriptionSink? subscriptions,
        TimeProvider timeProvider,
        ILogger<DurableDiscoverySession> logger)
    {
        ScopeId = scopeId != Guid.Empty ? scopeId : throw new ArgumentException("Discovery scope is required.", nameof(scopeId));
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
        this.subscriptions = subscriptions ?? NullDiscoverySubscriptionSink.Instance;
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Guid ScopeId { get; }

    public async Task<DiscoveryUniverseSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow().ToUniversalTime();
            currentSymbols ??= (await repository.GetActiveAsync(ScopeId, now, cancellationToken))
                .Select(member => member.Symbol)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var source in sources)
            {
                var scheduleKey = $"{source.SourceKind}\u001f{source.SourceKey}";
                if (nextRefreshBySource.TryGetValue(scheduleKey, out var nextRefresh) && nextRefresh > now)
                {
                    continue;
                }

                try
                {
                    var capture = await source.CaptureAsync(cancellationToken);
                    var commit = await CommitWithConcurrencyRetryAsync(source, capture, cancellationToken);
                    logger.LogInformation(
                        "Committed discovery source {SourceKind}/{SourceKey} version {SourceVersion}: " +
                        "{AddedCount} ADD, {RetainedCount} RETAIN, {DroppedCount} DROP.",
                        source.SourceKind,
                        source.SourceKey,
                        commit.SourceVersion,
                        commit.Added.Count,
                        commit.Retained.Count,
                        commit.Dropped.Count);
                    nextRefreshBySource[scheduleKey] = now.Add(source.RefreshInterval);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (DiscoveryNoObservationException)
                {
                    logger.LogDebug(
                        "Discovery event source {SourceKind}/{SourceKey} has no new observation; prior evidence keeps its original TTL.",
                        source.SourceKind,
                        source.SourceKey);
                    nextRefreshBySource[scheduleKey] = now.Add(source.RefreshInterval);
                }
                catch (Exception exception)
                {
                    // A failed read is not an empty successful snapshot. Keep the
                    // previous source evidence until its configured expiry.
                    logger.LogWarning(
                        exception,
                        "Discovery source {SourceKind}/{SourceKey} refresh failed; prior evidence remains until TTL.",
                        source.SourceKind,
                        source.SourceKey);
                    nextRefreshBySource[scheduleKey] = now.Add(source.RefreshInterval);
                }
            }

            await repository.ExpireAsync(ScopeId, now, cancellationToken);
            var active = await repository.GetActiveAsync(ScopeId, now, cancellationToken);
            var nextSymbols = active.Select(member => member.Symbol).ToHashSet(StringComparer.Ordinal);
            var added = nextSymbols.Except(currentSymbols, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var dropped = currentSymbols.Except(nextSymbols, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            currentSymbols = nextSymbols;
            var subscribedSymbols = nextSymbols
                .Concat(retainedExposureSymbols)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            await subscriptions.ReplaceScopeAsync(ScopeId, subscribedSymbols, cancellationToken);
            return new DiscoveryUniverseSnapshot(ScopeId, now, active, added, dropped);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public async Task RetainExposureSymbolsAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            retainedExposureSymbols.Clear();
            retainedExposureSymbols.UnionWith(symbols
                .Where(symbol => !String.IsNullOrWhiteSpace(symbol))
                .Select(symbol => symbol.Trim().ToUpperInvariant()));
            var subscribed = (currentSymbols ?? [])
                .Concat(retainedExposureSymbols)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            await subscriptions.ReplaceScopeAsync(ScopeId, subscribed, cancellationToken);
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task<DiscoverySnapshotCommit> CommitWithConcurrencyRetryAsync(
        IDiscoverySource source,
        DiscoveryCapture capture,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var expectedVersion = await repository.GetSourceVersionAsync(
                ScopeId,
                source.SourceKind,
                source.SourceKey,
                cancellationToken);
            try
            {
                return await repository.CommitSnapshotAsync(
                    new DiscoverySnapshotRequest(
                        ScopeId,
                        capture.ObservationId,
                        source.SourceKind,
                        source.SourceKey,
                        source.Horizon,
                        capture.ObservedAtUtc,
                        capture.ObservedAtUtc.Add(source.TimeToLive),
                        expectedVersion,
                        capture.Symbols,
                        source.IsDiagnostic,
                        capture.ProviderTimestampUtc,
                        capture.RawReference),
                    cancellationToken);
            }
            catch (DiscoveryConcurrencyException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        await refreshGate.WaitAsync(CancellationToken.None);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            await subscriptions.RemoveScopeAsync(ScopeId, CancellationToken.None);
        }
        finally
        {
            refreshGate.Release();
        }
    }
}

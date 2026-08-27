namespace TradingFlow.Domain.Discovery;

/// <summary>
/// Sources that may place a symbol into an operational discovery universe.
/// Discovery is evidence only; it never authorizes an order.
/// </summary>
public static class DiscoverySourceKinds
{
    public const string Wishlist = "wishlist";
    public const string Finviz = "finviz";
    public const string News = "news";
    public const string Earnings = "earnings";
    public const string Operator = "operator";
    public const string Alert = "alert";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Wishlist,
        Finviz,
        News,
        Earnings,
        Operator,
        Alert
    };
}

public sealed record DiscoverySymbolObservation(
    string Symbol,
    string MetadataJson = "{}");

/// <summary>
/// One successful point-in-time read from one discovery source. A failed source
/// read is never represented as an empty snapshot because that would incorrectly
/// drop the previous membership.
/// </summary>
public sealed record DiscoverySnapshotRequest(
    Guid ScopeId,
    Guid ObservationId,
    string SourceKind,
    string SourceKey,
    string Horizon,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long ExpectedSourceVersion,
    IReadOnlyCollection<DiscoverySymbolObservation> Symbols,
    bool IsDiagnostic = false,
    DateTimeOffset? ProviderTimestampUtc = null,
    string? RawReference = null);

public sealed record DiscoverySnapshotCommit(
    Guid SnapshotId,
    long SourceVersion,
    bool WasDuplicate,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Retained,
    IReadOnlyList<string> Dropped);

public sealed record DiscoverySourceEvidence(
    string SourceKind,
    string SourceKey,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string MetadataJson,
    Guid LatestSnapshotId,
    long SourceVersion,
    DateTimeOffset? ProviderTimestampUtc,
    bool IsDiagnostic,
    string? RawReference,
    string ContentSha256);

public sealed record ActiveDiscoveryAggregate(
    Guid AggregateId,
    Guid ScopeId,
    string Symbol,
    string Horizon,
    DateTimeOffset FirstDiscoveredAtUtc,
    DateTimeOffset LastObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long Version,
    IReadOnlyList<DiscoverySourceEvidence> Sources);

public interface IDiscoveryRepository
{
    Task<long> GetSourceVersionAsync(
        Guid scopeId,
        string sourceKind,
        string sourceKey,
        CancellationToken cancellationToken = default);

    Task<DiscoverySnapshotCommit> CommitSnapshotAsync(
        DiscoverySnapshotRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActiveDiscoveryAggregate>> GetActiveAsync(
        Guid scopeId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default);

    Task<int> ExpireAsync(
        Guid scopeId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default);
}

public sealed class DiscoveryConcurrencyException(string message) : InvalidOperationException(message);

/// <summary>
/// Event-style discovery sources use this to report that no new observation was
/// available. It is not an empty authoritative snapshot; prior evidence remains
/// active only until its original TTL expires.
/// </summary>
public sealed class DiscoveryNoObservationException(string message) : Exception(message);

public sealed record DiscoveryCapture(
    Guid ObservationId,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyCollection<DiscoverySymbolObservation> Symbols,
    DateTimeOffset? ProviderTimestampUtc = null,
    string? RawReference = null);

public interface IDiscoverySource
{
    string SourceKind { get; }

    string SourceKey { get; }

    string Horizon { get; }

    TimeSpan RefreshInterval { get; }

    TimeSpan TimeToLive { get; }

    bool IsDiagnostic { get; }

    Task<DiscoveryCapture> CaptureAsync(CancellationToken cancellationToken);
}

public sealed record DiscoveryUniverseSnapshot(
    Guid ScopeId,
    DateTimeOffset ResolvedAtUtc,
    IReadOnlyList<ActiveDiscoveryAggregate> Members,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Dropped)
{
    public IReadOnlyList<string> Symbols => Members
        .Select(member => member.Symbol)
        .OrderBy(symbol => symbol, StringComparer.Ordinal)
        .ToArray();
}

/// <summary>
/// Run-scoped discovery boundary consumed by paper/live orchestration. It owns
/// source refresh and durable membership; strategy qualification is deliberately
/// outside this Phase 2 contract.
/// </summary>
public interface ILiveDiscoverySession : IAsyncDisposable
{
    Guid ScopeId { get; }

    Task<DiscoveryUniverseSnapshot> RefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Keeps symbols with broker exposure subscribed even after their discovery
    /// source drops them. The last confirmed broker snapshot remains in force if
    /// a later reconciliation attempt fails.
    /// </summary>
    Task RetainExposureSymbolsAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reference-counted subscription boundary. Discovery supplies desired symbols;
/// the shared stream implementation decides the minimal subscribe/unsubscribe delta.
/// </summary>
public interface IDiscoverySubscriptionSink
{
    Task ReplaceScopeAsync(
        Guid scopeId,
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken);

    Task RemoveScopeAsync(Guid scopeId, CancellationToken cancellationToken);
}

public sealed class NullDiscoverySubscriptionSink : IDiscoverySubscriptionSink
{
    public static NullDiscoverySubscriptionSink Instance { get; } = new();

    private NullDiscoverySubscriptionSink()
    {
    }

    public Task ReplaceScopeAsync(Guid scopeId, IReadOnlyCollection<string> symbols, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task RemoveScopeAsync(Guid scopeId, CancellationToken cancellationToken) => Task.CompletedTask;
}

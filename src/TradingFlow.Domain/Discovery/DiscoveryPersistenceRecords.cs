namespace TradingFlow.Domain.Discovery;

public sealed class DiscoverySnapshotRecord
{
    public Guid SnapshotId { get; set; }
    public Guid ScopeId { get; set; }
    public Guid ObservationId { get; set; }
    public string SourceKind { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public string Horizon { get; set; } = string.Empty;
    public long SourceVersion { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ProviderTimestampUtc { get; set; }
    public bool IsDiagnostic { get; set; }
    public string? RawReference { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public string SymbolsJson { get; set; } = "[]";
}

public sealed class DiscoveryAggregateRecord
{
    public Guid AggregateId { get; set; }
    public Guid ScopeId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Horizon { get; set; } = string.Empty;
    public DateTimeOffset FirstDiscoveredAtUtc { get; set; }
    public DateTimeOffset LastObservedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; }
    public long Version { get; set; }
    public ICollection<DiscoverySourceMembershipRecord> Sources { get; set; } = new List<DiscoverySourceMembershipRecord>();
}

public sealed class DiscoverySourceMembershipRecord
{
    public Guid MembershipId { get; set; }
    public Guid AggregateId { get; set; }
    public string SourceKind { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public Guid LatestSnapshotId { get; set; }
    public DateTimeOffset FirstObservedAtUtc { get; set; }
    public DateTimeOffset LastObservedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; }
    public long Version { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DiscoveryAggregateRecord? Aggregate { get; set; }
    public DiscoverySnapshotRecord? LatestSnapshot { get; set; }
}

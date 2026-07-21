namespace TradingFlow.Domain.Persistence;

public sealed class RiskEventRecord : OperationalRecord
{
    public long RiskEventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string? Symbol { get; set; }
    public decimal? ObservedValue { get; set; }
    public decimal? LimitValue { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string DetailsJson { get; set; } = "{}";
}

public sealed class KillSwitchEventRecord : OperationalRecord
{
    public long KillSwitchEventId { get; set; }
    public string SwitchType { get; set; } = string.Empty;
    public string Transition { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string? Actor { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool FlattenRequested { get; set; }
    public string DetailsJson { get; set; } = "{}";
}

public sealed class ReconciliationRecord : OperationalRecord
{
    public Guid ReconciliationId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public string BrokerSnapshotJson { get; set; } = "{}";
    public string LocalSnapshotJson { get; set; } = "{}";
    public string DiffJson { get; set; } = "{}";
    public bool RequiresAcknowledgement { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
    public string? AcknowledgedBy { get; set; }
}

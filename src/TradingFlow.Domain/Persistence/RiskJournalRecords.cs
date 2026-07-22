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
    public string DiffHash { get; set; } = string.Empty;
    public bool RequiresAcknowledgement { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
    public string? AcknowledgedBy { get; set; }
    public string? AcknowledgementReason { get; set; }
}

/// <summary>
/// Append-only account position state emitted by authoritative broker fills or a proven REST repair.
/// QuantityAfter is signed: positive is long, negative is short, and zero is flat.
/// </summary>
public sealed class PositionEventRecord : OperationalRecord
{
    public long PositionEventId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string StrategyId { get; set; } = string.Empty;
    public string ExecutionStrategyId { get; set; } = string.Empty;
    public decimal QuantityAfter { get; set; }
    public decimal FillQuantity { get; set; }
    public decimal FillPrice { get; set; }
    public string Side { get; set; } = string.Empty;
    public string BrokerOrderId { get; set; } = string.Empty;
    public string ClientOrderId { get; set; } = string.Empty;
    public string ExecutionId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset BrokerTimestampUtc { get; set; }
    public DateTimeOffset LocalTimestampUtc { get; set; }
    public string PayloadJson { get; set; } = "{}";
}

namespace TradingFlow.Domain.Persistence;

public sealed class OrderIntentRecord : OperationalRecord
{
    public Guid IntentId { get; set; }
    public Guid? CandidateId { get; set; }
    public string ClientOrderId { get; set; } = string.Empty;
    public string StrategyId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string OrderType { get; set; } = string.Empty;
    public string TimeInForce { get; set; } = string.Empty;
    public decimal RequestedQuantity { get; set; }
    public decimal? LimitPrice { get; set; }
    public decimal? StopPrice { get; set; }
    public DateOnly SessionDate { get; set; }
    public int SequenceNumber { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string RequestJson { get; set; } = string.Empty;
}

public sealed class OrderEventRecord : OperationalRecord
{
    public long EventId { get; set; }
    public string ClientOrderId { get; set; } = string.Empty;
    public string? BrokerOrderId { get; set; }
    public string? PreviousState { get; set; }
    public string NewState { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset? BrokerTimestampUtc { get; set; }
    public DateTimeOffset LocalTimestampUtc { get; set; }
    public decimal? FilledQuantity { get; set; }
    public decimal? FillPrice { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
}

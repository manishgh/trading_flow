using System;

namespace TradingFlow.Domain.Audit;

public sealed record DecisionAuditRecord
{
    public long Id { get; init; }
    public string RunName { get; init; } = string.Empty;
    public string Ticker { get; init; } = string.Empty;
    public string StrategyName { get; init; } = string.Empty;
    public DateTimeOffset Timestamp { get; init; }
    public string Decision { get; init; } = string.Empty; // "Accepted", "Rejected", or neutral evaluation states such as "NoSignal"
    public string? RejectionReason { get; init; }
    public string SignalJson { get; init; } = string.Empty; // Serialized TradeSignal
}

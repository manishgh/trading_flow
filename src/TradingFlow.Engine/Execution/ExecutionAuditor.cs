using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace TradingFlow.Engine.Execution;

public enum ExecutionState
{
    SignalGenerated,
    BracketOrderSubmitted,
    OrderFilled,
    ExitSubmitted,
    TrailingStopAdjusted,
    PositionClosed,
    OrderRejected,
    OrderStateChanged
}

public sealed record ExecutionEvent(
    string Ticker,
    string StrategyName,
    DateTimeOffset Timestamp,
    ExecutionState State,
    string Message,
    string EvidenceScope,
    string? ReferenceId,
    string? EvidenceJson = null
);

public interface IExecutionEventSink
{
    void Append(ExecutionEvent item);
}

public sealed class AuditPersistenceException(string message, Exception innerException)
    : IOException(message, innerException);

public sealed class ExecutionAuditor
{
    public const int DefaultCapacity = 10_000;

    private readonly ConcurrentQueue<ExecutionEvent> _events = new();
    private readonly int _capacity;
    private readonly IExecutionEventSink? _sink;
    private readonly string _evidenceScope;

    public ExecutionAuditor(
        int capacity = DefaultCapacity,
        IExecutionEventSink? sink = null,
        string evidenceScope = "operational_execution")
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Audit capacity must be positive.");
        }

        _capacity = capacity;
        _sink = sink;
        _evidenceScope = String.IsNullOrWhiteSpace(evidenceScope)
            ? throw new ArgumentException("Evidence scope is required.", nameof(evidenceScope))
            : evidenceScope.Trim();
    }

    public void LogEvent(
        string ticker,
        string strategyName,
        DateTimeOffset timestamp,
        ExecutionState state,
        string message,
        string? referenceId = null,
        string? evidenceJson = null)
    {
        var ev = new ExecutionEvent(
            ticker, strategyName, timestamp, state, message, _evidenceScope, referenceId, evidenceJson);
        // Authoritative retention must succeed before the disposable UI preview changes.
        _sink?.Append(ev);
        _events.Enqueue(ev);
        while (_events.Count > _capacity)
        {
            _events.TryDequeue(out _);
        }
    }

    public IReadOnlyCollection<ExecutionEvent> GetEvents() => _events.ToArray();
}

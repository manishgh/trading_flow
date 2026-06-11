using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace TradingFlow.Engine.Execution;

public enum ExecutionState
{
    SignalGenerated,
    BracketOrderSubmitted,
    OrderFilled,
    TrailingStopAdjusted,
    PositionClosed,
    OrderRejected
}

public sealed record ExecutionEvent(
    string Ticker,
    string StrategyName,
    DateTimeOffset Timestamp,
    ExecutionState State,
    string Message
);

public sealed class ExecutionAuditor
{
    private readonly ConcurrentBag<ExecutionEvent> _events = new();

    public void LogEvent(
        string ticker,
        string strategyName,
        DateTimeOffset timestamp,
        ExecutionState state,
        string message)
    {
        var ev = new ExecutionEvent(ticker, strategyName, timestamp, state, message);
        _events.Add(ev);
    }

    public IReadOnlyCollection<ExecutionEvent> GetEvents() => _events.ToArray();
}

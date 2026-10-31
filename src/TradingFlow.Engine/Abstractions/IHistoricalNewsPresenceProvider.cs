using TradingFlow.Domain.Strategies;

namespace TradingFlow.Engine.Abstractions;

/// <summary>Resolves immutable historical revisions under a separately declared Research assumption.</summary>
public interface IHistoricalNewsPresenceProvider
{
    // Return evidence for this exact decision; missing coverage remains an explicit unknown.
    ValueTask<HistoricalNewsPresenceContext> ResolveAsync(string symbol, DateTimeOffset decisionAtUtc,
        CancellationToken cancellationToken = default);
}

using TradingFlow.Domain.Market;

namespace TradingFlow.Engine.Abstractions;

/// <summary>
/// Read-only live OPRA options context provider for equity Desk displays and optional strategy skew gates.
/// </summary>
public interface IOptionsSentimentSource
{
    /// <summary>
    /// Returns a point-in-time options sentiment summary for <paramref name="symbol"/>, optionally reusing
    /// <paramref name="spotPriceHint"/> and <paramref name="adr20Pct"/> when the caller already observed them.
    /// </summary>
    Task<OptionsSentimentSnapshot> GetSnapshotAsync(
        string symbol,
        decimal? spotPriceHint = null,
        decimal? adr20Pct = null,
        CancellationToken cancellationToken = default);
}

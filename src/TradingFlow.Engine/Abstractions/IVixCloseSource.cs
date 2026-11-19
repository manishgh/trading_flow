using TradingFlow.Engine.Regime;

namespace TradingFlow.Engine.Abstractions;

/// <summary>
/// Live source of published daily VIX closes (paper trading and the Desk read FRED's latest file). A failed read throws;
/// callers refuse the entry as <see cref="VixRegime.LookupFailedReason"/>. Backtests never use this: they read the frozen copy
/// published with their research screen.
/// </summary>
public interface IVixCloseSource
{
    /// <summary>The published closes as currently known, including the most recent sessions.</summary>
    Task<VixCloseSeries> LoadLatestAsync(CancellationToken cancellationToken);
}

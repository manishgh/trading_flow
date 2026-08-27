using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Engine.Configuration;

/// <summary>
/// Prevents a base wishlist profile from being executed before an application
/// workflow freezes its resolved symbol snapshot into the run config.
/// </summary>
public static class RunUniverseValidator
{
    public static void RequireResolved(BacktestRunConfig run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Universe?.IsUnresolvedWishlist == true)
        {
            throw new InvalidOperationException(
                "The selected config is an unresolved wishlist template. Resolve a wishlist and create an immutable run snapshot before execution.");
        }

        if (run.Universe?.IsHistoricalScreener == true)
        {
            return;
        }

        if (run.Universe?.IsResolvedSnapshot != true)
        {
            throw new InvalidOperationException(
                "The run does not declare a resolved universe snapshot. Freeze the selected wishlist symbols and provenance before execution.");
        }

        if (run.Tickers.Count == 0)
        {
            throw new InvalidOperationException(
                "The run has no resolved ticker snapshot. Resolve a wishlist or point-in-time research universe before execution.");
        }
    }
}

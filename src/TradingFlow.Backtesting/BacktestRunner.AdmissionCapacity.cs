using TradingFlow.Domain.Backtesting;

namespace TradingFlow.Backtesting;

// Slot and cash capacity for admitting new entries into the unified daily portfolio. Two opt-in portfolio settings serve a
// monthly ETF rotation: same-open swaps (a sale filling at the open frees its slot and value for that open's purchases) and
// sizing from marked equity. With both off the numbers are exactly the long-standing ones.
public sealed partial class BacktestRunner
{
    /// <summary>
    /// Capacity for entries filling at <paramref name="entryOpen"/>: held positions counted against the slot limit, the
    /// value of one slot, the value available to spend, and the equity the order planner caps a position against.
    /// <paramref name="realizedEquity"/> is starting capital plus closed-trade profit; each held position reserves its
    /// planned cost. Marks use each position's last processed close, which is known before the open.
    /// </summary>
    internal static AdmissionCapacity MeasureAdmissionCapacity(PortfolioConfig portfolio, decimal realizedEquity,
        IReadOnlyList<ActiveBacktestExecution> held, DateTimeOffset entryOpen)
    {
        var releasing = portfolio.SameOpenExitsReleaseCapacity
            ? held.Where(position => SellsAtOpen(position, entryOpen)).ToArray()
            : [];
        var reserved = held.Sum(position => position.ReservedNotional);
        var releasedValue = releasing.Sum(MarkedValue);
        var sizingEquity = portfolio.SizeFromMarkedEquity
            ? realizedEquity + held.Sum(position => MarkedValue(position) - position.ReservedNotional)
            : realizedEquity;
        return new AdmissionCapacity(
            held.Count - releasing.Length,
            sizingEquity / portfolio.MaxConcurrentPositions,
            Math.Max(0m, realizedEquity - reserved + releasedValue),
            sizingEquity);
    }

    /// <summary>
    /// With same-open swaps on, a purchase spends the sale value of a fund sold at that open (counted at its last close).
    /// If a sale filled only in part, or the open was far below the last close, cash would go negative unnoticed: the daily
    /// book has no cash check. Fails the run on any session ending with negative cash instead of publishing such a result.
    /// Returns the state unchanged otherwise, and always when the setting is off.
    /// </summary>
    private static BacktestPortfolioState RequireSwapsKeptCashCovered(PortfolioConfig portfolio, BacktestPortfolioState state)
    {
        if (!portfolio.SameOpenExitsReleaseCapacity) return state;
        var overdrawn = state.Sessions.FirstOrDefault(session => session.Cash < 0m);
        if (overdrawn is not null)
            throw new InvalidOperationException(
                $"Same-open swaps overdrew cash: the session {overdrawn.SessionDate:yyyy-MM-dd} ended with cash {overdrawn.Cash:F2}. " +
                "A released sale filled short or far below its last close; the result is not published.");
        return state;
    }

    // A held position whose planned sale is an open fill at this same open (a next-open technical exit, not a stop or target
    // touched inside a bar, and not a position still open at the end of the test).
    private static bool SellsAtOpen(ActiveBacktestExecution position, DateTimeOffset open) =>
        position.PlannedExitTimestamp == open && position.PlannedExitReason is { } reason &&
        !IsPriceTriggeredExitReason(reason) && reason != "open_at_study_end";

    // The position at its last processed close, or at its planned cost before any close of it has been processed.
    private static decimal MarkedValue(ActiveBacktestExecution position) =>
        position.LastClose is { } close ? position.RequestedQuantity * close : position.ReservedNotional;
}

/// <summary>Admission capacity for one open: slots in use, one slot's value, value available to spend, sizing equity.</summary>
internal sealed record AdmissionCapacity(int HeldCount, decimal SlotValue, decimal AvailableValue, decimal SizingEquity);

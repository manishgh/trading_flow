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
    /// planned cost. Marks use each position's last processed close, which is known before the open. With same-open swaps
    /// on, the share of sizing equity the slots leave unused (100% minus slots x position cap, 2.5% for 3 x 32.5%) stays
    /// in cash: a sale fills at the open, not at the last close its value was counted at, and slippage applies.
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
        var cashKept = portfolio.SameOpenExitsReleaseCapacity
            ? sizingEquity * Math.Max(0m, 1m - portfolio.MaxConcurrentPositions * portfolio.MaxPositionNotionalPct / 100m)
            : 0m;
        return new AdmissionCapacity(
            held.Count - releasing.Length,
            sizingEquity / portfolio.MaxConcurrentPositions,
            Math.Max(0m, realizedEquity - reserved + releasedValue - cashKept),
            sizingEquity);
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

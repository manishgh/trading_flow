namespace TradingFlow.Engine.Execution;

public enum EntryBarExitKind
{
    None,
    StopLoss,
    TakeProfit
}

public sealed record EntryBarExitResolution(
    EntryBarExitKind Kind,
    decimal? ExitPrice,
    bool WasAmbiguous);

/// <summary>
/// Resolves stop and target touches on the bar that contains an entry fill.
/// OHLC data cannot reveal which level traded first when both were touched, so
/// ambiguous bars fail closed to the stop instead of manufacturing a profit.
/// </summary>
public static class EntryBarExitResolver
{
    public static EntryBarExitResolution Resolve(
        string direction,
        decimal barHigh,
        decimal barLow,
        decimal stopLossPrice,
        decimal takeProfitPrice)
    {
        var isShort = direction.Equals("short", StringComparison.OrdinalIgnoreCase);
        var stopTouched = isShort
            ? barHigh >= stopLossPrice
            : barLow <= stopLossPrice;
        var targetTouched = isShort
            ? barLow <= takeProfitPrice
            : barHigh >= takeProfitPrice;

        if (stopTouched)
        {
            return new EntryBarExitResolution(
                EntryBarExitKind.StopLoss,
                stopLossPrice,
                targetTouched);
        }

        return targetTouched
            ? new EntryBarExitResolution(EntryBarExitKind.TakeProfit, takeProfitPrice, false)
            : new EntryBarExitResolution(EntryBarExitKind.None, null, false);
    }
}

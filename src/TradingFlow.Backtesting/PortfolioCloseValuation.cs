namespace TradingFlow.Backtesting;

public sealed record PortfolioValuationFill(string Symbol, DateTimeOffset Timestamp, bool IsBuy,
    decimal Quantity, decimal Price, decimal Fees);
public sealed record PortfolioCloseMark(string Symbol, DateTimeOffset Timestamp, decimal Price);
public sealed record PortfolioCloseObservation(DateTimeOffset Timestamp, decimal Cash, decimal PositionValue,
    decimal Equity, decimal GrossExposurePct, int OpenSymbolCount);
public sealed record PortfolioCloseValuationResult(decimal EndingEquity, decimal MaxCloseDrawdownPct,
    IReadOnlyList<PortfolioCloseObservation> Observations);

/// <summary>Values recorded fills at aligned completed-slice closes. This does not estimate intrabar losses.</summary>
public static class PortfolioCloseValuation
{
    public static PortfolioCloseValuationResult Analyze(decimal startingCash,
        IReadOnlyList<PortfolioValuationFill> fills, IReadOnlyList<PortfolioCloseMark> marks,
        IReadOnlyList<DateTimeOffset>? expectedValuationTimes = null)
    {
        if (startingCash <= 0) throw new ArgumentOutOfRangeException(nameof(startingCash));
        foreach (var fill in fills)
            if (String.IsNullOrWhiteSpace(fill.Symbol) || fill.Timestamp == default || fill.Timestamp.Offset != TimeSpan.Zero ||
                fill.Quantity <= 0 || fill.Price <= 0 || fill.Fees < 0)
                throw new ArgumentException("Fill requires symbol, UTC time, positive quantity/price and nonnegative fees.");
        foreach (var mark in marks)
            if (String.IsNullOrWhiteSpace(mark.Symbol) || mark.Timestamp == default || mark.Timestamp.Offset != TimeSpan.Zero || mark.Price <= 0)
                throw new ArgumentException("Close mark requires symbol, UTC time and positive price.");
        var prices = marks.GroupBy(mark => (Symbol: mark.Symbol.Trim().ToUpperInvariant(), mark.Timestamp))
            .ToDictionary(group => group.Key, group => group.Select(mark => mark.Price).Distinct().Single());
        if (expectedValuationTimes?.Any(time => time == default || time.Offset != TimeSpan.Zero) == true)
            throw new ArgumentException("Expected valuation grid requires UTC times.");
        var times = marks.Select(mark => mark.Timestamp).Concat(fills.Select(fill => fill.Timestamp))
            .Concat(expectedValuationTimes ?? []).Distinct().Order().ToArray();
        var orderedFills = fills.OrderBy(fill => fill.Timestamp).ToArray();
        var positions = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var observations = new List<PortfolioCloseObservation>();
        var cash = startingCash; var highWater = startingCash; var drawdown = 0m; var index = 0;
        foreach (var time in times)
        {
            while (index < orderedFills.Length && orderedFills[index].Timestamp <= time)
            {
                var fill = orderedFills[index++]; var symbol = fill.Symbol.Trim().ToUpperInvariant();
                var signed = fill.IsBuy ? fill.Quantity : -fill.Quantity;
                cash -= signed * fill.Price + fill.Fees;
                positions[symbol] = positions.GetValueOrDefault(symbol) + signed;
            }
            var value = 0m; var gross = 0m; var open = 0;
            foreach (var position in positions.Where(position => position.Value != 0))
            {
                if (!prices.TryGetValue((position.Key, time), out var price))
                    throw new InvalidOperationException($"Missing aligned close for open {position.Key} at {time:O}.");
                var marked = position.Value * price; value += marked; gross += Math.Abs(marked); open++;
            }
            var equity = cash + value; highWater = Math.Max(highWater, equity);
            drawdown = Math.Max(drawdown, (highWater - equity) / highWater * 100m);
            observations.Add(new(time, cash, value, equity, equity > 0 ? gross / equity * 100m : 0, open));
        }
        return new(observations.LastOrDefault()?.Equity ?? startingCash, drawdown, observations);
    }
}

using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Backtesting.Research;

public sealed class SwingResearchAnalyzer
{
    private readonly IndicatorEngine _indicatorEngine = new();

    public SwingResearchReport Analyze(
        BacktestResult result,
        string strategyName,
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> dailyBarsByTicker,
        SwingResearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyName);
        ArgumentNullException.ThrowIfNull(dailyBarsByTicker);

        options ??= new SwingResearchOptions();
        var strategy = result.StrategyResults.FirstOrDefault(x =>
            x.StrategyName.Equals(strategyName, StringComparison.OrdinalIgnoreCase));

        if (strategy is null)
        {
            throw new ArgumentException($"Strategy '{strategyName}' was not found in result '{result.RunName}'.", nameof(strategyName));
        }

        var trades = strategy.CompletedTrades.OrderBy(x => x.EntryTimestamp).ToArray();
        var normalizedBars = dailyBarsByTicker
            .ToDictionary(
                x => x.Key.ToUpperInvariant(),
                x => x.Value.OrderBy(bar => bar.Timestamp).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var snapshots = normalizedBars.ToDictionary(
            x => x.Key,
            x => _indicatorEngine.Compute(x.Value),
            StringComparer.OrdinalIgnoreCase);

        var weeklyEquity = BuildWeeklyEquity(result.StartingCapital, strategy.EndingCapital, trades, normalizedBars);
        var tickers = normalizedBars.Keys
            .Union(trades.Select(x => x.Ticker), StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .Select(ticker => BuildTickerResearch(
                ticker,
                normalizedBars.GetValueOrDefault(ticker, Array.Empty<OhlcvBar>()),
                snapshots.GetValueOrDefault(ticker, Array.Empty<IndicatorSnapshot>()),
                trades.Where(x => x.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase)).ToArray(),
                options))
            .ToArray();

        return new SwingResearchReport(
            result.RunName,
            strategy.StrategyName,
            strategy.StartingCapital,
            strategy.EndingCapital,
            strategy.TotalReturnPct,
            weeklyEquity,
            tickers);
    }

    private static SwingTickerResearch BuildTickerResearch(
        string ticker,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        IReadOnlyList<BacktestTrade> trades,
        SwingResearchOptions options)
    {
        var tradeDiagnostics = trades
            .OrderBy(x => x.EntryTimestamp)
            .Select(trade => BuildTradeDiagnostic(trade, bars, snapshots))
            .ToArray();
        decimal? buyAndHoldReturn = bars.Count < 2 ? null : PercentChange(bars[0].Close, bars[^1].Close);
        var longOpportunities = FindOpportunities(ticker, "long", bars, snapshots, trades, options);
        var shortOpportunities = FindOpportunities(ticker, "short", bars, snapshots, trades, options);

        return new SwingTickerResearch(
            ticker,
            buyAndHoldReturn is null ? null : Decimal.Round(buyAndHoldReturn.Value, 4),
            tradeDiagnostics,
            longOpportunities,
            shortOpportunities);
    }

    private static SwingTradeDiagnostic BuildTradeDiagnostic(
        BacktestTrade trade,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots)
    {
        var entryIndex = FindIndexAtOrAfter(bars, trade.EntryTimestamp);
        var exitIndex = FindIndexAtOrBefore(bars, trade.ExitTimestamp);
        if (entryIndex < 0)
        {
            entryIndex = FindNearestIndex(bars, trade.EntryTimestamp);
        }

        if (exitIndex < 0)
        {
            exitIndex = FindNearestIndex(bars, trade.ExitTimestamp);
        }

        var tradeBars = entryIndex >= 0 && exitIndex >= entryIndex
            ? bars.Skip(entryIndex).Take(exitIndex - entryIndex + 1).ToArray()
            : Array.Empty<OhlcvBar>();
        var isShort = trade.Direction.Equals("short", StringComparison.OrdinalIgnoreCase);
        var maxFavorable = 0m;
        var maxAdverse = 0m;

        foreach (var bar in tradeBars)
        {
            var highMove = PercentChange(trade.EntryPrice, bar.High);
            var lowMove = PercentChange(trade.EntryPrice, bar.Low);
            if (isShort)
            {
                maxFavorable = Math.Max(maxFavorable, -lowMove);
                maxAdverse = Math.Min(maxAdverse, -highMove);
            }
            else
            {
                maxFavorable = Math.Max(maxFavorable, highMove);
                maxAdverse = Math.Min(maxAdverse, lowMove);
            }
        }

        return new SwingTradeDiagnostic(
            trade.Ticker,
            trade.Direction,
            trade.EntryTimestamp,
            trade.ExitTimestamp,
            trade.EntryPrice,
            trade.ExitPrice,
            trade.NetProfit,
            Decimal.Round(isShort ? PercentChange(trade.ExitPrice, trade.EntryPrice) : PercentChange(trade.EntryPrice, trade.ExitPrice), 4),
            trade.ExitReason,
            Decimal.Round(maxFavorable, 4),
            Decimal.Round(maxAdverse, 4),
            entryIndex >= 0 ? ToContext(bars[entryIndex], snapshots.ElementAtOrDefault(entryIndex)) : null,
            exitIndex >= 0 ? ToContext(bars[exitIndex], snapshots.ElementAtOrDefault(exitIndex)) : null);
    }

    private static IReadOnlyList<SwingOpportunityDiagnostic> FindOpportunities(
        string ticker,
        string direction,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        IReadOnlyList<BacktestTrade> trades,
        SwingResearchOptions options)
    {
        if (bars.Count < 2)
        {
            return Array.Empty<SwingOpportunityDiagnostic>();
        }

        var candidates = new List<OpportunityCandidate>();
        for (var start = 0; start < bars.Count - 1; start++)
        {
            var last = Math.Min(bars.Count - 1, start + Math.Max(1, options.MaximumOpportunityHoldingBars));
            var bestIndex = -1;
            var bestMove = Decimal.MinValue;

            for (var end = start + 1; end <= last; end++)
            {
                var move = direction.Equals("short", StringComparison.OrdinalIgnoreCase)
                    ? PercentChange(bars[end].Close, bars[start].Close)
                    : PercentChange(bars[start].Close, bars[end].Close);
                if (move > bestMove)
                {
                    bestMove = move;
                    bestIndex = end;
                }
            }

            if (bestIndex > start && bestMove >= options.MinimumOpportunityMovePct)
            {
                candidates.Add(new OpportunityCandidate(start, bestIndex, bestMove));
            }
        }

        var selected = SelectNonOverlapping(candidates, options.MaximumOpportunitiesPerDirectionPerTicker);
        return selected
            .OrderBy(x => x.StartIndex)
            .Select(x => BuildOpportunity(ticker, direction, bars, snapshots, trades, x))
            .ToArray();
    }

    private static IReadOnlyList<OpportunityCandidate> SelectNonOverlapping(
        IReadOnlyList<OpportunityCandidate> candidates,
        int maximumCount)
    {
        var selected = new List<OpportunityCandidate>();
        foreach (var candidate in candidates.OrderByDescending(x => x.MovePct))
        {
            if (selected.Any(existing => candidate.StartIndex <= existing.EndIndex && candidate.EndIndex >= existing.StartIndex))
            {
                continue;
            }

            selected.Add(candidate);
            if (selected.Count >= Math.Max(1, maximumCount))
            {
                break;
            }
        }

        return selected;
    }

    private static SwingOpportunityDiagnostic BuildOpportunity(
        string ticker,
        string direction,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        IReadOnlyList<BacktestTrade> trades,
        OpportunityCandidate candidate)
    {
        var startBar = bars[candidate.StartIndex];
        var endBar = bars[candidate.EndIndex];
        var matchingTrades = trades
            .Where(trade => trade.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase))
            .Where(trade => trade.EntryTimestamp >= startBar.Timestamp && trade.EntryTimestamp <= endBar.Timestamp)
            .OrderBy(trade => trade.EntryTimestamp)
            .ToArray();
        var firstTrade = matchingTrades.FirstOrDefault();
        var capturedMove = firstTrade is null
            ? null
            : direction.Equals("short", StringComparison.OrdinalIgnoreCase)
                ? (decimal?)PercentChange(firstTrade.ExitPrice, firstTrade.EntryPrice)
                : PercentChange(firstTrade.EntryPrice, firstTrade.ExitPrice);
        var entryLag = firstTrade is null ? null : (int?)Math.Max(0, FindIndexAtOrAfter(bars, firstTrade.EntryTimestamp) - candidate.StartIndex);
        var classification = firstTrade is null
            ? "missed"
            : capturedMove >= candidate.MovePct * 0.5m
                ? "captured"
                : "under_captured";

        return new SwingOpportunityDiagnostic(
            ticker,
            direction,
            startBar.Timestamp,
            endBar.Timestamp,
            startBar.Close,
            endBar.Close,
            Decimal.Round(candidate.MovePct, 4),
            firstTrade is not null,
            entryLag,
            capturedMove is null ? null : Decimal.Round(capturedMove.Value, 4),
            classification,
            ToContext(startBar, snapshots.ElementAtOrDefault(candidate.StartIndex)),
            ToContext(endBar, snapshots.ElementAtOrDefault(candidate.EndIndex)));
    }

    private static IReadOnlyList<SwingWeeklyEquity> BuildWeeklyEquity(
        decimal startingCapital,
        decimal endingCapital,
        IReadOnlyList<BacktestTrade> trades,
        IReadOnlyDictionary<string, OhlcvBar[]> barsByTicker)
    {
        if (trades.Count == 0)
        {
            return Array.Empty<SwingWeeklyEquity>();
        }

        var firstEntry = trades.Min(x => x.EntryTimestamp.UtcDateTime.Date);
        var lastExit = trades.Max(x => x.ExitTimestamp.UtcDateTime.Date);
        var tradingDays = barsByTicker.Values
            .SelectMany(x => x)
            .Select(x => x.Timestamp.UtcDateTime.Date)
            .Where(day => day >= firstEntry && day <= lastExit)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        var dailyEquity = tradingDays
            .Select(day => new
            {
                Day = day,
                Equity = CalculateEquityForDay(startingCapital, trades, barsByTicker, day)
            })
            .ToArray();
        var previousEquity = startingCapital;
        var output = new List<SwingWeeklyEquity>();

        foreach (var week in dailyEquity.GroupBy(x => WeekStart(x.Day)).OrderBy(x => x.Key))
        {
            var last = week.OrderBy(x => x.Day).Last();
            var closedTrades = trades
                .Where(trade => WeekStart(trade.ExitTimestamp.UtcDateTime.Date) == week.Key)
                .ToArray();
            var gain = last.Equity - previousEquity;
            output.Add(new SwingWeeklyEquity(
                DateOnly.FromDateTime(week.Key),
                DateOnly.FromDateTime(week.Key.AddDays(4)),
                Decimal.Round(last.Equity, 4),
                Decimal.Round(gain, 4),
                Decimal.Round((gain / startingCapital) * 100m, 4),
                closedTrades.Length,
                closedTrades.Select(x => x.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray()));
            previousEquity = last.Equity;
        }

        if (output.Count > 0 && output[^1].WeekEndEquity != endingCapital)
        {
            var last = output[^1];
            output[^1] = last with
            {
                WeekEndEquity = Decimal.Round(endingCapital, 4),
                NetProfit = Decimal.Round(last.NetProfit + (endingCapital - last.WeekEndEquity), 4),
                GainPct = Decimal.Round(((last.NetProfit + (endingCapital - last.WeekEndEquity)) / startingCapital) * 100m, 4)
            };
        }

        return output;
    }

    private static decimal CalculateEquityForDay(
        decimal startingCapital,
        IReadOnlyList<BacktestTrade> trades,
        IReadOnlyDictionary<string, OhlcvBar[]> barsByTicker,
        DateTime day)
    {
        var equity = startingCapital;
        foreach (var trade in trades)
        {
            var entry = trade.EntryTimestamp.UtcDateTime.Date;
            var exit = trade.ExitTimestamp.UtcDateTime.Date;
            if (exit <= day)
            {
                equity += trade.NetProfit;
                continue;
            }

            if (entry > day || exit <= day)
            {
                continue;
            }

            if (!barsByTicker.TryGetValue(trade.Ticker, out var bars))
            {
                continue;
            }

            var close = bars.LastOrDefault(x => x.Timestamp.UtcDateTime.Date <= day)?.Close;
            if (close is null)
            {
                continue;
            }

            equity += trade.Direction.Equals("short", StringComparison.OrdinalIgnoreCase)
                ? (trade.EntryPrice - close.Value) * trade.ShareQuantity
                : (close.Value - trade.EntryPrice) * trade.ShareQuantity;
        }

        return equity;
    }

    private static DateTime WeekStart(DateTime day)
    {
        var offset = ((int)day.DayOfWeek + 6) % 7;
        return day.Date.AddDays(-offset);
    }

    private static int FindIndexAtOrAfter(IReadOnlyList<OhlcvBar> bars, DateTimeOffset timestamp)
    {
        for (var i = 0; i < bars.Count; i++)
        {
            if (bars[i].Timestamp >= timestamp)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindIndexAtOrBefore(IReadOnlyList<OhlcvBar> bars, DateTimeOffset timestamp)
    {
        for (var i = bars.Count - 1; i >= 0; i--)
        {
            if (bars[i].Timestamp <= timestamp)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindNearestIndex(IReadOnlyList<OhlcvBar> bars, DateTimeOffset timestamp)
    {
        if (bars.Count == 0)
        {
            return -1;
        }

        var bestIndex = 0;
        var bestDistance = Duration(bars[0].Timestamp - timestamp);
        for (var i = 1; i < bars.Count; i++)
        {
            var distance = Duration(bars[i].Timestamp - timestamp);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static TimeSpan Duration(TimeSpan value) => value < TimeSpan.Zero ? -value : value;

    private static SwingIndicatorContext ToContext(OhlcvBar bar, IndicatorSnapshot? snapshot)
    {
        return new SwingIndicatorContext(
            bar.Timestamp,
            bar.Close,
            bar.Volume,
            Round(snapshot?.Rsi),
            Round(snapshot?.Atr),
            Round(snapshot?.MacdHistogram),
            Round(snapshot?.Sma10),
            Round(snapshot?.Sma20),
            Round(snapshot?.Sma50),
            Round(snapshot?.Ema20),
            Round(snapshot?.Vwap),
            Round(snapshot?.BollingerUpper),
            Round(snapshot?.BollingerLower),
            Round(snapshot?.RelativeVolume));
    }

    private static decimal? Round(decimal? value) => value is null ? null : Decimal.Round(value.Value, 4);

    private static decimal PercentChange(decimal start, decimal end)
    {
        return start == 0 ? 0 : ((end - start) / start) * 100m;
    }

    private sealed record OpportunityCandidate(int StartIndex, int EndIndex, decimal MovePct);
}

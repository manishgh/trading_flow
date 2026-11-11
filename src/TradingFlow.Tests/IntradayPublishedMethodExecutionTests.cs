using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Tests;

// Opt-in execution rules of the published opening-range method (design docs/design-review/2026-11-02/orb-stocks-in-play-design.md,
// component 4): per-share commission, a gross exposure limit applied at each stop order's trigger, the stop measured from
// the actual fill, and the exit at the official close. Uses the shared synthetic session of IntradayPortfolioExecutionTests.
public sealed partial class IntradayPortfolioExecutionTests
{
    // $0.0035 per share is charged on both the entry and the exit; nothing else changes.
    [Fact]
    public void CommissionPerShare_IsChargedOnBothSides()
    {
        var withCommission = Assert.Single(Run(Bars(), [StopCandidate()], Portfolio() with { CommissionPerShare = 0.0035m }).Trades);
        var without = Assert.Single(Run(Bars(), [StopCandidate()]).Trades);

        Assert.Equal(without.ShareQuantity, withCommission.ShareQuantity);
        Assert.Equal(without.Fees + withCommission.ShareQuantity * 0.0035m * 2, withCommission.Fees);
        Assert.Equal(without.ExitPrice, withCommission.ExitPrice);
    }

    // Untriggered stop orders reserve nothing; the first order to trigger takes its full size and the next one only what is
    // left under the gross limit (40% of $10,000 less the first $2,400 buys 15 shares), or nothing at all (25%).
    [Theory]
    [InlineData(40, 15)]
    [InlineData(25, 0)]
    public void GrossExposureLimit_SizesEachStopOrderAtItsTrigger(int grossPct, int secondShares)
    {
        var bars = Bars().Concat(Bars().Select(bar => bar with { Ticker = "Y" })).ToArray();
        var second = StopCandidate("second") with { Ticker = "Y" };
        var result = Run(bars, [StopCandidate(), second], Portfolio() with { MaxGrossExposurePct = grossPct });

        // 24 shares: the 25% position cap ($2,500) less the planned slippage on a $100 trigger.
        Assert.Equal(24, result.Trades.Single(trade => trade.Ticker == "X").ShareQuantity);
        if (secondShares == 0)
        {
            Assert.DoesNotContain(result.Trades, trade => trade.Ticker == "Y");
            Assert.Contains(result.Events, item => item.Ticker == "Y" && item.Reason == "gross_exposure_limit_reached_at_trigger");
        }
        else
        {
            Assert.Equal(secondShares, result.Trades.Single(trade => trade.Ticker == "Y").ShareQuantity);
            Assert.Contains(result.Events, item => item.Ticker == "Y" && item.State == "quantity_capped");
        }
    }

    // A long stop order gaps open above its trigger (100) at 100.50. Measured from the fill, the stop moves from 99 to 99.50
    // and a later low of 99.40 exits there; measured from the trigger the stop is never reached.
    [Theory]
    [InlineData(true, "stop_loss", 99.5)]
    [InlineData(false, "session_deadline", 100)]
    public void StopFromFill_KeepsThePlannedDistanceFromTheActualFill(bool fromFill, string reason, double exitPrice)
    {
        var bars = Enumerable.Range(0, 9).Select(i => Bar(i * 5, 100, 100.2m, 99.7m, 100)).ToArray();
        bars[3] = Bar(15, 100.5m, 100.6m, 100.4m, 100.5m);
        bars[4] = Bar(20, 100.5m, 100.6m, 99.6m, 100m);
        bars[5] = Bar(25, 100m, 100.1m, 99.4m, 99.6m);
        var strategy = RecipeStrategy(new(OpeningRangeBreakoutRules.PublishedRecipePolicyId, 1, 5, 14, 1m, 14, StopFromFill: fromFill));

        var trade = Assert.Single(Replay(strategy, bars, [StopCandidate()], null).Trades);

        Assert.Equal(reason, trade.ExitReason.Split(';')[0]);
        Assert.Equal((decimal)exitPrice, decimal.Round(trade.ExitPrice, 4));
        Assert.Equal(fromFill ? 99.5m : 99m, trade.StopLossPrice);
    }

    // With the close exit the order runs to 16:00 (here the synthetic 10:15 close) and the position leaves at the session's
    // official closing price; without that price the exposure stays unresolved instead of inventing an exit.
    [Fact]
    public void ExitAtSessionClose_UsesTheOfficialClosingPrice()
    {
        var strategy = RecipeStrategy(new(OpeningRangeBreakoutRules.PublishedRecipePolicyId, 1, 5, 14, 1m, 14, ExitAtSessionClose: true));
        var candidate = StopCandidate() with
        {
            IntradayExecution = StopCandidate().IntradayExecution! with { ExpiresAtUtc = Start.AddMinutes(45), SessionDeadlineUtc = Start.AddMinutes(45) }
        };
        var closes = new Dictionary<(string, DateOnly), decimal> { [("X", Day)] = 100.7m };

        var trade = Assert.Single(Replay(strategy, Bars(), [candidate], closes).Trades);
        var missing = Replay(strategy, Bars(), [candidate], new Dictionary<(string, DateOnly), decimal>());

        Assert.Equal(Start.AddMinutes(45), trade.ExitTimestamp);
        Assert.Equal("session_close", trade.ExitReason.Split(';')[0]);
        Assert.Equal(100.7m, decimal.Round(trade.ExitPrice, 4));
        Assert.Contains(missing.Failures, failure => failure.Reason == "session_close_price_missing");
    }

    // A long stop order (trigger 100, stop 99) fills inside the 10:15 bar whose low (99.00) touches the stop. One-minute bars
    // decide the order: the low before the entry minute leaves the position open; a later low is a known stop-out; the same
    // minute stays unknown and counts as a loss; without minute bars the five-minute worst case stands.
    [Theory]
    [InlineData("low_before_entry", "session_deadline")]
    [InlineData("low_after_entry", "stop_loss")]
    [InlineData("same_minute", "stop_loss_entry_minute_order_unknown")]
    [InlineData("no_minutes", "stop_loss_entry_bar_order_unknown")]
    public void MinuteBars_OrderAnEntryAndAStopInsideOneFiveMinuteBar(string scenario, string reason)
    {
        var bars = Bars();
        bars[3] = Bar(15, 99.9m, 100.3m, 99.0m, 100m);
        OhlcvBar Minute(int minute, decimal open, decimal high, decimal low, decimal close) =>
            new("X", Start.AddMinutes(15 + minute), "1m", open, high, low, close, 200m);
        OhlcvBar[] minutes = scenario switch
        {
            "low_before_entry" => [Minute(0, 99.9m, 99.95m, 99.0m, 99.5m), Minute(1, 99.5m, 100.3m, 99.5m, 100.1m), Minute(2, 100.1m, 100.2m, 99.8m, 100m)],
            "low_after_entry" => [Minute(0, 99.9m, 100.3m, 99.9m, 100.2m), Minute(1, 100.2m, 100.2m, 99.5m, 99.6m), Minute(2, 99.6m, 99.7m, 99.0m, 99.2m)],
            "same_minute" => [Minute(0, 99.9m, 99.95m, 99.6m, 99.7m), Minute(1, 99.7m, 100.3m, 99.0m, 99.5m)],
            _ => []
        };
        var strategy = RecipeStrategy(new(OpeningRangeBreakoutRules.PublishedRecipePolicyId, 1, 5, 14, 1m, 14));
        strategy = strategy with { ExitRules = strategy.ExitRules with { ProfitTargetMode = ExitRules.NoProfitTarget, ContinuationProtection = null } };

        var trade = Assert.Single(Replay(strategy, bars, [StopCandidate()], null, minutes).Trades);

        Assert.Equal(reason, trade.ExitReason.Split(';')[0]);
    }

    // The timing strategy with the published recipe's opening-range rules attached; the replay reads only the opt-in flags.
    private static StrategyDefinition RecipeStrategy(OpeningRangeBreakoutRules rules)
    {
        var strategy = Strategy(0.000000001m);
        return strategy with { EntryRules = strategy.EntryRules with { OpeningRangeBreakout = rules } };
    }

    // The production replay entry point with an explicit strategy and optional official closing prices.
    private static IntradayPortfolioReplayOutcome Replay(StrategyDefinition strategy, OhlcvBar[] bars, BacktestCandidateTrade[] candidates,
        IReadOnlyDictionary<(string, DateOnly), decimal>? closes, OhlcvBar[]? minutes = null) =>
        ReplayWith(strategy, bars, candidates, closes: closes, minutes: minutes);

    // The production replay entry point with every opt-in input explicit; unset inputs use the shared synthetic session.
    private static IntradayPortfolioReplayOutcome ReplayWith(StrategyDefinition strategy, OhlcvBar[] bars, BacktestCandidateTrade[] candidates,
        PortfolioConfig? portfolio = null, IReadOnlyDictionary<DateOnly, MarketSessionSchedule>? sessions = null,
        IReadOnlyDictionary<(string, DateOnly), decimal>? closes = null, OhlcvBar[]? minutes = null) =>
        IntradayPortfolioExecution.Replay(new(portfolio ?? Portfolio(), [strategy],
            candidates.Select(candidate => candidate with { StrategyId = strategy.StrategyId }).ToArray(), bars,
            sessions ?? Sessions(), Start, Start.AddMinutes(45), SessionClosePrices: closes, MinuteBars: minutes));

    // The same synthetic bars for another stock.
    private static OhlcvBar[] ForTicker(IEnumerable<OhlcvBar> bars, string ticker) => bars.Select(bar => bar with { Ticker = ticker }).ToArray();

    // The ordinary timing strategy (3R target) used by the shared Run helper, for tests that also need other replay inputs.
    private static StrategyDefinition TimingStrategy() => Strategy(0.000000001m);

    // A recipe strategy without a profit target, the published method's stop-only exit.
    private static StrategyDefinition StopOnlyStrategy(OpeningRangeBreakoutRules rules)
    {
        var strategy = RecipeStrategy(rules);
        return strategy with { ExitRules = strategy.ExitRules with { ProfitTargetMode = ExitRules.NoProfitTarget, ContinuationProtection = null } };
    }

    // The published recipe's opening-range rules with the given opt-in flags.
    private static OpeningRangeBreakoutRules Recipe(bool stopFromFill = false, bool exitAtClose = false) =>
        new(OpeningRangeBreakoutRules.PublishedRecipePolicyId, 1, 5, 14, 1m, 14, StopFromFill: stopFromFill, ExitAtSessionClose: exitAtClose);

    // A stop order that runs to the given deadline, as a close-exit strategy's orders do.
    private static BacktestCandidateTrade CloseExitCandidate(DateTimeOffset deadline) => StopCandidate() with
    {
        IntradayExecution = StopCandidate().IntradayExecution! with { ExpiresAtUtc = deadline, SessionDeadlineUtc = deadline }
    };

    // Two stop orders (X then Y in admission order) trigger inside the 09:45 bar, not at its open. Under a 40% gross limit the
    // first to be processed takes its full 24 shares and the other only the 15 left. One-minute bars showing Y reached its
    // trigger in minute 1 and X in minute 3 put Y first; without them the admission order stands and both orders carry the
    // named difference same_bar_trigger_order_unknown_admission_order_used.
    [Theory]
    [InlineData(true, 15, 24)]
    [InlineData(false, 24, 15)]
    public void GrossExposureLimit_SameBarTriggersClaimCapacityInTriggerMinuteOrder(bool withMinutes, int xShares, int yShares)
    {
        // The bar closes at 100.20; the first position's gain to that close is not knowable at the second trigger, so the
        // second order gets 15 shares, not the 16 that valuing the first at that close would give.
        var x = Bars();
        x[3] = Bar(15, 99.9m, 100.5m, 99.8m, 100.2m);
        var bars = x.Concat(ForTicker(x, "Y")).ToArray();
        OhlcvBar Minute(string ticker, int minute, decimal high) =>
            new(ticker, Start.AddMinutes(15 + minute), "1m", 99.9m, high, high > 100m ? 99.9m : 99.8m, 99.9m, 100m);
        OhlcvBar[] minutes = [Minute("X", 0, 99.95m), Minute("X", 1, 99.95m), Minute("X", 2, 99.95m), Minute("X", 3, 100.5m),
            Minute("Y", 0, 99.95m), Minute("Y", 1, 100.5m)];

        var result = ReplayWith(TimingStrategy(), bars, [StopCandidate(), StopCandidate("second") with { Ticker = "Y" }],
            Portfolio() with { MaxGrossExposurePct = 40 }, minutes: withMinutes ? minutes : null);

        Assert.Equal(xShares, result.Trades.Single(trade => trade.Ticker == "X").ShareQuantity);
        Assert.Equal(yShares, result.Trades.Single(trade => trade.Ticker == "Y").ShareQuantity);
        var flagged = result.Events.Where(item => item.State == IntradayPortfolioExecution.TriggerOrderAssumedState).ToArray();
        if (withMinutes) Assert.Empty(flagged);
        else Assert.Equal(["X", "Y"], flagged.Where(item => item.Reason == IntradayPortfolioExecution.SameBarTriggerOrderUnknown)
            .Select(item => item.Ticker).Order());
    }

    // The same two same-bar triggers without one-minute bars, but under a 100% limit both get their full 24 shares: the
    // assumed admission order changed nothing, so no trigger_order_assumed event is recorded.
    [Fact]
    public void GrossExposureLimit_AssumedTriggerOrderIsReportedOnlyWhenItChangedAnOutcome()
    {
        var x = Bars();
        x[3] = Bar(15, 99.9m, 100.5m, 99.8m, 100m);
        var bars = x.Concat(ForTicker(x, "Y")).ToArray();

        var result = ReplayWith(TimingStrategy(), bars, [StopCandidate(), StopCandidate("second") with { Ticker = "Y" }],
            Portfolio() with { MaxGrossExposurePct = 100 });

        Assert.Equal(2, result.Trades.Count);
        Assert.All(result.Trades, trade => Assert.Equal(24, trade.ShareQuantity));
        Assert.DoesNotContain(result.Events, item => item.State == IntradayPortfolioExecution.TriggerOrderAssumedState);
    }

    // Under a 25% limit X holds 24 shares from 09:45. In the 09:50 bar X is stopped out and Y's order (admitted at 09:45 with
    // 24 shares) triggers inside that bar. X comes first in the work list, but its stop may have hit after Y's trigger, so
    // the room it releases is not available to Y in that bar: Y is canceled for lack of room.
    [Fact]
    public void GrossExposureLimit_AStopOutInTheSameBarDoesNotFreeRoomForATrigger()
    {
        var x = Bars();
        x[4] = Bar(20, 100m, 100.2m, 98.9m, 99.2m);
        var y = ForTicker(Bars(), "Y");
        y[3] = y[3] with { Open = 99.5m, High = 99.9m, Low = 99.4m, Close = 99.6m };
        y[4] = y[4] with { Open = 99.9m, High = 100.5m, Low = 99.8m, Close = 100m };

        var result = ReplayWith(TimingStrategy(), x.Concat(y).ToArray(),
            [StopCandidate(), StopCandidate("second") with { Ticker = "Y" }], Portfolio() with { MaxGrossExposurePct = 25 });

        Assert.Equal("stop_loss", result.Trades.Single(trade => trade.Ticker == "X").ExitReason.Split(';')[0]);
        Assert.DoesNotContain(result.Trades, trade => trade.Ticker == "Y");
        Assert.Contains(result.Events, item => item.Ticker == "Y" && item.Reason == "gross_exposure_limit_reached_at_trigger");
    }

    // Both orders are admitted at 09:45 with 24 shares. X opens through its trigger and holds 24 shares from 09:45 (marked
    // at 100 when that bar completed); Y does not reach its trigger until inside the 09:50 bar, in which X closes at 106.
    // Y is sized from equity known when that bar opened: 40% of $10,000 less X's $2,400 buys 15 shares. Valuing X at the
    // 106 close (equity $10,144) would have bought 16.
    [Fact]
    public void GrossExposureLimit_SizesFromEquityKnownBeforeTheTriggerBarCloses()
    {
        var x = Bars();
        x[4] = Bar(20, 100m, 106m, 99.9m, 106m);
        var y = ForTicker(Bars(), "Y");
        y[3] = y[3] with { Open = 99.5m, High = 99.9m, Low = 99.4m, Close = 99.6m };
        y[4] = y[4] with { Open = 99.9m, High = 100.5m, Low = 99.8m, Close = 100m };

        var result = ReplayWith(StopOnlyStrategy(Recipe()), x.Concat(y).ToArray(),
            [StopCandidate(), StopCandidate("second") with { Ticker = "Y" }], Portfolio() with { MaxGrossExposurePct = 40 });

        Assert.Equal(24, result.Trades.Single(trade => trade.Ticker == "X").ShareQuantity);
        Assert.Equal(15, result.Trades.Single(trade => trade.Ticker == "Y").ShareQuantity);
    }

    // Two short stop orders (trigger 100, stop 101) both open through their trigger. The first takes its 25% position cap
    // (25 shares: a sell cannot fill below its $100 trigger); the gross limit then caps the second exactly as for longs, so
    // short-sale proceeds never become extra room: 40% of $10,000 less the first $2,500 leaves 14 shares.
    [Fact]
    public void GrossExposureLimit_CapsShortPositions()
    {
        var bars = Bars().Concat(ForTicker(Bars(), "Y")).ToArray();
        var result = Run(bars, [StopCandidate(direction: "short"), StopCandidate("second", "short") with { Ticker = "Y" }],
            Portfolio() with { MaxGrossExposurePct = 40 });

        Assert.All(result.Trades, trade => Assert.Equal("short", trade.Direction));
        Assert.Equal(25, result.Trades.Single(trade => trade.Ticker == "X").ShareQuantity);
        Assert.Equal(14, result.Trades.Single(trade => trade.Ticker == "Y").ShareQuantity);
        Assert.Contains(result.Events, item => item.Ticker == "Y" && item.State == "quantity_capped");
    }

    // Under a 25% gross limit X takes 24 shares at 09:45. When X is stopped out in the 09:50 bar its exposure is released, so
    // Y's order triggering at 09:55 gets 24 shares; while X is still open nothing is left and Y never trades.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GrossExposureLimit_FreesCapacityWhenAPositionIsStoppedOut(bool stoppedOut)
    {
        var x = Bars();
        if (stoppedOut) x[4] = Bar(20, 100m, 100.2m, 98.9m, 99.2m);
        var bars = x.Concat(ForTicker(Bars(), "Y")).ToArray();

        var result = Run(bars, [StopCandidate(), StopCandidate("second", minute: 25) with { Ticker = "Y" }],
            Portfolio() with { MaxGrossExposurePct = 25 });

        Assert.Equal(stoppedOut ? "stop_loss" : "session_deadline", result.Trades.Single(trade => trade.Ticker == "X").ExitReason.Split(';')[0]);
        if (stoppedOut) Assert.Equal(24, result.Trades.Single(trade => trade.Ticker == "Y").ShareQuantity);
        else Assert.DoesNotContain(result.Trades, trade => trade.Ticker == "Y");
    }

    // A one-bar limit order reserves its full size until it fills or is canceled. X's 09:40 limit at 100 is not reached and
    // is canceled at 09:45, which releases its reservation: Y's stop order triggering at 09:45 gets 24 shares under a 25%
    // limit. When X's limit fills instead, the position keeps the room and Y never trades.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GrossExposureLimit_FreesCapacityWhenAnOrderIsCanceled(bool canceled)
    {
        var x = Bars();
        if (canceled) x[2] = Bar(10, 100.3m, 100.5m, 100.1m, 100.3m);
        var bars = x.Concat(ForTicker(Bars(), "Y")).ToArray();

        var result = Run(bars, [Candidate(), StopCandidate("second") with { Ticker = "Y" }], Portfolio() with { MaxGrossExposurePct = 25 });

        Assert.Equal(canceled, result.Events.Any(item => item.Ticker == "X" && item.Reason == "one_bar_entry_expiry"));
        if (canceled) Assert.Equal(24, result.Trades.Single(trade => trade.Ticker == "Y").ShareQuantity);
        else Assert.DoesNotContain(result.Trades, trade => trade.Ticker == "Y");
    }

    // A short stop order (trigger 100, planned stop 101) gaps open below its trigger at 99.50. Measured from the fill the stop
    // moves from 101 to 100.50 and a later high of 100.60 exits there; measured from the trigger it is never reached.
    [Theory]
    [InlineData(true, "stop_loss", 100.5)]
    [InlineData(false, "session_deadline", 100)]
    public void StopFromFill_KeepsThePlannedDistanceForAShortGapFill(bool fromFill, string reason, double exitPrice)
    {
        var bars = Enumerable.Range(0, 9).Select(i => Bar(i * 5, 100, 100.2m, 99.7m, 100)).ToArray();
        bars[3] = Bar(15, 99.5m, 99.6m, 99.4m, 99.5m);
        bars[4] = Bar(20, 99.5m, 100.4m, 99.4m, 100m);
        bars[5] = Bar(25, 100m, 100.6m, 99.9m, 100.4m);

        var trade = Assert.Single(Replay(RecipeStrategy(Recipe(stopFromFill: fromFill)), bars, [StopCandidate(direction: "short")], null).Trades);

        Assert.Equal(reason, trade.ExitReason.Split(';')[0]);
        Assert.Equal((decimal)exitPrice, decimal.Round(trade.ExitPrice, 4));
        Assert.Equal(fromFill ? 100.5m : 101m, trade.StopLossPrice);
    }

    // With 1% participation the 24-share order fills in three parts (10, 10 and 4 shares from the preceding bars' volume).
    // The per-share commission is charged on every part and on the exit: 48 shares x $0.0035 more than without it. The stop
    // measured from the fill is set by the first part (gap fill at 100.50, stop 99.50) and not moved by the later parts at
    // 100.80; re-measuring from the average after each part would have raised it to 99.65 and then 99.67.
    [Fact]
    public void PartialFills_PayCommissionOnEveryPartAndKeepTheFirstFillsStop()
    {
        var bars = Enumerable.Range(0, 9).Select(i => Bar(i * 5, 100, 100.2m, 99.7m, 100)).ToArray();
        bars[3] = Bar(15, 100.5m, 100.6m, 100.4m, 100.5m);
        bars[4] = Bar(20, 100.8m, 100.9m, 100.7m, 100.8m, 400);
        bars[5] = Bar(25, 100.8m, 100.9m, 100.7m, 100.8m);
        bars[7] = Bar(35, 100, 100.2m, 99.7m, 100, 100_000);
        var strategy = StopOnlyStrategy(Recipe(stopFromFill: true));
        var limited = Portfolio(participation: 1);

        var with = ReplayWith(strategy, bars, [StopCandidate()], limited with { CommissionPerShare = 0.0035m });
        var without = ReplayWith(strategy, bars, [StopCandidate()], limited);

        var trade = Assert.Single(with.Trades);
        Assert.Equal([10, 10, 4], with.PortfolioState.Fills.Where(fill => fill.IsEntry).Select(fill => fill.Quantity));
        Assert.Equal(24, trade.ShareQuantity);
        Assert.Equal(Assert.Single(without.Trades).Fees + 48 * 0.0035m, trade.Fees);
        Assert.Equal(99.5m, trade.StopLossPrice);
    }

    // A stop order without a target fills at 09:45. When it fills at the bar's open (100.10, above the trigger) the whole bar
    // follows the entry, so the bar's low through the stop is an ordinary stop loss; when it fills inside the bar the order
    // of entry and stop is unknown and is labelled so.
    [Theory]
    [InlineData(100.1, "stop_loss")]
    [InlineData(99.9, "stop_loss_entry_bar_order_unknown")]
    public void EntryAtTheOpen_MakesAStopInTheSameBarAnOrdinaryStopLoss(double open, string reason)
    {
        var bars = Bars();
        bars[3] = Bar(15, (decimal)open, 100.3m, 98.9m, 99.5m);

        var trade = Assert.Single(Replay(StopOnlyStrategy(Recipe()), bars, [StopCandidate()], null).Trades);

        Assert.Equal(reason, trade.ExitReason.Split(';')[0]);
    }

    // On a half-day session (synthetic close 10:00 instead of 10:15) the close exit leaves at that session's own close.
    [Fact]
    public void ExitAtSessionClose_UsesTheEarlyCloseOfAHalfDay()
    {
        var halfDay = new Dictionary<DateOnly, MarketSessionSchedule> { [Day] = new(Day, true, new(9, 30), new(10, 0)) };
        var closes = new Dictionary<(string, DateOnly), decimal> { [("X", Day)] = 100.4m };

        var trade = Assert.Single(ReplayWith(RecipeStrategy(Recipe(exitAtClose: true)), Bars(), [CloseExitCandidate(Start.AddMinutes(30))],
            sessions: halfDay, closes: closes).Trades);

        Assert.Equal(Start.AddMinutes(30), trade.ExitTimestamp);
        Assert.Equal("session_close", trade.ExitReason.Split(';')[0]);
        Assert.Equal(100.4m, decimal.Round(trade.ExitPrice, 4));
    }

    // With the close exit, a stop touched in the last five-minute bar exits at the stop when that bar completes; the closing
    // price is not used for shares that already left.
    [Fact]
    public void ExitAtSessionClose_StopInTheLastBarExitsAtTheStop()
    {
        var bars = Bars();
        bars[8] = Bar(40, 100m, 100.2m, 98.9m, 99.2m);
        var closes = new Dictionary<(string, DateOnly), decimal> { [("X", Day)] = 99.1m };

        var trade = Assert.Single(ReplayWith(RecipeStrategy(Recipe(exitAtClose: true)), bars, [CloseExitCandidate(Start.AddMinutes(45))],
            closes: closes).Trades);

        Assert.Equal("stop_loss", trade.ExitReason.Split(';')[0]);
        Assert.Equal(99m, decimal.Round(trade.ExitPrice, 4));
        Assert.Equal(Start.AddMinutes(45), trade.ExitTimestamp);
    }

    // With 1% participation the last bar's 2,000 shares allow 20 shares. The stop in that bar sells 20 of the 24; the closing
    // auction may not reuse the volume those 20 consumed, so the last 4 shares stay unresolved instead of exiting at the close.
    [Fact]
    public void ExitAtSessionClose_DoesNotReuseLastBarVolumeAlreadyFilled()
    {
        var bars = Bars();
        bars[2] = Bar(10, 100, 100.5m, 99.5m, 100, 100_000);
        bars[8] = Bar(40, 100m, 100.2m, 98.9m, 99.2m, 2_000);
        var closes = new Dictionary<(string, DateOnly), decimal> { [("X", Day)] = 99.1m };

        var result = ReplayWith(RecipeStrategy(Recipe(exitAtClose: true)), bars, [CloseExitCandidate(Start.AddMinutes(45))],
            Portfolio(participation: 1), closes: closes);

        Assert.Empty(result.Trades);
        Assert.Equal(20, result.PortfolioState.Fills.Where(fill => !fill.IsEntry).Sum(fill => fill.Quantity));
        Assert.Contains(result.Failures, failure => failure.Reason == "session_close_partial_liquidation_unresolved");
    }
}

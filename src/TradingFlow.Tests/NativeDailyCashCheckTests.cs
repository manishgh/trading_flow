using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Tests;

public sealed class NativeDailyCashCheckTests
{
    // The guard is independent of entry sizing and rejects any negative observed cash without rounding it to zero.
    [Theory]
    [InlineData("-0.00000001")]
    [InlineData("-100.25")]
    public void NegativeSessionCash_FailsBeforePublication(string value)
    {
        var state = State([Session(1, decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture))]);
        Assert.Contains("not published", Assert.Throws<InvalidOperationException>(() =>
            NativeDailyCashCheck.Apply(state, Context())).Message);
    }

    // Actual session cash can be zero and can remain known when marks cannot support an equity calculation.
    [Fact]
    public void MinimumCash_PreservesZeroAndDoesNotDependOnPriceMarks()
    {
        var state = State([Session(1, 50m), Session(2, 0m) with { Equity = null, UnavailableReason = "missing mark" }]);
        var result = NativeDailyCashCheck.Apply(state, Context());
        Assert.Equal(0m, result.CashSummary!.MinimumCash);
        Assert.Equal(new DateOnly(2026, 7, 2), result.CashSummary.MinimumCashSession);
        Assert.True(result.CashSummary.CoversAllClosedSessions);
    }

    // Partial sessions are excluded; after close but before the daily replay receipt, a minimum must be withheld.
    [Theory]
    [InlineData("2026-07-02T19:59:59Z", true, 1)]
    [InlineData("2026-07-02T20:00:00Z", false, 0)]
    [InlineData("2026-07-02T20:01:00Z", false, 0)]
    [InlineData("2026-07-03T04:00:00Z", true, 0)]
    public void Cutoff_RequiresCloseAndNativeReplayReceipt(string cutoff, bool complete, int excluded)
    {
        var context = Context() with { CutoffUtc = DateTimeOffset.Parse(cutoff) };
        var result = NativeDailyCashCheck.Apply(State([Session(1, 20), Session(2, 10)]) with { CutoffUtc = context.CutoffUtc }, context);
        Assert.Equal(complete, result.CashSummary!.CoversAllClosedSessions);
        Assert.Equal(excluded, result.CashSummary.ExcludedOpenSessionCount);
        Assert.Equal(complete ? (excluded == 1 ? 20m : 10m) : (decimal?)null, result.CashSummary.MinimumCash);
    }

    // An authoritative early close changes the closed-session boundary but never invents a replay receipt.
    [Fact]
    public void EarlyClose_WithoutReplayReceiptIsUnavailable()
    {
        var context = Context() with { CutoffUtc = DateTimeOffset.Parse("2026-07-02T17:01:00Z"),
            Sessions = new Dictionary<DateOnly, MarketSessionSchedule>
            {
                [new(2026,7,1)] = MarketSessionSchedule.RegularDay(new(2026,7,1)),
                [new(2026,7,2)] = new(new(2026,7,2), true, new(9,30), new(13,0))
            } };
        var result = NativeDailyCashCheck.Apply(State([Session(1, 20), Session(2, 10)]), context);
        Assert.False(result.CashSummary!.CoversAllClosedSessions);
        Assert.Contains("receipt", result.CashSummary.UnavailableReason);
    }

    // Missing executions invalidate the full minimum without hiding an already proven earlier overdraft.
    [Fact]
    public void UnknownExecutions_WithholdMinimumButStillCheckKnownPrefix()
    {
        var state = State([Session(1, 50), Session(2, -10)]) with { CashEvidenceUnavailableFromSession = new(2026,7,2) };
        var result = NativeDailyCashCheck.Apply(state, Context());
        Assert.Null(result.CashSummary!.MinimumCash);
        Assert.False(result.CashSummary.CoversAllClosedSessions);
        Assert.Throws<InvalidOperationException>(() => NativeDailyCashCheck.Apply(
            state with { Sessions = [Session(1, -1), Session(2, 50)] }, Context()));
    }

    // A completed valuation can later lose execution coverage; its formerly published minimum cannot survive that change.
    [Fact]
    public void LaterExecutionFailure_ClearsPreviouslyComputedMinimum()
    {
        var complete = NativeDailyCashCheck.Apply(State([Session(1, 20), Session(2, 10)]), Context());
        var changed = NativeDailySessionLedger.MarkUnavailableFrom(complete, new(2026,7,2), "selected ticker failed", Context());
        Assert.Equal(1, changed.CashSummary!.ObservedSessionCount);
        Assert.Null(changed.CashSummary.MinimumCash);
        Assert.Null(changed.CashSummary.MinimumCashSession);
        Assert.False(changed.CashSummary.CoversAllClosedSessions);
    }

    // No completed observation has no cash minimum, rather than a made-up starting balance or zero.
    [Fact]
    public void EmptyBook_HasNoObservedMinimum()
    {
        var result = NativeDailyCashCheck.Apply(State([]), Context());
        Assert.Null(result.CashSummary!.MinimumCash);
        Assert.Equal(0, result.CashSummary.ObservedSessionCount);
    }

    // Fixtures model already reconciled end-session observations; real-fill accounting is covered by ledger/runner tests.
    private static BacktestSessionObservation Session(int day, decimal cash) =>
        new(new(2026,7,day), cash, 0, 0, 0, 0, cash, 0, 0, null);
    private static NativeDailyAccountingContext Context() => new(DateTimeOffset.Parse("2026-07-01T04:00:00Z"),
        DateTimeOffset.Parse("2026-07-03T04:00:00Z"), new Dictionary<DateOnly, MarketSessionSchedule>
        {
            [new(2026,7,1)] = MarketSessionSchedule.RegularDay(new(2026,7,1)),
            [new(2026,7,2)] = MarketSessionSchedule.RegularDay(new(2026,7,2))
        }, true);
    private static BacktestPortfolioState State(IReadOnlyList<BacktestSessionObservation> sessions) =>
        new(NativeDailySessionLedger.ValuationBasis, Context().CutoffUtc, 0, 0, 0, [], [], [], [], sessions);
}
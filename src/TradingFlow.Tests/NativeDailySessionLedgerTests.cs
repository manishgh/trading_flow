using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Tests;

public sealed class NativeDailySessionLedgerTests
{
    [Theory]
    [InlineData("2026-07-01T04:00:00Z", "2026-06-30")]
    [InlineData("2026-09-01T04:00:00Z", "2026-08-31")]
    [InlineData("2026-10-31T04:00:00Z", "2026-10-30")]
    public void SourceSessionDate_PreservesMonthEndAndFriday(string receipt, string expected)
        => Assert.Equal(DateOnly.Parse(expected), NativeDailySessionLedger.SourceSessionDate(DateTimeOffset.Parse(receipt)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialExitAcrossMonthBoundary_ReconcilesActualCashFeesAndRemainingBasis(bool shortTrade)
    {
        var entry = Fill(true, !shortTrade, 10, 100, 1, "2026-07-01T04:00:00Z");
        var exit = Fill(false, shortTrade, 4, shortTrade ? 80 : 120, 2, "2026-07-02T04:00:00Z");
        var state = State([entry, exit], shortTrade ? -6 : 6);
        var bars = new[] { Bar("2026-06-30T04:00:00Z", shortTrade ? 95 : 105),
            Bar("2026-07-01T04:00:00Z", shortTrade ? 90 : 110) };
        var result = NativeDailySessionLedger.Analyze(10000, state, bars, Context());
        Assert.Equal(10049, result.Sessions[0].Equity);
        Assert.Equal(-1, result.Sessions[0].RealizedNetProfit);
        Assert.Equal(50, result.Sessions[0].UnrealizedProfit);
        Assert.Equal(10137, result.Sessions[1].Equity);
        Assert.Equal(77, result.Sessions[1].RealizedNetProfit);
        Assert.Equal(60, result.Sessions[1].UnrealizedProfit);
        Assert.Equal(3, result.Sessions[1].Fees);
        Assert.Equal(100, Assert.Single(result.OpenPositions).EntryBasis);
        Assert.Equal(entry.TimestampUtc, result.Fills[0].TimestampUtc);
        NativeDailySessionLedger.VerifyRecordedState(10000, result, Context());
    }

    [Fact]
    public void PartialEntry_UsesWeightedActualEntryCost()
    {
        var fills = new[] { Fill(true, true, 6, 100, 1, "2026-07-01T04:00:00Z"),
            Fill(true, true, 4, 110, 1, "2026-07-02T04:00:00Z"),
            Fill(false, false, 2, 120, 1, "2026-07-02T04:00:00Z") };
        var result = NativeDailySessionLedger.Analyze(10000, State(fills, 8),
            [Bar("2026-06-30T04:00:00Z", 105), Bar("2026-07-01T04:00:00Z", 130)], Context());
        Assert.Equal(104, Assert.Single(result.OpenPositions).EntryBasis);
        Assert.Equal(29, result.Sessions[^1].RealizedNetProfit);
        Assert.Equal(208, result.Sessions[^1].UnrealizedProfit);
        Assert.Equal(10237, result.Sessions[^1].Equity);
    }

    [Fact]
    public void MissingMark_WithholdsAffectedAndLaterSessionsWithoutInventingSale()
    {
        var result = NativeDailySessionLedger.Analyze(10000,
            State([Fill(true, true, 10, 100, 1, "2026-07-01T04:00:00Z")], 10),
            [Bar("2026-07-01T04:00:00Z", 110)], Context());
        Assert.Equal(new DateOnly(2026, 6, 30), result.UnavailableFromSession);
        Assert.All(result.Sessions, s => Assert.Null(s.Equity));
        Assert.Equal(8999, result.Sessions[^1].Cash);
        Assert.Equal(10, Assert.Single(result.OpenPositions).SignedQuantity);
        Assert.Single(result.Fills);
        NativeDailySessionLedger.VerifyRecordedState(10000, result, Context());
    }

    [Fact]
    public void MissingLaterMark_PreservesEarlierSupportedSession()
    {
        var result = NativeDailySessionLedger.Analyze(10000,
            State([Fill(true, true, 10, 100, 1, "2026-07-01T04:00:00Z")], 10),
            [Bar("2026-06-30T04:00:00Z", 105)], Context());
        Assert.Equal(10049, result.Sessions[0].Equity);
        Assert.Null(result.Sessions[1].Equity);
        Assert.Null(Assert.Single(result.OpenPositions).MarkPrice);
    }

    [Fact]
    public void LateKnownMark_CannotBeMovedIntoItsEarlierSourceSession()
    {
        var bar = Bar("2026-06-30T04:00:00Z", 105) with { KnownAtUtc = DateTimeOffset.Parse("2026-07-01T05:00:00Z") };
        var result = NativeDailySessionLedger.Analyze(10000,
            State([Fill(true, true, 10, 100, 1, "2026-07-01T04:00:00Z")], 10), [bar], Context());
        Assert.Null(result.Sessions[0].Equity);
    }

    [Fact]
    public void InvalidFill_IsRejectedEvenWhenEveryMarkIsMissing()
    {
        var invalid = Fill(true, true, 10, 100, 1, "2026-07-01T04:00:00Z") with { Quantity = -1 };
        Assert.Throws<InvalidDataException>(() => NativeDailySessionLedger.Analyze(10000, State([invalid], 10), [], Context()));
    }

    [Fact]
    public void FutureBars_DoNotChangeFrozenCutoffState()
    {
        var state = State([Fill(true, true, 10, 100, 1, "2026-07-01T04:00:00Z")], 10);
        var bars = new[] { Bar("2026-06-30T04:00:00Z", 105), Bar("2026-07-01T04:00:00Z", 110) };
        var original = NativeDailySessionLedger.Analyze(10000, state, bars, Context());
        var appended = NativeDailySessionLedger.Analyze(10000, state, [..bars, Bar("2026-07-02T04:00:00Z", 99999)], Context());
        Assert.Equal(original.Sessions, appended.Sessions);
        Assert.Equal(original.OpenPositions, appended.OpenPositions);
    }

    [Fact]
    public void ChangedSavedMarkOrEquity_IsRejectedByRecordedStateReconciliation()
    {
        var state = NativeDailySessionLedger.Analyze(10000,
            State([Fill(true, true, 10, 100, 1, "2026-07-01T04:00:00Z")], 10),
            [Bar("2026-06-30T04:00:00Z", 105), Bar("2026-07-01T04:00:00Z", 110)], Context());
        Assert.Throws<InvalidDataException>(() => NativeDailySessionLedger.VerifyRecordedState(10000,
            state with { Sessions = [state.Sessions[0] with { Equity = 99999 }, state.Sessions[1]] }, Context()));
    }

    private static NativeDailyAccountingContext Context() => new(DateTimeOffset.Parse("2026-06-30T04:00:00Z"),
        DateTimeOffset.Parse("2026-07-02T04:00:00Z"), new Dictionary<DateOnly, MarketSessionSchedule>
        { [new(2026,6,30)] = MarketSessionSchedule.RegularDay(new(2026,6,30)),
          [new(2026,7,1)] = MarketSessionSchedule.RegularDay(new(2026,7,1)) }, true);

    private static BacktestPortfolioFill Fill(bool entry, bool buy, int quantity, decimal price, decimal fee, string timestamp)
    {
        var time = DateTimeOffset.Parse(timestamp);
        return new("candidate", "strategy", "ABC", buy, entry, quantity, price, fee, time, NativeDailySessionLedger.SourceSessionDate(time));
    }

    private static BacktestPortfolioState State(IReadOnlyList<BacktestPortfolioFill> fills, int remaining) =>
        new(NativeDailySessionLedger.ValuationBasis, Context().CutoffUtc, 1, 1, 0, fills,
            [new("candidate", "strategy", "ABC", remaining, 100, null, null, null)], [], [], [])
        {
            AdmittedOrders = [new("candidate", "strategy", "ABC", 10, 100, remaining < 0 ? "short" : "long",
                DateTimeOffset.Parse("2026-06-30T04:00:00Z"))]
        };

    private static OhlcvBar Bar(string timestamp, decimal close) =>
        new("ABC", DateTimeOffset.Parse(timestamp), "1d", close, close, close, close, 10000);
}

using TradingFlow.Domain.Execution;

namespace TradingFlow.Tests;

public sealed class AccountLossLimitPolicyTests
{
    private static readonly AccountLossLimits Limits = new(2m, 4m, 10m);
    private static readonly DateOnly Monday = new(2026, 11, 23);
    private static readonly AccountClosingEquity LastWeek = Close(Monday.AddDays(-3), 100m);

    // Loss at the exact threshold latches; recovery does not clear it in the same period.
    [Fact]
    public void Observe_DailyAndWeeklyResetOnlyInTheirNewVerifiedPeriods()
    {
        var first = AccountLossLimitPolicy.Observe(null, Sample(Monday, 98m, LastWeek, LastWeek), Limits);
        Assert.Equal(2m, first.DailyBreach?.ObservedPct);
        Assert.Null(first.WeeklyBreach);
        var recoveredMark = Sample(Monday, 101m, LastWeek, LastWeek, minute: 1);
        var recovered = AccountLossLimitPolicy.Observe(first, recoveredMark, Limits);
        Assert.Equal(first.DailyBreach, recovered.DailyBreach);
        Assert.Equal(101m, recovered.PeakEquity);

        var tuesday = Monday.AddDays(1);
        var mondayClose = Close(Monday, 98m);
        var second = AccountLossLimitPolicy.Observe(recovered, Sample(tuesday, 96m, mondayClose, LastWeek), Limits);
        Assert.Equal(4m, second.WeeklyBreach?.ObservedPct);

        var wednesday = tuesday.AddDays(1);
        var tuesdayClose = Close(tuesday, 99m);
        var third = AccountLossLimitPolicy.Observe(second, Sample(wednesday, 100m, tuesdayClose, LastWeek), Limits);
        Assert.Null(third.DailyBreach);
        Assert.NotNull(third.WeeklyBreach);

        var nextWeek = Monday.AddDays(7);
        var earlyClose = Close(nextWeek.AddDays(-3), 100m, closeUtcHour: 18);
        var fourth = AccountLossLimitPolicy.Observe(third, Sample(nextWeek, 100m, earlyClose, earlyClose), Limits);
        Assert.False(fourth.Breached);
        Assert.Equal(101m, fourth.PeakEquity);
        Assert.Equal(LastWeek.CloseUtc, fourth.PeakEpochStartUtc);
    }

    // A missed call or replay cannot reset durable peak evidence, even in another week.
    [Fact]
    public void Observe_PeakStopSurvivesStaleReplayOutageAndNewWeek()
    {
        var high = AccountLossLimitPolicy.Observe(null, Sample(Monday, 110m, LastWeek, LastWeek), Limits);
        var low = Sample(Monday, 99m, LastWeek, LastWeek, minute: 1);
        var halted = AccountLossLimitPolicy.Observe(high, low, Limits);
        Assert.Equal(10m, halted.PeakBreach?.ObservedPct);
        Assert.Same(halted, AccountLossLimitPolicy.Observe(halted, Sample(Monday, 110m, LastWeek, LastWeek), Limits));

        var after = low.ObservedAtUtc.AddMinutes(1);
        var outage = AccountLossLimitPolicy.Observe(halted, new AccountLossSample(after, after, 0m, null, "Broker timeout"), Limits);
        Assert.Equal(halted.PeakBreach, outage.PeakBreach);
        var nextWeek = Monday.AddDays(7);
        var anchor = Close(nextWeek.AddDays(-3), 105m, closeUtcHour: 18);
        var resumedPrices = AccountLossLimitPolicy.Observe(outage, Sample(nextWeek, 115m, anchor, anchor), Limits);
        Assert.Equal(halted.PeakBreach, resumedPrices.PeakBreach);
        Assert.NotNull(AccountLossLimitPolicy.Refusal(resumedPrices, new("alpaca", Limits, TimeSpan.FromMinutes(1)),
            nextWeek, resumedPrices.ObservationUtc));
    }

    // Overnight restart sees the known closing loss even though the live mark recovered.
    [Fact]
    public void Observe_InterveningBrokerCloseCanLatchWeeklyAndPeakLosses()
    {
        var mondayMark = Sample(Monday, 100m, LastWeek, LastWeek);
        var monday = AccountLossLimitPolicy.Observe(null, mondayMark, Limits);
        var missingClose = Close(Monday, 89m);
        var tuesdayMark = Sample(Monday.AddDays(1), 104m, missingClose, LastWeek);
        var recovered = AccountLossLimitPolicy.Observe(monday, tuesdayMark, Limits);

        Assert.Null(recovered.DailyBreach);
        Assert.Equal(11m, recovered.WeeklyBreach?.ObservedPct);
        Assert.Equal(11m, recovered.PeakBreach?.ObservedPct);
        Assert.Equal(missingClose.CloseUtc, recovered.PeakBreach?.ObservedAtUtc);
        Assert.Equal(104m, recovered.PeakEquity);
    }

    // Missing anchor proof, unknown funding and old account marks each fail entry closed.
    [Fact]
    public void Refusal_DoesNotInventABaselineOrTreatFundingAsPnl()
    {
        var options = new AccountLossGuardOptions("alpaca", Limits, TimeSpan.FromMinutes(1));
        var first = Sample(Monday, 100m, LastWeek, LastWeek);
        Assert.NotNull(AccountLossLimitPolicy.Refusal(null, options, Monday, first.ObservedAtUtc));
        var healthy = AccountLossLimitPolicy.Observe(null, first, Limits);
        Assert.Null(AccountLossLimitPolicy.Refusal(healthy, options, Monday, first.ObservedAtUtc));
        Assert.NotNull(AccountLossLimitPolicy.Refusal(healthy, options, Monday, first.ObservedAtUtc.AddMinutes(2)));

        var newMark = Sample(Monday, 75m, LastWeek, LastWeek, minute: 1);
        var fundingEvidence = newMark.Evidence! with { FundingIssue = "Broker transfer requires a reviewed equity basis." };
        var funding = AccountLossLimitPolicy.Observe(healthy, newMark with { Evidence = fundingEvidence }, Limits);
        Assert.False(funding.Breached);
        Assert.NotNull(AccountLossLimitPolicy.Refusal(funding, options, Monday, newMark.ObservedAtUtc));
        var later = AccountLossLimitPolicy.Observe(funding, Sample(Monday, 100m, LastWeek, LastWeek, minute: 2), Limits);
        Assert.Equal(funding.FundingIssue, later.FundingIssue);
    }

    // All observations use known historical marks followed by a synthetic UTC receipt.
    private static AccountLossSample Sample(DateOnly session, decimal equity, AccountClosingEquity prior,
        AccountClosingEquity week, int minute = 0)
    {
        var at = new DateTimeOffset(session.ToDateTime(new TimeOnly(16, minute)), TimeSpan.Zero);
        return new AccountLossSample(at.AddSeconds(-1), at, equity,
            new AccountLossEvidence(session, prior, week, [week, prior], null), null);
    }

    // Tag known daily closes explicitly, including a fixture early close after Thanksgiving.
    private static AccountClosingEquity Close(DateOnly date, decimal equity, int closeUtcHour = 21) =>
        new(date, new DateTimeOffset(date.ToDateTime(new TimeOnly(closeUtcHour, 0)), TimeSpan.Zero), equity);
}

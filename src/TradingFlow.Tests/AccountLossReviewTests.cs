using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Data.Orders;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Tests;

public sealed class AccountLossReviewTests
{
    private static readonly AccountLossGuardOptions Options = new("alpaca", new(2m, 4m, 10m), TimeSpan.FromMinutes(1));
    private static readonly DateTimeOffset At = new(2026, 11, 23, 16, 0, 0, TimeSpan.Zero);
    private static readonly AccountLossScope Scope = new("alpaca", "paper", "fixture-account");
    private static readonly AccountLossReview Review = new(1, "reset_peak", "administrator-id", "Reviewed the portfolio and resolved the cause.");

    // Reset a peak after review while preserving losses in the same verified daily/weekly periods.
    [Fact]
    public void PeakResetPreservesDailyWeeklyAndStartsNewObservedEpoch()
    {
        var sample = Sample(At, 89m);
        var state = AccountLossLimitPolicy.Observe(null, sample, Options.Limits) with { CancellationSweepPending = false };
        var fresh = Sample(At.AddSeconds(10), 90m);
        var next = AccountLossReviewPolicy.Apply(state, Review, fresh, Options, fresh.ObservedAtUtc);
        Assert.Null(next.PeakBreach);
        Assert.Equal(90m, next.PeakEquity);
        Assert.Equal(fresh.ObservedAtUtc, next.PeakEpochStartUtc);
        Assert.Equal(state.DailyBreach, next.DailyBreach);
        Assert.Equal(state.WeeklyBreach, next.WeeklyBreach);
        Assert.Contains("Daily", AccountLossLimitPolicy.Refusal(next, Options, fresh.Evidence!.Session, fresh.ObservedAtUtc));
    }

    // A reset must replay a missed current-week loss even when the fresh mark has rebounded.
    [Fact]
    public void PeakResetDoesNotEraseAnUnobservedWeeklyBreach()
    {
        var previous = AccountLossLimitPolicy.Observe(null, Sample(At.AddDays(-7), 89m), Options.Limits)
            with { CancellationSweepPending = false };
        var fresh = Sample(At.AddDays(1), 101m);
        var mondayClose = new AccountClosingEquity(new DateOnly(2026, 11, 23), At.AddHours(5), 95m);
        fresh = fresh with { Evidence = fresh.Evidence! with { PriorClose = mondayClose,
            CompletedSessions = [fresh.Evidence.WeekAnchor, mondayClose] } };
        var next = AccountLossReviewPolicy.Apply(previous, Review, fresh, Options, fresh.ObservedAtUtc);
        Assert.Null(next.PeakBreach);
        Assert.Equal(5m, next.WeeklyBreach?.ObservedPct);
        Assert.True(next.CancellationSweepPending);
    }
    // Unknown, stale, future and unresolved order evidence never allows an administrator to bypass controls.
    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("archive")]
    [InlineData("sweep")]
    [InlineData("order")]
    [InlineData("reason")]
    [InlineData("funding")]
    public void InvalidReviewEvidenceIsRefused(string fault)
    {
        var state = AccountLossLimitPolicy.Observe(null, Sample(At, 89m), Options.Limits) with { CancellationSweepPending = false };
        var fresh = Sample(At.AddSeconds(10), 90m);
        var review = Review;
        var now = fresh.ObservedAtUtc;
        switch (fault)
        {
            case "stale": now = now.AddMinutes(2); break;
            case "future": now = now.AddSeconds(-1); break;
            case "archive": fresh = fresh with { Evidence = fresh.Evidence! with { HistoryArchiveId = null } }; break;
            case "sweep": state = state with { CancellationSweepPending = true }; break;
            case "order": state = state with { CancellationReviews = [new("c", "b", "AAPL", 1m, At)] }; break;
            case "reason": review = review with { Reason = "" }; break;
            case "funding": state = state with { FundingIssue = "Transfer" }; break;
        }
        Assert.Throws<InvalidOperationException>(() => AccountLossReviewPolicy.Apply(state, review, fresh, Options, now));
    }

    // A transfer cannot be reviewed away while the weekly reference still predates it.
    [Fact]
    public void FundingRequiresBothPostTransferAnchorsAndNewTransferRelatches()
    {
        var healthy = AccountLossLimitPolicy.Observe(null, Sample(At, 100m), Options.Limits);
        var fundingAt = At.AddSeconds(1);
        var fundingSample = Sample(fundingAt, 80m);
        fundingSample = fundingSample with { Evidence = fundingSample.Evidence! with
            { FundingIssue = "Transfer", LatestFundingDate = new DateOnly(2026, 11, 23) } };
        var stopped = AccountLossLimitPolicy.Observe(healthy, fundingSample, Options.Limits);
        var request = Review with { Action = "rebaseline_funding" };
        var early = Sample(At.AddSeconds(10), 80m);
        Assert.Throws<InvalidOperationException>(() => AccountLossReviewPolicy.Apply(stopped, request, early, Options, early.ObservedAtUtc));
        var later = Sample(At.AddDays(7), 80m, 80m);
        var next = AccountLossReviewPolicy.Apply(stopped, request, later, Options, later.ObservedAtUtc);
        Assert.Null(next.FundingIssue);
        Assert.Null(AccountLossLimitPolicy.Refusal(next, Options, later.Evidence!.Session, later.ObservedAtUtc));
        var newFunding = later with { ObservedAtUtc = later.ObservedAtUtc.AddSeconds(5),
            Evidence = later.Evidence with { FundingIssue = "New transfer", LatestFundingDate = later.Evidence.Session } };
        Assert.Equal("New transfer", AccountLossLimitPolicy.Observe(next, newFunding, Options.Limits).FundingIssue);
    }

    // A first-ever funding latch has no trusted mark; weekly replay still begins at its verified anchor.
    [Fact]
    public void FirstFundingRebaselineReplaysCurrentWeekBeforeRebound()
    {
        var initial = Sample(At.AddDays(-7), 80m);
        initial = initial with { Evidence = initial.Evidence! with { FundingIssue = "Transfer",
            LatestFundingDate = new DateOnly(2026, 11, 16) } };
        var state = AccountLossLimitPolicy.Observe(null, initial, Options.Limits);
        Assert.Null(state.LastTrustedObservationUtc);
        var fresh = Sample(At.AddDays(1), 100m);
        var low = new AccountClosingEquity(new DateOnly(2026, 11, 23), At.AddHours(5), 94m);
        fresh = fresh with { Evidence = fresh.Evidence! with { PriorClose = low,
            CompletedSessions = [fresh.Evidence.WeekAnchor, low] } };
        var next = AccountLossReviewPolicy.Apply(state, Review with { Action = "rebaseline_funding" },
            fresh, Options, fresh.ObservedAtUtc);
        Assert.Null(next.PeakBreach);
        Assert.Null(next.FundingIssue);
        Assert.Equal(6m, next.WeeklyBreach?.ObservedPct);
        Assert.True(next.CancellationSweepPending);
    }
    // The immutable event revision fences even cancellation-only changes, and reset survives store reopening.
    [Fact]
    public async Task SqliteReviewFencesEveryStateChangeAndKeepsAudit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>().UseSqlite(connection).Options;
        var factory = new Factory(options);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        var store = new SqliteAccountLossStateStore(factory);
        var run = new ProductionRun { RunId = Guid.NewGuid(), Profile = "paper", Status = "running",
            CodeVersion = "fixture-build", ConfigHash = new string('b', 64), StartedAtUtc = At };
        var initial = await store.RecordAsync(Scope, run, Sample(At, 89m), Options, default);
        Assert.True(await store.CompleteCancellationSweepAsync(Scope, initial.CancellationSweepGeneration, default));
        var stale = await store.GetReviewAsync(Scope, default);
        var order = new AccountLossCancellationReview("client", "broker", "AAPL", 0m, At);
        await store.SetCancellationReviewAsync(Scope, run, order, true, default);
        await store.SetCancellationReviewAsync(Scope, run, order, false, default);
        var fresh = Sample(At.AddSeconds(10), 90m);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReviewAsync(Scope, run,
            Review with { ExpectedRevision = stale.Revision }, fresh, Options, fresh.ObservedAtUtc, default));
        var current = await store.GetReviewAsync(Scope, default);
        var result = await store.ReviewAsync(Scope, run, Review with { ExpectedRevision = current.Revision },
            fresh, Options, fresh.ObservedAtUtc, default);
        Assert.True(result.Revision > current.Revision);
        var reopened = await new SqliteAccountLossStateStore(factory).GetReviewAsync(Scope, default);
        Assert.Equal(result.Revision, reopened.Revision);
        Assert.Null(reopened.State!.PeakBreach);
        Assert.NotNull(reopened.State.DailyBreach);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReviewAsync(Scope, run,
            Review with { ExpectedRevision = current.Revision }, fresh, Options, fresh.ObservedAtUtc, default));
        await using var verify = factory.CreateDbContext();
        var audit = Assert.Single(await verify.RiskEvents.Where(row => row.EventType == "account_loss_reviewed_reset").ToArrayAsync());
        Assert.Contains("administrator-id", audit.DetailsJson);
        Assert.Contains("fixture-archive", audit.DetailsJson);
    }

    // Provide known completed closes for one NY week with an archived broker response.
    private static AccountLossSample Sample(DateTimeOffset at, decimal equity, decimal baseline = 100m)
    {
        var session = DateOnly.FromDateTime(at.UtcDateTime);
        var friday = AccountLossLimitPolicy.WeekOf(session).AddDays(-3);
        var prior = new AccountClosingEquity(friday, new DateTimeOffset(friday.ToDateTime(new TimeOnly(21, 0)), TimeSpan.Zero), baseline);
        return new(at.AddSeconds(-1), at, equity, new(session, prior, prior, [prior], null, "fixture-archive"), null);
    }

    private sealed class Factory(DbContextOptions<TradingFlowDbContext> options) : IDbContextFactory<TradingFlowDbContext>
    {
        // Share only this test's in-memory connection.
        public TradingFlowDbContext CreateDbContext() => new(options);
    }
}

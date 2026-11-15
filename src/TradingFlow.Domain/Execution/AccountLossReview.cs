using TradingFlow.Domain.Persistence;

namespace TradingFlow.Domain.Execution;

public sealed record AccountLossReviewSnapshot(long Revision, AccountLossState? State);
public sealed record AccountLossReviewRequest(long ExpectedRevision, string Action, string Reason);
public sealed record AccountLossReview(long ExpectedRevision, string Action, string Actor, string Reason);

public interface IAccountLossReviewStore
{
    Task<AccountLossReviewSnapshot> GetReviewAsync(AccountLossScope scope, CancellationToken token);
    Task<AccountLossReviewSnapshot> ReviewAsync(AccountLossScope scope, ProductionRun run,
        AccountLossReview review, AccountLossSample sample, AccountLossGuardOptions options,
        DateTimeOffset now, CancellationToken token);
}

/// <summary>Clears only reviewed peak/funding latches using fresh server-sourced account evidence.</summary>
public static class AccountLossReviewPolicy
{
    // Keep loss periods and operational fences intact; a reset cannot erase the account's history.
    public static AccountLossState Apply(AccountLossState current, AccountLossReview review,
        AccountLossSample sample, AccountLossGuardOptions options, DateTimeOffset now)
    {
        if (review.ExpectedRevision <= 0 || string.IsNullOrWhiteSpace(review.Actor) ||
            string.IsNullOrWhiteSpace(review.Reason) || review.Reason.Length > 2000)
            throw new InvalidOperationException("A current review revision, administrator identity and explanatory reason (at most 2000 characters) are required.");
        if (review.Action is not ("reset_peak" or "rebaseline_funding"))
            throw new InvalidOperationException("Choose reset_peak or rebaseline_funding.");
        if (current.CancellationSweepPending || current.CancellationReviews is { Count: > 0 })
            throw new InvalidOperationException("Complete the pending opening-order sweep and cancellation/protection review before resetting limits.");
        if (sample.RequestedAtUtc == default || sample.ObservedAtUtc < sample.RequestedAtUtc ||
            sample.ObservedAtUtc <= current.ObservationUtc || sample.ObservedAtUtc > now ||
            now - sample.RequestedAtUtc > options.MaximumSnapshotAge || sample.Equity <= 0m ||
            sample.EvidenceIssue is not null || sample.Evidence is not { } evidence ||
            string.IsNullOrWhiteSpace(evidence.HistoryArchiveId))
            throw new InvalidOperationException("Reset requires a newer, fresh positive broker equity mark and archived complete loss evidence.");
        var latestFunding = new[] { current.LatestFundingDate, evidence.LatestFundingDate }.Max();
        if (review.Action == "reset_peak")
        {
            if (current.PeakBreach is null || current.FundingIssue is not null || evidence.FundingIssue is not null)
                throw new InvalidOperationException("Peak reset requires a peak breach and a clean funding basis.");
        }
        else
        {
            if (current.FundingIssue is null || latestFunding is null)
                throw new InvalidOperationException("Funding rebaseline requires a recorded transfer date supported by broker history.");
            if (evidence.PriorClose.Session <= latestFunding || evidence.WeekAnchor.Session <= latestFunding)
                throw new InvalidOperationException("Both daily and weekly reference closes must be after the latest transfer date. Wait for verified post-transfer reference closes.");
        }
        // Reset the observed peak epoch only; Observe still preserves same-period daily/weekly latches
        // and rejects changed frozen baselines. No reset path clears broker reconciliation or admission.
        var basis = current with
        {
            PeakEquity = sample.Equity, PeakEpochStartUtc = sample.ObservedAtUtc,
            PeakBreach = null, FundingIssue = null, EvidenceIssue = null,
            LatestFundingDate = latestFunding
        };
        var next = AccountLossLimitPolicy.Observe(basis,
            sample with { Evidence = evidence with { FundingIssue = null } }, options.Limits);
        if (next.EvidenceIssue is not null || next.LastTrustedObservationUtc != sample.ObservedAtUtc)
            throw new InvalidOperationException(next.EvidenceIssue ?? "The broker evidence could not establish a reviewed baseline.");
        return next;
    }
}

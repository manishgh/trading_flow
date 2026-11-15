using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Engine.Execution;

public interface IBrokerAccountLossEvidenceProvider
{
    Task<AccountLossEvidence> GetAccountLossEvidenceAsync(
        BrokerAccountSnapshot account, DateOnly? lastTrustedSession, CancellationToken cancellationToken);
}

/// <summary>Feeds the same durable account gate from admission, dispatch and broker reconciliation.</summary>
public sealed class AccountLossGuard(
    IAccountLossStateStore store, AccountLossGuardOptions options, IBrokerAccountBindingService accountBinding,
    TimeProvider timeProvider, ILogger<AccountLossGuard> logger)
{
    // Bind the broker account and retain a source failure as an explicit entry refusal.
    public async Task<AccountLossState> ObserveAsync(
        ProductionRun run, IBrokerClient broker, BrokerAccountSnapshot account, CancellationToken cancellationToken)
    {
        accountBinding.Validate(account);
        var scope = Scope(run, account.AccountId);
        var previous = await store.GetAsync(scope, cancellationToken);
        AccountLossEvidence? evidence = null;
        string? issue = null;
        try
        {
            RequireCurrentAccount(account, timeProvider.GetUtcNow());
            var source = broker as IBrokerAccountLossEvidenceProvider
                ?? throw new InvalidDataException("This broker cannot supply verified account loss evidence.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.MaximumSnapshotAge);
            evidence = await source.GetAccountLossEvidenceAsync(account, previous?.Session, deadline.Token);
            RequireCurrentAccount(account, timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            issue = exception.Message;
            if (previous?.EvidenceIssue != issue)
            {
                logger.LogWarning("Account loss evidence unavailable for {Mode}: {Reason}", scope.Mode, issue);
            }
        }

        var now = timeProvider.GetUtcNow();
        var observedAt = account.ObservedAtUtc > now ? now : account.ObservedAtUtc;
        var state = await store.RecordAsync(scope, run,
            new AccountLossSample(account.RequestedAtUtc.ToUniversalTime(), observedAt.ToUniversalTime(),
                account.Equity, evidence, issue),
            options, cancellationToken);
        if (state.Breached && state.ObservationUtc == account.ObservedAtUtc &&
            (state.DailyBreach != previous?.DailyBreach || state.WeeklyBreach != previous?.WeeklyBreach ||
             state.PeakBreach != previous?.PeakBreach))
        {
            logger.LogCritical("Account loss limit reached for {Mode}. Daily={DailyBreach} Weekly={WeeklyBreach} PeakToTrough={PeakBreach}",
                scope.Mode, state.DailyBreach, state.WeeklyBreach, state.PeakBreach);
        }

        return state;
    }

    // Keep unresolved protected-parent cancellations in the same durable entry gate.
    public Task SetCancellationReviewAsync(
        ProductionRun run, string accountId, AccountLossCancellationReview review,
        bool required, CancellationToken cancellationToken) =>
        store.SetCancellationReviewAsync(Scope(run, accountId), run, review, required, cancellationToken);

    // Fence completion against any new account breach observed during the broker sweep.
    public Task<bool> CompleteCancellationSweepAsync(
        ProductionRun run, string accountId, long generation, CancellationToken cancellationToken) =>
        store.CompleteCancellationSweepAsync(Scope(run, accountId), generation, cancellationToken);

    // Normalize saved paper labels to one durable account scope shared by all runs.
    private AccountLossScope Scope(ProductionRun run, string accountId) =>
        new(options.Provider, OperationalTradingLimitProfile.Mode(run.Profile), accountId.Trim());

    // Use the same reason as the transactional reservation and first-POST boundary.
    public string? Refusal(AccountLossState state, DateOnly session) =>
        AccountLossLimitPolicy.Refusal(state, options, session, timeProvider.GetUtcNow());

    // Reject aged or future facts on both sides of the potentially slow history lookup.
    private void RequireCurrentAccount(BrokerAccountSnapshot account, DateTimeOffset now)
    {
        if (account.RequestedAtUtc == default || account.ObservedAtUtc < account.RequestedAtUtc ||
            account.ObservedAtUtc > now || now - account.RequestedAtUtc > options.MaximumSnapshotAge)
        {
            throw new InvalidDataException("Authoritative marked account equity is stale or has an invalid observation clock.");
        }
    }
}

using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Engine.Execution;

public interface IAccountLossReviewService
{
    Task<AccountLossReviewSnapshot> GetAsync(CancellationToken token);
    Task<AccountLossReviewSnapshot> ReviewAsync(AccountLossReviewRequest request, string actor, CancellationToken token);
}

/// <summary>Obtains review inputs from the bound paper broker; callers cannot choose equity or account identity.</summary>
public sealed class AccountLossReviewService(IAccountLossReviewStore store, IBrokerClient broker,
    IBrokerAccountBindingService binding, AccountLossGuardOptions options,
    ReconciliationRunContext runContext, TimeProvider clock) : IAccountLossReviewService
{
    // Read current durable review state only after confirming the configured broker account.
    public async Task<AccountLossReviewSnapshot> GetAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.MaximumSnapshotAge);
        var account = await broker.GetAccountSnapshotAsync(deadline.Token);
        binding.Validate(account);
        return await store.GetReviewAsync(Scope(account.AccountId), deadline.Token);
    }

    // Fetch fresh archived facts before the atomic revision check; never call the ordinary observation path here.
    public async Task<AccountLossReviewSnapshot> ReviewAsync(AccountLossReviewRequest request, string actor, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(options.MaximumSnapshotAge);
        var account = await broker.GetAccountSnapshotAsync(deadline.Token);
        binding.Validate(account);
        RequireFresh(account);
        var scope = Scope(account.AccountId);
        var previous = await store.GetReviewAsync(scope, deadline.Token);
        if (previous.Revision != request.ExpectedRevision || previous.State is null)
            throw new InvalidOperationException("Account loss state changed. Reload the review.");
        var source = broker as IBrokerAccountLossEvidenceProvider
            ?? throw new InvalidOperationException("The broker cannot provide archived loss evidence.");
        var evidence = await source.GetAccountLossEvidenceAsync(account, previous.State.Session, deadline.Token);
        RequireFresh(account);
        return await store.ReviewAsync(scope, runContext.Run,
            new(request.ExpectedRevision, request.Action, actor, request.Reason),
            new(account.RequestedAtUtc, account.ObservedAtUtc, account.Equity, evidence, null),
            options, clock.GetUtcNow(), deadline.Token);
    }

    // A reset never targets a caller-supplied account or non-paper execution mode.
    private AccountLossScope Scope(string accountId)
    {
        if (OperationalTradingLimitProfile.Mode(runContext.Run.Profile) != "paper")
            throw new InvalidOperationException("Loss review is available only for paper mode.");
        return new(options.Provider, "paper", accountId.Trim());
    }

    // Check request age again after a slow history lookup, not merely the account response's timestamp.
    private void RequireFresh(BrokerAccountSnapshot account)
    {
        var now = clock.GetUtcNow();
        if (account.RequestedAtUtc == default || account.ObservedAtUtc < account.RequestedAtUtc ||
            account.ObservedAtUtc > now || now - account.RequestedAtUtc > options.MaximumSnapshotAge)
            throw new InvalidOperationException("Fresh broker equity is required for a reset.");
    }
}

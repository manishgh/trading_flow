using TradingFlow.Data.Context;
using TradingFlow.Domain.Execution;

namespace TradingFlow.Data.Orders;

public sealed partial class SqliteOrderIntentRepository
{
    // Run in the caller's immediate SQLite transaction so a breach observed by
    // another entry, reconciliation or a peer process cannot slip past first POST.
    private async Task RequireAccountLossPermissionAsync(
        TradingFlowDbContext context, string profile, string accountId, DateOnly session,
        Guid intentId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (accountLossOptions is null)
        {
            return;
        }

        var scope = new AccountLossScope(accountLossOptions.Provider, OperationalTradingLimitProfile.Mode(profile), accountId.Trim());
        var state = await SqliteAccountLossStateStore.ReadAsync(context, scope, cancellationToken);
        if (AccountLossLimitPolicy.Refusal(state, accountLossOptions, session, now) is { } refusal)
        {
            throw RiskRejected(intentId, "account_loss_entry_blocked", refusal);
        }
    }
}

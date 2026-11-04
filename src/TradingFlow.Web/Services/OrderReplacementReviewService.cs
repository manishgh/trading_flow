using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Services;

public sealed record ReplacementCancelRequest(string ClientOrderId, string IdempotencyKey);
public sealed record ReplacementReview(bool CanReview, string State, string Detail, string? ReviewUrl, bool CanRelease = false);

/// <summary>Cancel then open a fresh review. Never submits or re-arms a replacement entry.</summary>
public sealed class OrderReplacementReviewService(IOrderWorkflowService workflow, IOrderIntentRepository intents,
    IOrderEventRepository events, IReviewedOrderBroker broker, BrokerAccountBindingOptions binding)
{
    public async Task<ReplacementReview> CancelAsync(ReplacementCancelRequest request, CancellationToken token)
    {
        var intent = await RequireIntentAsync(request.ClientOrderId, token);
        var current = await events.GetCurrentAsync(intent.ClientOrderId, token);
        RequireNoFill(current);
        if (current!.State != OrderState.Canceled) {
            var outcome = await workflow.CancelAsync(new(ContractVersions.VersionOne, intent.AccountId, intent.IntentId,
                "Operator requested cancel before fresh replacement review.", request.IdempotencyKey), token);
            if (outcome?.RejectionCode is "command_expired" or "broker_identity_missing")
                return new(false,outcome.State,outcome.RejectionReason ?? "Cancellation was rejected before dispatch. Review a new cancellation separately.",null,true);
            if (outcome?.RejectionCode is not null)
                return new(false,outcome.State,outcome.RejectionReason ?? "Cancellation could not be resolved. Reconcile the original order before continuing.",null);
        }
        return await ReviewAsync(intent.ClientOrderId, token);
    }

    public async Task<ReplacementReview> ReviewAsync(string clientOrderId, CancellationToken token)
    {
        var intent = await RequireIntentAsync(clientOrderId, token);
        var current = await events.GetCurrentAsync(clientOrderId, token);
        if (current is null) return new(false,"unknown","The authoritative journal state is unavailable.",null);
        if (current.FilledQuantity is > 0 || current.State == OrderState.Filled)
            return new(false,current.State.ToString(),"The order filled or partially filled. Review the current position instead of replacing it.",null,OrderStateMachine.IsTerminal(current.State));
        if (current.State is OrderState.Rejected or OrderState.Expired)
            return new(false,current.State.ToString(),"The original order is terminal. End this replacement review and inspect the journal.",null,true);
        if (current.State != OrderState.Canceled)
            return new(false,current.State.ToString(),"Wait for authoritative Canceled with zero fills. A cancellation request is not broker acknowledgment.",null);
        RequireNoFill(current);
        // This adapter issues fresh account and position GETs, without the desk's cached holdings.
        var account = await broker.GetBrokerContextAsync(intent.Symbol, token);
        if (account.AccountId != intent.AccountId || !account.AssetActive || !account.AssetTradable)
            throw new InvalidOperationException("Account binding or asset eligibility changed. Review remains unavailable.");
        if (intent.Side == "sell" && account.OpenPositionQuantity <= 0)
            throw new InvalidOperationException("The long position is no longer available for a replacement exit.");
        var final = await events.GetCurrentAsync(clientOrderId, token);
        RequireNoFill(final);
        if (final!.State != OrderState.Canceled) return new(false,final.State.ToString(),"The order changed during the account refresh; review stopped.",null);
        var symbol = Uri.EscapeDataString(intent.Symbol);
        var url = intent.Side == "sell" ? $"/OrderTicket?env=paper&ticker={symbol}&side=sell" : $"/Desk?search={symbol}&order=buy";
        return new(true,"Canceled","Cancellation acknowledged with zero fills; account and position refreshed. Review new evidence and prices. A consumed candidate cannot be reused. Nothing has been submitted.",url,true);
    }

    private async Task<OrderIntentRecord> RequireIntentAsync(string clientOrderId, CancellationToken token)
    {
        var intent = await intents.GetByClientOrderIdAsync(clientOrderId, token)
            ?? throw new InvalidOperationException("The original order intent is unavailable.");
        if (!binding.EnforcementEnabled || intent.AccountId != binding.ExpectedAccountId)
            throw new InvalidOperationException("The original order does not belong to the configured paper account.");
        if (intent.Kind is not (OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry or OrderIntentKind.PositionExit))
            throw new InvalidOperationException("Protective orders must use the position protection workflow, not Replace.");
        return intent;
    }
    private static void RequireNoFill(OrderStateSnapshot? current)
    {
        if (current is null || current.FilledQuantity is not 0m || current.State is OrderState.Filled or OrderState.Rejected or OrderState.Expired)
            throw new InvalidOperationException("Replace requires known zero cumulative fills and a working or canceled original order.");
    }
}

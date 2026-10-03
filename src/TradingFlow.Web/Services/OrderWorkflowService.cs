using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;
using TradingFlow.Data.Identity;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Execution;

namespace TradingFlow.Web.Services;

public interface IOrderWorkflowService
{
    Task<OrderPreviewResponse> PreviewAsync(PreviewOrderRequest request, CancellationToken cancellationToken = default);
    Task<OrderCommandResponse> ConfirmAsync(ConfirmOrderRequest request, CancellationToken cancellationToken = default);
    Task<OrderCommandResponse> CancelAsync(CancelOrderRequest request, CancellationToken cancellationToken = default);
    Task<OrderCommandResponse> CloseAsync(ClosePositionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Durable review/command orchestration; broker admission remains in the shared engine.</summary>
public sealed class OrderWorkflowService(
    ManualOrderTicketService tickets,
    IReviewedOrderBroker broker,
    ICandidateRepository candidates,
    IUniversePreviewRepository universes,
    IStrategyIdentityResolver strategies,
    IOrderPreviewRepository previews,
    IOrderIntentRepository intents,
    IOrderEventRepository orderEvents,
    BrokerAccountBindingOptions accountBinding,
    IHttpContextAccessor operatorContext,
    TimeProvider timeProvider) : IOrderWorkflowService
{
    private static readonly TimeSpan ConfirmationLease = TimeSpan.FromSeconds(20);

    public async Task<OrderPreviewResponse> PreviewAsync(PreviewOrderRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        request = request with { Symbol = request.Symbol.Trim().ToUpperInvariant(), IdempotencyKey = request.IdempotencyKey.Trim(),
            Strategy = request.Strategy with { ContentSha256 = request.Strategy.ContentSha256.ToLowerInvariant() } };
        RequireAccount(request.AccountId);
        RequirePolicy(request.ExecutionPolicy);
        var requestJson = JsonSerializer.Serialize(request);
        var existing = await previews.FindByKeyAsync(request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            RequireSameRequest(existing, requestJson);
            return Read<OrderPreviewResponse>(existing.PreviewJson);
        }
        await ValidateBindingsAsync(request, cancellationToken);
        var draft = new ManualOrderDraft(request.Symbol, request.Side, request.Quantity, request.LimitPrice!.Value,
            request.StopLossPrice, request.TakeProfitPrice, "swing", request.AllowExtendedHoursTrading);
        var manual = await tickets.PreviewWorkflowAsync(draft, request.ExecutionPolicy == "strategy_validated", Sha256(requestJson), cancellationToken);
        if (!manual.CanSubmit || String.IsNullOrWhiteSpace(manual.TicketToken) || manual.QuoteTimestampUtc is null)
            throw new InvalidOperationException(String.Join("; ", manual.Rejections));
        var response = new OrderPreviewResponse(ContractVersions.VersionOne, manual.TicketId, manual.TicketToken,
            manual.CreatedAtUtc, manual.ExpiresAtUtc, request.AccountId, request.CandidateId, request.CandidateVersion,
            request.UniverseSnapshotId, request.Strategy, request.Symbol, "buy", request.Quantity, "limit", "day",
            request.LimitPrice, request.StopLossPrice, request.TakeProfitPrice, manual.Notional,
            (request.LimitPrice.Value - request.StopLossPrice) * request.Quantity,
            Sha256(JsonSerializer.Serialize(new { feed = "sip", request.Symbol, manual.QuoteTimestampUtc, manual.BidPrice, manual.AskPrice })),
            manual.QuoteTimestampUtc.Value, manual.RiskSnapshotSha256, request.ExecutionPolicy, request.IdempotencyKey, []);
        var saved = await previews.SaveAsync(new OrderPreviewRecord
        {
            PreviewId = response.PreviewId, TokenSha256 = Sha256(response.PreviewToken), RequestSha256 = Sha256(requestJson),
            IdempotencyKey = request.IdempotencyKey, CreatedAtUtc = response.CreatedAtUtc, ExpiresAtUtc = response.ExpiresAtUtc,
            Status = "previewed", RequestJson = requestJson, PreviewJson = JsonSerializer.Serialize(response)
        }, cancellationToken);
        return Read<OrderPreviewResponse>(saved.PreviewJson);
    }

    public async Task<OrderCommandResponse> ConfirmAsync(ConfirmOrderRequest request, CancellationToken cancellationToken = default)
    {
        RequireCommand(request.ContractVersion, request.IdempotencyKey);
        if (String.IsNullOrWhiteSpace(request.PreviewToken)) throw new InvalidOperationException("Preview token is required.");
        var tokenHash = Sha256(request.PreviewToken);
        var stored = await previews.GetByTokenHashAsync(tokenHash, cancellationToken)
            ?? throw new InvalidOperationException("Order preview was not found.");
        var bound = Read<PreviewOrderRequest>(stored.RequestJson);
        RequireSameRequest(stored, JsonSerializer.Serialize(bound));
        RequireAccount(bound.AccountId);
        RequirePolicy(bound.ExecutionPolicy); // Reauthorize at confirmation, including retries.
        var review = Read<OrderPreviewResponse>(stored.PreviewJson);
        if (Sha256(review.PreviewToken) != stored.TokenSha256 || review.PreviewId != stored.PreviewId ||
            review.ExpiresAtUtc != stored.ExpiresAtUtc || review.AccountId != bound.AccountId ||
            review.ExecutionPolicy != bound.ExecutionPolicy || review.IdempotencyKey != bound.IdempotencyKey)
            throw new InvalidOperationException("Order preview integrity failed.");
        var claim = await previews.ClaimConfirmationAsync(tokenHash, request.IdempotencyKey.Trim(), timeProvider.GetUtcNow(), ConfirmationLease, cancellationToken);
        if (!claim.OwnsConfirmation)
            return await ExistingOutcomeAsync(claim.Preview, bound.AccountId, bound.Symbol, "buy", bound.Quantity, cancellationToken);
        var submitted = false;
        try
        {
            if (stored.ExpiresAtUtc <= timeProvider.GetUtcNow()) throw new InvalidOperationException("Order preview expired; review again.");
            var binding = await ValidateBindingsAsync(bound, cancellationToken);
            await tickets.RevalidateWorkflowAsync(request.PreviewToken, bound.ExecutionPolicy == "strategy_validated", stored.RequestSha256, review.RiskSnapshotVersion, cancellationToken);
            // EntryGateChain then rechecks candidate version, strategy authorization,
            // quote, account and portfolio risk before atomic reservation/consumption.
            if (!await previews.TryBeginSubmissionAsync(stored.PreviewId, claim.LeaseToken!.Value, timeProvider.GetUtcNow(), cancellationToken))
                return await ExistingOutcomeAsync(claim.Preview, bound.AccountId, bound.Symbol, "buy", bound.Quantity, cancellationToken);
            submitted = true;
            if (bound.ExecutionPolicy == "strategy_validated")
                await broker.SubmitStrategyEntryAsync(bound, stored.PreviewId, binding.Candidate!, binding.Run!, cancellationToken);
            else
                await broker.SubmitLimitOrderAsync(bound.Symbol, "buy", bound.Quantity, bound.LimitPrice!.Value, bound.StopLossPrice,
                    bound.TakeProfitPrice, "swing", cancellationToken, bound.AllowExtendedHoursTrading, stored.PreviewId);
            return await ExistingOutcomeAsync(claim.Preview, bound.AccountId, bound.Symbol, "buy", bound.Quantity, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            if (submitted && exception is not EntryGateRejectedException)
                return await ExistingOutcomeAsync(claim.Preview, bound.AccountId, bound.Symbol, "buy", bound.Quantity, cancellationToken);
            return await FinishAsync(claim.Preview, Rejected(stored, bound.AccountId, bound.Symbol, "buy", bound.Quantity,
                "preview_revalidation_failed", exception.Message), cancellationToken);
        }
        // Cancellation/transport exceptions leave confirming intact for reconciliation.
    }

    public async Task<OrderCommandResponse> CancelAsync(CancelOrderRequest request, CancellationToken cancellationToken = default)
    {
        RequireCommand(request.ContractVersion, request.IdempotencyKey);
        RequireAccount(request.AccountId);
        if (request.OrderIntentId == Guid.Empty || String.IsNullOrWhiteSpace(request.Reason)) throw new InvalidOperationException("Order and cancellation reason are required.");
        request = request with { IdempotencyKey = request.IdempotencyKey.Trim() };
        var intent = await intents.GetByIntentIdAsync(request.OrderIntentId, cancellationToken) ?? throw new InvalidOperationException("Order intent was not found.");
        if (intent.AccountId != request.AccountId) throw new InvalidOperationException("Order belongs to another account.");
        var claim = await BeginCommandAsync(request.IdempotencyKey, JsonSerializer.Serialize(request), cancellationToken);
        if (claim.Preview.OutcomeJson is not null) return Read<OrderCommandResponse>(claim.Preview.OutcomeJson);
        var current = await orderEvents.GetCurrentAsync(intent.ClientOrderId, cancellationToken) ?? throw new InvalidOperationException("Order state is unavailable.");
        if (OrderStateMachine.IsTerminal(current.State)) return await FinishAsync(claim.Preview, ToCommand(intent, current, request.IdempotencyKey), cancellationToken);
        if (!claim.OwnsConfirmation) return Pending(claim.Preview, intent.AccountId, intent.Symbol, intent.Side, intent.RequestedQuantity);
        if (claim.Preview.ExpiresAtUtc <= timeProvider.GetUtcNow())
            return await FinishAsync(claim.Preview, Rejected(claim.Preview, intent.AccountId, intent.Symbol, intent.Side, intent.RequestedQuantity,
                "command_expired", "Undispatched cancellation expired. Review and issue a new command."), cancellationToken);
        if (String.IsNullOrWhiteSpace(current.BrokerOrderId))
            return await FinishAsync(claim.Preview, Rejected(claim.Preview, intent.AccountId, intent.Symbol, intent.Side, intent.RequestedQuantity,
                "broker_identity_missing", "Order has no broker identity to cancel."), cancellationToken);
        if (!await previews.TryBeginSubmissionAsync(claim.Preview.PreviewId, claim.LeaseToken!.Value, timeProvider.GetUtcNow(), cancellationToken))
            return Pending(claim.Preview, intent.AccountId, intent.Symbol, intent.Side, intent.RequestedQuantity);
        await broker.CancelOrderAsync(current.BrokerOrderId, cancellationToken);
        current = await orderEvents.GetCurrentAsync(intent.ClientOrderId, cancellationToken) ?? current;
        return await FinishAsync(claim.Preview, ToCommand(intent, current, request.IdempotencyKey), cancellationToken);
    }

    public async Task<OrderCommandResponse> CloseAsync(ClosePositionRequest request, CancellationToken cancellationToken = default)
    {
        RequireCommand(request.ContractVersion, request.IdempotencyKey);
        RequireAccount(request.AccountId);
        if (String.IsNullOrWhiteSpace(request.Symbol) || String.IsNullOrWhiteSpace(request.Reason) || request.Quantity is <= 0m ||
            request.OrderType is not ("market" or "limit" or "stop" or "stop_limit") || request.TimeInForce is not ("day" or "gtc" or "ioc" or "fok") ||
            (request.OrderType is "limit" or "stop_limit" && request.LimitPrice is not > 0m) ||
            (request.OrderType is "stop" or "stop_limit" && request.TriggerPrice is not > 0m) ||
            (request.AllowExtendedHoursTrading && (request.OrderType != "limit" || request.TimeInForce != "day")))
            throw new InvalidOperationException("Position symbol, reason, valid order prices, time in force and positive optional quantity are required.");
        request = request with { Symbol = request.Symbol.Trim().ToUpperInvariant(), IdempotencyKey = request.IdempotencyKey.Trim() };
        var claim = await BeginCommandAsync(request.IdempotencyKey, JsonSerializer.Serialize(request), cancellationToken);
        if (!claim.OwnsConfirmation)
            return await ExistingOutcomeAsync(claim.Preview, request.AccountId, request.Symbol, "sell", request.Quantity ?? 0m, cancellationToken);
        var submitted = false;
        try
        {
            if (claim.Preview.ExpiresAtUtc <= timeProvider.GetUtcNow())
                return await FinishAsync(claim.Preview, Rejected(claim.Preview, request.AccountId, request.Symbol, "sell", request.Quantity ?? 0m,
                    "command_expired", "Undispatched close expired. Review and issue a new command."), cancellationToken);
            var context = await broker.GetBrokerContextAsync(request.Symbol, cancellationToken);
            if (context.AccountId != request.AccountId) throw new InvalidOperationException("Connected broker account changed.");
            var quantity = request.Quantity ?? context.OpenPositionQuantity;
            if (quantity <= 0m || quantity > context.OpenPositionQuantity || quantity != Decimal.Truncate(quantity))
                throw new InvalidOperationException("Close quantity must be whole shares within the open long position.");
            if (!await previews.TryBeginSubmissionAsync(claim.Preview.PreviewId, claim.LeaseToken!.Value, timeProvider.GetUtcNow(), cancellationToken))
                return await ExistingOutcomeAsync(claim.Preview, request.AccountId, request.Symbol, "sell", quantity, cancellationToken);
            submitted = true;
            await broker.SubmitLimitOrderAsync(request.Symbol, "sell", quantity, request.LimitPrice ?? 0m, null, null,
                "swing", cancellationToken, request.AllowExtendedHoursTrading, claim.Preview.PreviewId,
                request.OrderType, request.TriggerPrice, request.TimeInForce);
            return await ExistingOutcomeAsync(claim.Preview, request.AccountId, request.Symbol, "sell", quantity, cancellationToken);
        }
        catch (InvalidOperationException exception) when (!submitted)
        {
            return await FinishAsync(claim.Preview, Rejected(claim.Preview, request.AccountId, request.Symbol, "sell", request.Quantity ?? 0m,
                "close_revalidation_failed", exception.Message), cancellationToken);
        }
    }

    private async Task<(CandidateRecord? Candidate, ProductionRun? Run)> ValidateBindingsAsync(PreviewOrderRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var record = await universes.GetAsync(request.UniverseSnapshotId, cancellationToken) ?? throw new InvalidOperationException("Universe preview is missing.");
        var universe = UniverseDiscoveryService.ReadPreview(record, request.UniverseSnapshotId);
        if (universe.ExpiresAtUtc <= now || universe.ResolvedAtUtc > now ||
            !universe.Members.Any(member => member.Symbol == request.Symbol && member.Sources.Any(source => !source.IsDiagnostic && source.ExpiresAtUtc > now)))
            throw new InvalidOperationException("Universe is expired or does not contain the symbol.");
        if (request.ExecutionPolicy == "operator_override")
        {
            if (request.CandidateId is not null || request.CandidateRunId is not null || request.CandidateVersion is not null)
                throw new InvalidOperationException("Operator override cannot claim a strategy candidate.");
            await strategies.RequireSwingStrategyAsync(request.Strategy, "backtest", cancellationToken);
            return (null, null);
        }
        if (request.CandidateId is not { } id || request.CandidateRunId is null || request.CandidateVersion is null)
            throw new InvalidOperationException("Strategy entry requires candidate, version and run.");
        var candidate = await candidates.GetAsync(id, cancellationToken) ?? throw new InvalidOperationException("Candidate is missing.");
        var run = await intents.GetRunAsync(candidate.RunId, cancellationToken) ?? throw new InvalidOperationException("Candidate run is missing.");
        var mode = run.Profile switch { "paper" or "paper_shadow" => "paper_shadow", "paper_experiment" => "paper_experiment", _ => throw new InvalidOperationException("An authorized paper candidate run is required.") };
        await strategies.RequireSwingStrategyAsync(request.Strategy, mode, cancellationToken);
        if (candidate.Version != request.CandidateVersion || candidate.State != StrategyCandidateState.Triggered || candidate.ExpiresAtUtc <= now ||
            candidate.Symbol != request.Symbol || candidate.SelectedStrategy != request.Strategy.StrategyId ||
            candidate.StrategySemanticVersion != request.Strategy.SemanticVersion || candidate.StrategyContentSha256 != request.Strategy.ContentSha256 ||
            candidate.RunId != request.CandidateRunId || run.UniverseSnapshotId != request.UniverseSnapshotId)
            throw new InvalidOperationException("Candidate identity, strategy, version or readiness changed.");
        return (candidate, run);
    }

    private async Task<OrderPreviewConfirmationClaim> BeginCommandAsync(string key, string json, CancellationToken cancellationToken)
    {
        var stored = await previews.FindByKeyAsync(key, cancellationToken);
        if (stored is null)
        {
            var now = timeProvider.GetUtcNow();
            stored = await previews.SaveAsync(new OrderPreviewRecord { PreviewId = Guid.NewGuid(), IdempotencyKey = key,
                TokenSha256 = Sha256("command:" + key), RequestSha256 = Sha256(json), RequestJson = json, PreviewJson = "{}",
                Status = "previewed", CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(2) }, cancellationToken);
        }
        RequireSameRequest(stored, json);
        return await previews.ClaimConfirmationAsync(stored.TokenSha256, key, timeProvider.GetUtcNow(), ConfirmationLease, cancellationToken);
    }

    private async Task<OrderCommandResponse> ExistingOutcomeAsync(OrderPreviewRecord preview, string account, string symbol, string side, decimal quantity, CancellationToken cancellationToken)
    {
        var latest = await previews.GetByTokenHashAsync(preview.TokenSha256, cancellationToken) ?? preview;
        if (latest.OutcomeJson is not null) return Read<OrderCommandResponse>(latest.OutcomeJson);
        var intent = await intents.GetByIntentIdAsync(preview.PreviewId, cancellationToken);
        if (intent is null && side == "sell")
            intent = (await intents.ListByRunAsync(preview.PreviewId, cancellationToken)).SingleOrDefault(item => item.Kind == OrderIntentKind.PositionExit);
        if (intent is null) return Pending(latest, account, symbol, side, quantity);
        if (intent.AccountId != account || intent.Symbol != symbol) throw new InvalidOperationException("Durable order identity differs from the command.");
        var state = await orderEvents.GetCurrentAsync(intent.ClientOrderId, cancellationToken);
        if (state is null || (String.IsNullOrWhiteSpace(state.BrokerOrderId) && !OrderStateMachine.IsTerminal(state.State)))
            return Pending(latest, account, symbol, side, quantity) with { OrderIntentId = intent.IntentId, ClientOrderId = intent.ClientOrderId };
        return await FinishAsync(latest, ToCommand(intent, state, preview.IdempotencyKey), cancellationToken);
    }

    private async Task<OrderCommandResponse> FinishAsync(OrderPreviewRecord preview, OrderCommandResponse outcome, CancellationToken cancellationToken)
    {
        var saved = await previews.CompleteAsync(preview.PreviewId,
            preview.ConfirmationLeaseToken ?? throw new InvalidOperationException("Confirmation owner is unavailable."),
            outcome.State == "rejected" ? "rejected" : "confirmed", JsonSerializer.Serialize(outcome), timeProvider.GetUtcNow(), cancellationToken);
        return Read<OrderCommandResponse>(saved.OutcomeJson!);
    }

    private static OrderCommandResponse ToCommand(OrderIntentRecord intent, OrderStateSnapshot state, string key) => new(
        ContractVersions.VersionOne, intent.IntentId, intent.ClientOrderId, state.BrokerOrderId, state.State.ToStorageValue(),
        intent.AccountId, intent.Symbol, intent.Side, intent.RequestedQuantity, state.FilledQuantity, state.FillPrice,
        intent.CreatedAtUtc, state.LocalTimestampUtc, key, null, null);
    private static OrderCommandResponse Pending(OrderPreviewRecord preview, string account, string symbol, string side, decimal quantity) => new(
        ContractVersions.VersionOne, preview.PreviewId, String.Empty, null, "confirmation_pending", account, symbol, side, quantity,
        null, null, preview.CreatedAtUtc, preview.CreatedAtUtc, preview.IdempotencyKey, "reconciliation_required", "Confirmation is pending durable execution reconciliation; retry the same command.");
    private OrderCommandResponse Rejected(OrderPreviewRecord preview, string account, string symbol, string side, decimal quantity, string code, string reason) =>
        Pending(preview, account, symbol, side, quantity) with { State = "rejected", UpdatedAtUtc = timeProvider.GetUtcNow(), RejectionCode = code, RejectionReason = reason };
    private void RequireAccount(string account)
    {
        if (!accountBinding.EnforcementEnabled || String.IsNullOrWhiteSpace(account) || account != accountBinding.ExpectedAccountId)
            throw new InvalidOperationException("Order account must match the configured broker account.");
    }
    private void RequirePolicy(string policy)
    {
        if (policy is not ("operator_override" or "strategy_validated")) throw new InvalidOperationException("Execution policy is invalid.");
        if (policy == "operator_override" && operatorContext.HttpContext?.User.IsInRole(TradingFlowRoles.Administrator) != true)
            throw new UnauthorizedAccessException("Administrator authorization is required for operator override.");
    }
    private static void RequireCommand(string contract, string key)
    {
        if (contract != ContractVersions.VersionOne || String.IsNullOrWhiteSpace(key) || key.Trim().Length > 160)
            throw new InvalidOperationException("Supported contract and idempotency key are required.");
    }
    private static void ValidateRequest(PreviewOrderRequest request)
    {
        RequireCommand(request.ContractVersion, request.IdempotencyKey);
        if (request.Strategy is null || String.IsNullOrWhiteSpace(request.Strategy.StrategyId) || String.IsNullOrWhiteSpace(request.Strategy.SemanticVersion) ||
            request.Strategy.ContentSha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit) ||
            request.UniverseSnapshotId == Guid.Empty || String.IsNullOrWhiteSpace(request.Symbol) || request.Quantity <= 0m ||
            request.Quantity != Decimal.Truncate(request.Quantity) || request.Quantity > Int32.MaxValue || request.Side != "buy" ||
            request.OrderType != "limit" || request.TimeInForce != "day" || request.LimitPrice is not > 0m || request.StopLossPrice <= 0m ||
            request.StopLossPrice >= request.LimitPrice || request.TakeProfitPrice is not > 0m || request.TakeProfitPrice <= request.LimitPrice)
            throw new InvalidOperationException("Review requires an exact strategy and a protected swing long limit/day order.");
    }
    private static T Read<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException("Persisted command is invalid."); }
        catch (JsonException exception) { throw new InvalidOperationException("Persisted command is invalid.", exception); }
    }
    private static void RequireSameRequest(OrderPreviewRecord preview, string json)
    {
        if (Sha256(preview.RequestJson) != preview.RequestSha256 || Sha256(json) != preview.RequestSha256)
            throw new IdempotencyKeyConflictException(preview.IdempotencyKey);
    }
    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingFlow.Application.Candidates;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;
using TradingFlow.Domain.Strategies;

namespace TradingFlow.Web.Services;

public interface IOrderWorkflowService
{
    Task<OrderPreviewResponse> PreviewAsync(PreviewOrderRequest request, CancellationToken cancellationToken = default);
    Task<OrderCommandResponse> ConfirmAsync(ConfirmOrderRequest request, CancellationToken cancellationToken = default);
    Task<OrderCommandResponse> CancelAsync(CancelOrderRequest request, CancellationToken cancellationToken = default);
    Task<OrderCommandResponse> CloseAsync(ClosePositionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Durable API-facing order workflow. The existing broker gateway still owns
/// Alpaca-specific validation and submission, while this service owns immutable
/// preview binding, candidate/strategy revalidation and idempotent confirmation.
/// </summary>
public sealed class OrderWorkflowService(
    ManualOrderTicketService tickets,
    AlpacaManualOrderService broker,
    ICandidateRepository candidates,
    IUniversePreviewRepository universes,
    IStrategyIdentityResolver strategies,
    IOrderPreviewRepository previews,
    IOrderIntentRepository intents,
    IOrderEventRepository orderEvents,
    TradingFlow.Engine.Execution.BrokerAccountBindingOptions accountBinding,
    TimeProvider timeProvider) : IOrderWorkflowService
{
    private static readonly TimeSpan ConfirmationLease = TimeSpan.FromSeconds(20);

    public async Task<OrderPreviewResponse> PreviewAsync(
        PreviewOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        await ValidateBindingsAsync(request, timeProvider.GetUtcNow(), cancellationToken);
        var manual = await tickets.PreviewAsync(new ManualOrderDraft(
            request.Symbol,
            request.Side,
            request.Quantity,
            request.LimitPrice ?? 0m,
            request.StopLossPrice,
            request.TakeProfitPrice,
            "swing",
            request.AllowExtendedHoursTrading,
            OrderType: request.OrderType,
            TimeInForce: request.TimeInForce), cancellationToken);
        if (!manual.CanSubmit || String.IsNullOrWhiteSpace(manual.TicketToken))
        {
            throw new InvalidOperationException(String.Join("; ", manual.Rejections));
        }

        var quoteIdentity = $"sip:{manual.Ticker}:{manual.QuoteTimestampUtc?.UtcTicks}:{manual.BidPrice}:{manual.AskPrice}";
        var riskVersion = Sha256(JsonSerializer.Serialize(new
        {
            manual.Environment,
            manual.Notional,
            manual.StopLossPrice,
            manual.TakeProfitPrice,
            manual.Session
        }));
        var response = new OrderPreviewResponse(
            ContractVersions.VersionOne,
            manual.TicketId,
            manual.TicketToken,
            manual.CreatedAtUtc,
            manual.ExpiresAtUtc,
            request.AccountId,
            request.CandidateId,
            request.CandidateVersion,
            request.UniverseSnapshotId,
            request.Strategy,
            manual.Ticker,
            manual.Side,
            manual.Quantity,
            manual.OrderType,
            manual.TimeInForce.ToLowerInvariant(),
            request.LimitPrice,
            request.StopLossPrice,
            request.TakeProfitPrice,
            manual.Notional,
            Math.Abs((request.LimitPrice ?? manual.LimitPrice) - request.StopLossPrice) * request.Quantity,
            quoteIdentity,
            manual.QuoteTimestampUtc ?? throw new InvalidOperationException("Preview quote timestamp is required."),
            riskVersion,
            request.ExecutionPolicy,
            request.IdempotencyKey,
            []);
        var requestJson = JsonSerializer.Serialize(request);
        var stored = await previews.SaveAsync(new OrderPreviewRecord
        {
            PreviewId = response.PreviewId,
            TokenSha256 = Sha256(response.PreviewToken),
            RequestSha256 = Sha256(requestJson),
            IdempotencyKey = request.IdempotencyKey,
            CreatedAtUtc = response.CreatedAtUtc,
            ExpiresAtUtc = response.ExpiresAtUtc,
            Status = "previewed",
            RequestJson = requestJson,
            PreviewJson = JsonSerializer.Serialize(response),
            Version = 0
        }, cancellationToken);
        return JsonSerializer.Deserialize<OrderPreviewResponse>(stored.PreviewJson)
            ?? throw new InvalidOperationException("Persisted order preview could not be decoded.");
    }

    public async Task<OrderCommandResponse> ConfirmAsync(
        ConfirmOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!String.Equals(request.ContractVersion, ContractVersions.VersionOne, StringComparison.Ordinal) ||
            String.IsNullOrWhiteSpace(request.PreviewToken) || String.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new InvalidOperationException("Contract version, preview token and idempotency key are required.");
        }
        var tokenHash = Sha256(request.PreviewToken);
        var claim = await previews.ClaimConfirmationAsync(
            tokenHash,
            request.IdempotencyKey,
            timeProvider.GetUtcNow(),
            ConfirmationLease,
            cancellationToken);
        if (!claim.OwnsConfirmation)
        {
            return await ReadFinalOutcomeAsync(tokenHash, cancellationToken);
        }

        var previewRequest = JsonSerializer.Deserialize<PreviewOrderRequest>(claim.Preview.RequestJson)
            ?? throw new InvalidOperationException("Persisted order preview request is invalid.");
        if (!Sha256(claim.Preview.RequestJson).Equals(claim.Preview.RequestSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Persisted order preview request failed integrity validation.");
        }
        try
        {
            await ValidateBindingsAsync(previewRequest, timeProvider.GetUtcNow(), cancellationToken);
            var submitted = await tickets.ConfirmAsync(request.PreviewToken, cancellationToken);
            var now = timeProvider.GetUtcNow();
            var outcome = new OrderCommandResponse(
                ContractVersions.VersionOne,
                claim.Preview.PreviewId,
                claim.Preview.PreviewId.ToString("N"),
                submitted.OrderId,
                "acknowledged",
                previewRequest.AccountId,
                submitted.Ticker,
                submitted.Side,
                submitted.Quantity,
                null,
                null,
                claim.Preview.CreatedAtUtc,
                now,
                previewRequest.IdempotencyKey,
                null,
                null);
            await previews.CompleteAsync(
                claim.Preview.PreviewId,
                claim.LeaseToken!.Value,
                "confirmed",
                JsonSerializer.Serialize(outcome),
                now,
                cancellationToken);
            return outcome;
        }
        catch (InvalidOperationException exception)
        {
            var now = timeProvider.GetUtcNow();
            var rejected = new OrderCommandResponse(
                ContractVersions.VersionOne,
                claim.Preview.PreviewId,
                claim.Preview.PreviewId.ToString("N"),
                null,
                "rejected",
                previewRequest.AccountId,
                previewRequest.Symbol,
                previewRequest.Side,
                previewRequest.Quantity,
                null,
                null,
                claim.Preview.CreatedAtUtc,
                now,
                previewRequest.IdempotencyKey,
                "preview_revalidation_failed",
                exception.Message);
            await previews.CompleteAsync(
                claim.Preview.PreviewId,
                claim.LeaseToken!.Value,
                "rejected",
                JsonSerializer.Serialize(rejected),
                now,
                cancellationToken);
            return rejected;
        }
    }

    public async Task<OrderCommandResponse> CancelAsync(
        CancelOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        var intent = await intents.GetByIntentIdAsync(request.OrderIntentId, cancellationToken)
            ?? throw new InvalidOperationException("Order intent was not found.");
        if (!intent.AccountId.Equals(request.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Order intent belongs to a different broker account.");
        }
        var current = await orderEvents.GetCurrentAsync(intent.ClientOrderId, cancellationToken)
            ?? throw new InvalidOperationException("Order state was not found.");
        if (String.IsNullOrWhiteSpace(current.BrokerOrderId))
        {
            throw new InvalidOperationException("Order has no broker identity to cancel.");
        }
        await broker.CancelOrderAsync(current.BrokerOrderId, cancellationToken);
        var updated = await orderEvents.GetCurrentAsync(intent.ClientOrderId, cancellationToken) ?? current;
        return ToCommand(intent, updated, request.IdempotencyKey);
    }

    public async Task<OrderCommandResponse> CloseAsync(
        ClosePositionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.LimitPrice is not > 0m)
        {
            throw new InvalidOperationException("Swing position close requires an explicit limit price.");
        }
        var context = await broker.GetBrokerContextAsync(request.Symbol, cancellationToken);
        var quantity = request.Quantity ?? context.OpenPositionQuantity;
        if (quantity <= 0m || quantity > context.OpenPositionQuantity)
        {
            throw new InvalidOperationException("Close quantity must be within the open broker position.");
        }
        var intentId = DeterministicGuid($"close\u001f{request.IdempotencyKey}");
        var result = await broker.SubmitLimitOrderAsync(
            request.Symbol,
            "sell",
            quantity,
            request.LimitPrice.Value,
            null,
            null,
            "swing",
            cancellationToken,
            request.AllowExtendedHoursTrading,
            intentId,
            "limit",
            null,
            "day");
        var now = timeProvider.GetUtcNow();
        return new OrderCommandResponse(
            ContractVersions.VersionOne,
            intentId,
            intentId.ToString("N"),
            result.OrderId,
            "acknowledged",
            request.AccountId,
            result.Ticker,
            result.Side,
            result.Quantity,
            null,
            null,
            now,
            now,
            request.IdempotencyKey,
            null,
            null);
    }

    private async Task ValidateBindingsAsync(
        PreviewOrderRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var universe = await universes.GetAsync(request.UniverseSnapshotId, cancellationToken)
            ?? throw new InvalidOperationException("Universe preview was not found.");
        if (universe.ExpiresAtUtc <= now)
        {
            throw new InvalidOperationException("Universe preview expired; create a new preview.");
        }
        if (accountBinding.EnforcementEnabled &&
            !request.AccountId.Equals(accountBinding.ExpectedAccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Order account does not match the configured broker account.");
        }
        var universePayload = JsonSerializer.Deserialize<UniversePreviewResponse>(universe.PreviewJson)
            ?? throw new InvalidOperationException("Universe preview payload is invalid.");
        if (!universePayload.Members.Any(member =>
                member.Symbol.Equals(request.Symbol, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Order symbol is not a member of the bound universe preview.");
        }
        var mode = request.ExecutionPolicy.Equals("operator_override", StringComparison.OrdinalIgnoreCase)
            ? "backtest"
            : "paper_shadow";
        await strategies.RequireSwingStrategyAsync(request.Strategy, mode, cancellationToken);
        if (request.CandidateId is not { } candidateId)
        {
            if (!request.ExecutionPolicy.Equals("operator_override", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Strategy-validated execution requires a candidate identity.");
            }
            return;
        }

        var candidate = await candidates.GetAsync(candidateId, cancellationToken)
            ?? throw new InvalidOperationException("Candidate was not found.");
        var candidateRun = await intents.GetRunAsync(candidate.RunId, cancellationToken)
            ?? throw new InvalidOperationException("Candidate run was not found.");
        if (candidate.Version != request.CandidateVersion ||
            candidate.State != StrategyCandidateState.Triggered ||
            candidate.ExpiresAtUtc <= now ||
            !candidate.Symbol.Equals(request.Symbol, StringComparison.OrdinalIgnoreCase) ||
            !candidate.StrategyContentSha256.Equals(request.Strategy.ContentSha256, StringComparison.OrdinalIgnoreCase) ||
            !candidate.SelectedStrategy!.Equals(request.Strategy.StrategyId, StringComparison.Ordinal) ||
            candidate.RunId != request.CandidateRunId ||
            candidateRun.UniverseSnapshotId != request.UniverseSnapshotId)
        {
            throw new InvalidOperationException("Candidate identity, version, strategy or state changed after preview.");
        }
    }

    private async Task<OrderCommandResponse> ReadFinalOutcomeAsync(
        string tokenHash,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var current = await previews.GetByTokenHashAsync(tokenHash, cancellationToken)
                ?? throw new InvalidOperationException("Order preview token was not found.");
            if (current.Status is "confirmed" or "rejected")
            {
                OrderCommandResponse? outcome;
                try
                {
                    outcome = JsonSerializer.Deserialize<OrderCommandResponse>(current.OutcomeJson ?? String.Empty);
                }
                catch (JsonException)
                {
                    outcome = null;
                }
                if (outcome is not null)
                {
                    return outcome;
                }

                var persistedRequest = JsonSerializer.Deserialize<PreviewOrderRequest>(current.RequestJson)
                    ?? throw new InvalidOperationException("Order confirmation outcome could not be decoded.");
                return new OrderCommandResponse(
                    ContractVersions.VersionOne,
                    current.PreviewId,
                    current.PreviewId.ToString("N"),
                    null,
                    "rejected",
                    persistedRequest.AccountId,
                    persistedRequest.Symbol,
                    persistedRequest.Side,
                    persistedRequest.Quantity,
                    null,
                    null,
                    current.CreatedAtUtc,
                    timeProvider.GetUtcNow(),
                    current.IdempotencyKey,
                    "preview_expired",
                    "The immutable order preview expired before confirmation.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        throw new InvalidOperationException("Order confirmation is already in progress; retry with the same idempotency key.");
    }

    private static OrderCommandResponse ToCommand(
        OrderIntentRecord intent,
        OrderStateSnapshot state,
        string idempotencyKey) => new(
            ContractVersions.VersionOne,
            intent.IntentId,
            intent.ClientOrderId,
            state.BrokerOrderId,
            state.State.ToStorageValue(),
            intent.AccountId,
            intent.Symbol,
            intent.Side,
            intent.RequestedQuantity,
            state.FilledQuantity,
            state.FillPrice,
            intent.CreatedAtUtc,
            state.LocalTimestampUtc,
            idempotencyKey,
            null,
            null);

    private static void ValidateRequest(PreviewOrderRequest request)
    {
        if (!String.Equals(request.ContractVersion, ContractVersions.VersionOne, StringComparison.Ordinal) ||
            request.UniverseSnapshotId == Guid.Empty || String.IsNullOrWhiteSpace(request.AccountId) ||
            String.IsNullOrWhiteSpace(request.Symbol) || request.Quantity <= 0m ||
            String.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 160 ||
            !request.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) ||
            !request.OrderType.Equals("limit", StringComparison.OrdinalIgnoreCase) ||
            !request.TimeInForce.Equals("day", StringComparison.OrdinalIgnoreCase) ||
            request.LimitPrice is not > 0m || request.StopLossPrice <= 0m ||
            request.StopLossPrice >= request.LimitPrice ||
            request.TakeProfitPrice is not > 0m || request.TakeProfitPrice <= request.LimitPrice)
        {
            throw new InvalidOperationException("Order preview requires a valid swing long limit/day entry, stop, target and idempotency key.");
        }
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Guid DeterministicGuid(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}

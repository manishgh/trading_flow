using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Engine.Execution;

public sealed record BracketOrderSubmission(
    Guid IntentId,
    Guid? CandidateId,
    ExecutionRunContext RunContext,
    string StrategyId,
    string Side,
    string OrderType,
    string TimeInForce,
    DateOnly SessionDate,
    DateTimeOffset CreatedAtUtc,
    FinalizedOrder Order);

public sealed record OrderSubmissionResult(
    string BrokerOrderId,
    string ClientOrderId,
    Guid IntentId,
    DateTimeOffset BrokerAcceptedAtUtc);

public sealed record ProtectiveStopSubmission(
    Guid IntentId,
    ExecutionRunContext RunContext,
    string Symbol,
    string Side,
    decimal Quantity,
    decimal StopPrice,
    DateOnly SessionDate,
    DateTimeOffset CreatedAtUtc);

public interface IOrderSubmissionService
{
    Task<OrderSubmissionResult> SubmitBracketOrderAsync(
        BracketOrderSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken);

    Task<OrderSubmissionResult> SubmitProtectiveStopAsync(
        ProtectiveStopSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken);
}

/// <summary>
/// Implements EXE-01 ordering: reserve and fsync an immutable intent first, then call
/// the broker with the persisted client order ID. A logical retry reuses that ID.
/// </summary>
public sealed class OrderSubmissionService : IOrderSubmissionService
{
    private readonly IOrderIntentRepository intentRepository;
    private readonly IOrderEventRepository eventRepository;
    private readonly IEntryAdmissionControl entryAdmission;
    private readonly IPositionConflictGuard positionConflict;
    private readonly ILogger<OrderSubmissionService> logger;

    public OrderSubmissionService(
        IOrderIntentRepository intentRepository,
        IOrderEventRepository eventRepository,
        IEntryAdmissionControl entryAdmission,
        IPositionConflictGuard positionConflict,
        ILogger<OrderSubmissionService> logger)
    {
        this.intentRepository = intentRepository;
        this.eventRepository = eventRepository;
        this.entryAdmission = entryAdmission;
        this.positionConflict = positionConflict;
        this.logger = logger;
    }

    public async Task<OrderSubmissionResult> SubmitBracketOrderAsync(
        BracketOrderSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(brokerClient);
        Validate(submission);
        entryAdmission.EnsureEntriesAllowed();
        return await positionConflict.ExecuteEntryAsync(
            submission.Order.Ticker,
            submission.StrategyId,
            token => SubmitBracketOrderCoreAsync(submission, brokerClient, token),
            cancellationToken);
    }

    private async Task<OrderSubmissionResult> SubmitBracketOrderCoreAsync(
        BracketOrderSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        var requestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            submission.StrategyId,
            submission.Side,
            submission.OrderType,
            submission.TimeInForce,
            symbol = submission.Order.Ticker,
            quantity = submission.Order.ShareQuantity,
            limitPrice = submission.Order.LimitPrice,
            stopPrice = submission.Order.StopLossPrice,
            takeProfitPrice = submission.Order.TakeProfitPrice,
            submission.SessionDate
        });
        var run = new ProductionRun
        {
            RunId = submission.RunContext.RunId,
            SchemaVersion = 1,
            ConfigHash = submission.RunContext.ConfigHash,
            CodeVersion = submission.RunContext.CodeVersion,
            Profile = submission.RunContext.Profile,
            Status = "running",
            StartedAtUtc = submission.RunContext.StartedAtUtc
        };
        var intent = await intentRepository.ReserveAsync(
            run,
            new OrderIntentReservation(
                submission.IntentId,
                submission.CandidateId,
                submission.StrategyId,
                submission.Order.Ticker.Trim().ToUpperInvariant(),
                NormalizeSide(submission.Side),
                submission.OrderType.Trim().ToLowerInvariant(),
                submission.TimeInForce.Trim().ToLowerInvariant(),
                submission.Order.ShareQuantity,
                submission.Order.LimitPrice,
                submission.Order.StopLossPrice,
                submission.SessionDate,
                submission.CreatedAtUtc.ToUniversalTime(),
                requestJson),
            cancellationToken);

        var current = await eventRepository.GetCurrentAsync(intent.ClientOrderId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Persisted order intent '{intent.ClientOrderId}' has no lifecycle state.");
        if (current.State == OrderState.Acked)
        {
            if (String.IsNullOrWhiteSpace(current.BrokerOrderId) || current.BrokerTimestampUtc is null)
            {
                throw new InvalidOperationException(
                    $"ACKED order '{intent.ClientOrderId}' is missing broker provenance.");
            }

            logger.LogInformation(
                "Suppressing duplicate submission for acknowledged intent {IntentId} with client order ID {ClientOrderId}.",
                intent.IntentId,
                intent.ClientOrderId);
            return new OrderSubmissionResult(
                current.BrokerOrderId,
                intent.ClientOrderId,
                intent.IntentId,
                current.BrokerTimestampUtc.Value);
        }

        if (current.State == OrderState.Submitted)
        {
            throw new InvalidOperationException(
                $"Order '{intent.ClientOrderId}' has an uncertain prior submission and must be reconciled before retry.");
        }

        if (current.State != OrderState.Intent)
        {
            throw new InvalidOperationException(
                $"Order '{intent.ClientOrderId}' cannot be submitted from terminal/state {current.State.ToStorageValue()}.");
        }

        var submitted = await eventRepository.TransitionAsync(
            new OrderTransitionRequest(
                intent.ClientOrderId,
                OrderState.Intent,
                OrderState.Submitted,
                Source: "engine",
                LocalTimestampUtc: DateTimeOffset.UtcNow,
                PayloadJson: requestJson),
            cancellationToken);
        if (!submitted.Applied)
        {
            throw new InvalidOperationException(
                $"Order '{intent.ClientOrderId}' is already being submitted and must be reconciled before retry.");
        }

        var persistedOrder = submission.Order with { ClientOrderId = intent.ClientOrderId };
        logger.LogInformation(
            "Submitting persisted order intent {IntentId} with client order ID {ClientOrderId} for {Symbol} {StrategyId}.",
            intent.IntentId,
            intent.ClientOrderId,
            intent.Symbol,
            intent.StrategyId);
        var receipt = await brokerClient.SubmitOrderAsync(persistedOrder, cancellationToken);
        var acknowledged = await eventRepository.TransitionAsync(
            new OrderTransitionRequest(
                intent.ClientOrderId,
                OrderState.Submitted,
                OrderState.Acked,
                Source: "broker_rest",
                LocalTimestampUtc: DateTimeOffset.UtcNow,
                BrokerTimestampUtc: receipt.BrokerAcceptedAtUtc,
                BrokerOrderId: receipt.BrokerOrderId,
                PayloadJson: JsonSerializer.Serialize(new
                {
                    receipt.BrokerOrderId,
                    receipt.BrokerAcceptedAtUtc
                })),
            cancellationToken);
        return new OrderSubmissionResult(
            acknowledged.Snapshot.BrokerOrderId
                ?? throw new InvalidOperationException("ACKED order has no broker order ID."),
            intent.ClientOrderId,
            intent.IntentId,
            acknowledged.Snapshot.BrokerTimestampUtc
                ?? throw new InvalidOperationException("ACKED order has no broker timestamp."));
    }

    public async Task<OrderSubmissionResult> SubmitProtectiveStopAsync(
        ProtectiveStopSubmission submission,
        IBrokerClient brokerClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(brokerClient);
        Validate(submission);

        var symbol = submission.Symbol.Trim().ToUpperInvariant();
        var side = NormalizeSide(submission.Side);
        var requestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            strategyId = "BACKSTOP",
            symbol,
            side,
            orderType = "stop",
            timeInForce = "gtc",
            quantity = submission.Quantity,
            stopPrice = submission.StopPrice,
            submission.SessionDate
        });
        var run = new ProductionRun
        {
            RunId = submission.RunContext.RunId,
            SchemaVersion = 1,
            ConfigHash = submission.RunContext.ConfigHash,
            CodeVersion = submission.RunContext.CodeVersion,
            Profile = submission.RunContext.Profile,
            Status = "running",
            StartedAtUtc = submission.RunContext.StartedAtUtc
        };
        var intent = await intentRepository.ReserveAsync(
            run,
            new OrderIntentReservation(
                submission.IntentId,
                null,
                "BACKSTOP",
                symbol,
                side,
                "stop",
                "gtc",
                submission.Quantity,
                null,
                submission.StopPrice,
                submission.SessionDate,
                submission.CreatedAtUtc.ToUniversalTime(),
                requestJson),
            cancellationToken);

        var current = await eventRepository.GetCurrentAsync(intent.ClientOrderId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Persisted protective intent '{intent.ClientOrderId}' has no lifecycle state.");
        if (current.State == OrderState.Acked)
        {
            if (String.IsNullOrWhiteSpace(current.BrokerOrderId) || current.BrokerTimestampUtc is null)
            {
                throw new InvalidOperationException(
                    $"ACKED protective order '{intent.ClientOrderId}' is missing broker provenance.");
            }

            return new OrderSubmissionResult(
                current.BrokerOrderId,
                intent.ClientOrderId,
                intent.IntentId,
                current.BrokerTimestampUtc.Value);
        }

        if (current.State != OrderState.Intent)
        {
            throw new InvalidOperationException(
                $"Protective order '{intent.ClientOrderId}' is in state {current.State.ToStorageValue()} and must reconcile before retry.");
        }

        var submitted = await eventRepository.TransitionAsync(
            new OrderTransitionRequest(
                intent.ClientOrderId,
                OrderState.Intent,
                OrderState.Submitted,
                Source: "engine",
                LocalTimestampUtc: submission.CreatedAtUtc.ToUniversalTime(),
                PayloadJson: requestJson),
            cancellationToken);
        if (!submitted.Applied)
        {
            throw new InvalidOperationException(
                $"Protective order '{intent.ClientOrderId}' is already being submitted and must reconcile before retry.");
        }

        var receipt = await brokerClient.SubmitProtectiveStopAsync(
            new ProtectiveStopOrder(
                symbol,
                side,
                submission.Quantity,
                submission.StopPrice,
                "gtc",
                intent.ClientOrderId),
            cancellationToken);
        var acknowledged = await eventRepository.TransitionAsync(
            new OrderTransitionRequest(
                intent.ClientOrderId,
                OrderState.Submitted,
                OrderState.Acked,
                Source: "broker_rest",
                LocalTimestampUtc: DateTimeOffset.UtcNow,
                BrokerTimestampUtc: receipt.BrokerAcceptedAtUtc,
                BrokerOrderId: receipt.BrokerOrderId,
                PayloadJson: JsonSerializer.Serialize(receipt)),
            cancellationToken);
        logger.LogCritical(
            "Restored broker protection for {Symbol}. ClientOrderId={ClientOrderId} BrokerOrderId={BrokerOrderId} StopPrice={StopPrice} Quantity={Quantity}.",
            symbol,
            intent.ClientOrderId,
            receipt.BrokerOrderId,
            submission.StopPrice,
            submission.Quantity);
        return new OrderSubmissionResult(
            acknowledged.Snapshot.BrokerOrderId
                ?? throw new InvalidOperationException("ACKED protective order has no broker order ID."),
            intent.ClientOrderId,
            intent.IntentId,
            acknowledged.Snapshot.BrokerTimestampUtc
                ?? throw new InvalidOperationException("ACKED protective order has no broker timestamp."));
    }

    private static void Validate(BracketOrderSubmission submission)
    {
        if (submission.IntentId == Guid.Empty || submission.RunContext.RunId == Guid.Empty)
        {
            throw new InvalidOperationException("Run and intent identity are required before order submission.");
        }

        if (submission.Order.ShareQuantity <= 0 ||
            submission.Order.LimitPrice <= 0m ||
            submission.Order.StopLossPrice <= 0m ||
            submission.Order.TakeProfitPrice <= 0m)
        {
            throw new InvalidOperationException("Bracket order quantity and prices must all be positive.");
        }

        if (!NormalizeSide(submission.Side).Equals("buy", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The current bracket-entry broker contract supports long buy entries only.");
        }

        if (submission.OrderType.Trim().ToLowerInvariant() is not ("limit" or "market"))
        {
            throw new InvalidOperationException("Bracket entry order type must be limit or market.");
        }

        if (submission.TimeInForce.Trim().ToLowerInvariant() is not ("day" or "gtc"))
        {
            throw new InvalidOperationException("Bracket entry time in force must be day or gtc.");
        }

        if (submission.RunContext.ConfigHash.Length != 64 ||
            !submission.RunContext.ConfigHash.All(Uri.IsHexDigit) ||
            submission.RunContext.CodeVersion.Length is not (40 or 64) ||
            !submission.RunContext.CodeVersion.All(Uri.IsHexDigit) ||
            submission.RunContext.Profile is not ("paper" or "live"))
        {
            throw new InvalidOperationException("Execution run provenance is invalid.");
        }
    }

    private static void Validate(ProtectiveStopSubmission submission)
    {
        if (submission.IntentId == Guid.Empty || submission.RunContext.RunId == Guid.Empty ||
            String.IsNullOrWhiteSpace(submission.Symbol) || submission.Quantity <= 0m ||
            submission.Quantity != Decimal.Truncate(submission.Quantity) || submission.StopPrice <= 0m)
        {
            throw new InvalidOperationException(
                "A GTC protective stop requires run/intent identity, symbol, positive whole-share quantity, and stop price.");
        }

        _ = NormalizeSide(submission.Side);
        if (submission.RunContext.ConfigHash.Length != 64 ||
            !submission.RunContext.ConfigHash.All(Uri.IsHexDigit) ||
            submission.RunContext.CodeVersion.Length is not (40 or 64) ||
            !submission.RunContext.CodeVersion.All(Uri.IsHexDigit) ||
            submission.RunContext.Profile is not ("paper" or "live"))
        {
            throw new InvalidOperationException("Protective execution run provenance is invalid.");
        }
    }

    private static string NormalizeSide(string side) => side.Trim().ToLowerInvariant() switch
    {
        "b" or "buy" => "buy",
        "s" or "sell" => "sell",
        _ => throw new InvalidOperationException("Order side must be buy or sell.")
    };
}

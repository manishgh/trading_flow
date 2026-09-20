using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Engine.Execution;

public sealed record OrderDispatchOptions
{
    public OrderDispatchOptions(
        int leaseSeconds,
        int orphanTimeoutSeconds,
        int recoveryIntervalSeconds,
        int recoveryParallelism)
    {
        if (leaseSeconds is < 10 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        }

        if (orphanTimeoutSeconds is < 10 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(orphanTimeoutSeconds));
        }

        if (recoveryIntervalSeconds is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryIntervalSeconds));
        }

        if (recoveryParallelism is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryParallelism));
        }

        LeaseDuration = TimeSpan.FromSeconds(leaseSeconds);
        OrphanTimeout = TimeSpan.FromSeconds(orphanTimeoutSeconds);
        RecoveryInterval = TimeSpan.FromSeconds(recoveryIntervalSeconds);
        RecoveryParallelism = recoveryParallelism;
    }

    public TimeSpan LeaseDuration { get; }
    public TimeSpan OrphanTimeout { get; }
    public TimeSpan RecoveryInterval { get; }
    public int RecoveryParallelism { get; }
}

public sealed class OrderDispatchPendingException(string clientOrderId) : InvalidOperationException(
    $"Order '{clientOrderId}' has an unresolved broker submission inside the orphan window.")
{
    public string ClientOrderId { get; } = clientOrderId;
}

public sealed class OrderDispatchRecoveryIncompleteException(int pendingCount, int failedCount)
    : InvalidOperationException(
        $"Durable order recovery is incomplete: {pendingCount} intent(s) remain pending and {failedCount} intent(s) failed recovery.")
{
    public int PendingCount { get; } = pendingCount;
    public int FailedCount { get; } = failedCount;
}

public sealed class OrderDispatchNoLongerRequiredException(Guid intentId) : InvalidOperationException(
    $"Order intent {intentId:N} was terminalized because its protected position generation is no longer current.")
{
    public Guid IntentId { get; } = intentId;
}

public interface IOrderDispatchService
{
    Task<OrderSubmissionResult> DispatchAsync(
        Guid intentId,
        IBrokerClient broker,
        CancellationToken cancellationToken = default);

    Task<int> RecoverPendingAsync(
        IBrokerClient broker,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Claims one durable intent, adopts any broker order with the same client ID,
/// and only then retries a missing submission. The broker client ID is the final
/// idempotency fence across lease expiry, process crashes, and ambiguous HTTP results.
/// </summary>
public sealed class OrderDispatchService : IOrderDispatchService
{
    private static readonly IReadOnlySet<string> RecoveryEntryAdmissionExemptions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            EntryAdmissionSources.DurableOrderRecovery
        };

    private readonly IOrderIntentRepository intents;
    private readonly IOrderDispatchRepository dispatches;
    private readonly IOrderEventRepository events;
    private readonly IPositionLedgerRepository positions;
    private readonly IBrokerAccountBindingService accountBinding;
    private readonly IOrderLifecycleService lifecycle;
    private readonly OrderDispatchOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OrderDispatchService> logger;
    private readonly IEntryAdmissionControl? admission;
    private readonly IBrokerMutationCoordinator brokerMutations;
    private readonly string ownerId =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public OrderDispatchService(
        IOrderIntentRepository intents,
        IOrderDispatchRepository dispatches,
        IOrderEventRepository events,
        IPositionLedgerRepository positions,
        IBrokerAccountBindingService accountBinding,
        IOrderLifecycleService lifecycle,
        OrderDispatchOptions options,
        TimeProvider timeProvider,
        ILogger<OrderDispatchService> logger,
        IEntryAdmissionControl? admission = null,
        IBrokerMutationCoordinator? brokerMutations = null)
    {
        this.intents = intents;
        this.dispatches = dispatches;
        this.events = events;
        this.positions = positions;
        this.accountBinding = accountBinding;
        this.lifecycle = lifecycle;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
        this.brokerMutations = brokerMutations ?? new BrokerMutationCoordinator();
        this.admission = admission;
    }

    public async Task<OrderSubmissionResult> DispatchAsync(
        Guid intentId,
        IBrokerClient broker,
        CancellationToken cancellationToken = default)
    {
        return await DispatchCoreAsync(
            intentId,
            broker,
            ignoredEntryAdmissionSources: null,
            cancellationToken);
    }

    private async Task<OrderSubmissionResult> DispatchCoreAsync(
        Guid intentId,
        IBrokerClient broker,
        IReadOnlySet<string>? ignoredEntryAdmissionSources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(broker);
        using var dispatchMutation = brokerMutations.Enter($"dispatch intent {intentId:N}");
        var intent = await intents.GetByIntentIdAsync(intentId, cancellationToken)
            ?? throw new InvalidOperationException($"Unknown order intent {intentId:N}.");
        var current = await events.GetCurrentAsync(intent.ClientOrderId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Intent {intent.IntentId:N} has no lifecycle state.");
        if (current.State is not (OrderState.Intent or OrderState.Submitted))
        {
            return ResolveCompletedDispatch(intent, current);
        }

        var account = await broker.GetAccountSnapshotAsync(cancellationToken);
        accountBinding.Validate(account);
        if (!intent.AccountId.Equals(account.AccountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Intent {intentId:N} belongs to account '{intent.AccountId}', not the connected broker account.");
        }

        var lease = await dispatches.TryAcquireDispatchLeaseAsync(
            intentId,
            account.AccountId,
            ownerId,
            options.LeaseDuration,
            cancellationToken)
            ?? throw new OrderDispatchPendingException(intent.ClientOrderId);
        try
        {
            return await DispatchClaimedAsync(
                lease,
                broker,
                ignoredEntryAdmissionSources,
                cancellationToken);
        }
        finally
        {
            await dispatches.ReleaseDispatchLeaseAsync(
                intentId,
                lease.LeaseToken,
                CancellationToken.None);
        }
    }

    public async Task<int> RecoverPendingAsync(
        IBrokerClient broker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        var account = await broker.GetAccountSnapshotAsync(cancellationToken);
        accountBinding.Validate(account);
        var intentIds = await dispatches.ListRecoverableIntentIdsAsync(
            account.AccountId,
            cancellationToken);
        var recovered = 0;
        var pending = 0;
        var failed = 0;
        await Parallel.ForEachAsync(
            intentIds,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = options.RecoveryParallelism
            },
            async (intentId, token) =>
            {
                try
                {
                    await DispatchCoreAsync(
                        intentId,
                        broker,
                        RecoveryEntryAdmissionExemptions,
                        token);
                    Interlocked.Increment(ref recovered);
                }
                catch (OrderDispatchPendingException)
                {
                    // Another owner or the broker propagation window still owns this attempt.
                    Interlocked.Increment(ref pending);
                }
                catch (Exception exception) when (
                    exception is OrderDispatchNoLongerRequiredException or
                    OrderDispatchExpiredException or
                    OrderDispatchRunInactiveException or
                    OrderDispatchUnsupportedHorizonException or
                    PositionExitInProgressException)
                {
                    // DispatchAsync durably terminalized these intents before throwing.
                    // They require no broker action and are safe recovery outcomes.
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Interlocked.Increment(ref failed);
                    logger.LogError(
                        exception,
                        "Durable dispatch recovery failed for intent {IntentId}.",
                        intentId);
                }
            });

        if (pending > 0 || failed > 0)
        {
            throw new OrderDispatchRecoveryIncompleteException(pending, failed);
        }

        return recovered;
    }

    private async Task<OrderSubmissionResult> DispatchClaimedAsync(
        OrderDispatchLease lease,
        IBrokerClient broker,
        IReadOnlySet<string>? ignoredEntryAdmissionSources,
        CancellationToken cancellationToken)
    {
        var intent = lease.Intent;
        var current = await events.GetCurrentAsync(intent.ClientOrderId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Intent {intent.IntentId:N} has no lifecycle state.");
        if (current.State is not (OrderState.Intent or OrderState.Submitted))
        {
            return ResolveCompletedDispatch(intent, current);
        }

        var brokerOrder = await broker.GetOrderByClientOrderIdAsync(
            intent.ClientOrderId,
            cancellationToken);
        if (brokerOrder is not null)
        {
            return await AdoptBrokerOrderAsync(intent, current, brokerOrder, cancellationToken);
        }

        if (current.State == OrderState.Submitted && intent.DispatchAttemptCount > 0)
        {
            var lastAttemptAt = intent.LastDispatchAttemptAtUtc ?? current.LocalTimestampUtc;
            if (timeProvider.GetUtcNow() - lastAttemptAt.ToUniversalTime() < options.OrphanTimeout)
            {
                throw new OrderDispatchPendingException(intent.ClientOrderId);
            }

            if (intent.DispatchAttemptCount > 0)
            {
                throw new OrderDispatchAdoptionRequiredException(intent.IntentId);
            }
        }

        if (intent.Kind == OrderIntentKind.PositionExit)
        {
            await ValidatePositionExitOwnershipAsync(intent, cancellationToken);
            if (current.State == OrderState.Intent)
            {
                throw new PositionExitPreparationRequiredException(intent.IntentId);
            }
        }
        else if (intent.Kind == OrderIntentKind.ProtectiveStop &&
                 intent.PositionGenerationEventId is not null)
        {
            await ValidateProtectiveStopOwnershipAsync(intent, cancellationToken);
        }

        // Adoption above is always safe. Only a new entry broker POST crosses this
        // fence; recovery may ignore its own block but no independent safety block.
        using var entryDispatch = intent.Kind is
            OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry
                ? admission?.BeginEntryDispatch(ignoredEntryAdmissionSources)
                : null;

        if (current.State == OrderState.Intent)
        {
            var submitted = await events.TransitionAsync(
                new OrderTransitionRequest(
                    intent.ClientOrderId,
                    OrderState.Intent,
                    OrderState.Submitted,
                    Source: "engine",
                    LocalTimestampUtc: timeProvider.GetUtcNow(),
                    PayloadJson: intent.RequestJson),
                cancellationToken);
            if (!submitted.Applied && submitted.Snapshot.State != OrderState.Submitted)
            {
                return ResolveCompletedDispatch(intent, submitted.Snapshot);
            }
        }

        using var brokerMutation = brokerMutations.Enter(
            $"dispatch {intent.Kind} {intent.ClientOrderId}");
        try
        {
            intent = await dispatches.RecordDispatchAttemptAsync(
                intent.IntentId,
                lease.LeaseToken,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is OrderDispatchExpiredException or OrderDispatchRunInactiveException or
                OrderDispatchUnsupportedHorizonException)
        {
            var latest = await events.GetCurrentAsync(intent.ClientOrderId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Intent {intent.IntentId:N} disappeared before dispatch expiry.");
            if (latest.State is OrderState.Intent or OrderState.Submitted)
            {
                await events.TransitionAsync(
                    new OrderTransitionRequest(
                        intent.ClientOrderId,
                        latest.State,
                        OrderState.Expired,
                        Source: "engine",
                        LocalTimestampUtc: timeProvider.GetUtcNow(),
                        PayloadJson: JsonSerializer.Serialize(new
                        {
                            reason = exception switch
                            {
                                OrderDispatchExpiredException => "dispatch_expired_before_broker_post",
                                OrderDispatchUnsupportedHorizonException => "unsupported_entry_horizon",
                                _ => "owning_run_inactive_before_broker_post"
                            }
                        })),
                    cancellationToken);
            }

            throw;
        }
        catch (PositionExitInProgressException)
        {
            var latest = await events.GetCurrentAsync(intent.ClientOrderId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Intent {intent.IntentId:N} has no lifecycle state after exit ownership was established.");
            if (!OrderStateMachine.IsTerminal(latest.State))
            {
                await events.TransitionAsync(
                    new OrderTransitionRequest(
                        intent.ClientOrderId,
                        latest.State,
                        OrderState.Expired,
                        Source: "engine",
                        LocalTimestampUtc: timeProvider.GetUtcNow().ToUniversalTime(),
                        BrokerOrderId: latest.BrokerOrderId,
                        PayloadJson: JsonSerializer.Serialize(new
                        {
                            action = "protective_dispatch_expired",
                            reason = "position_exit_in_progress",
                            intent.PositionGenerationEventId
                        })),
                    cancellationToken);
            }

            throw;
        }

        logger.LogInformation(
            "Dispatching durable order intent {IntentId} ClientOrderId={ClientOrderId} Attempt={Attempt} Kind={Kind}.",
            intent.IntentId,
            intent.ClientOrderId,
            intent.DispatchAttemptCount,
            intent.Kind);
        BrokerOrderReceipt receipt;
        try
        {
            receipt = intent.Kind switch
            {
                OrderIntentKind.ProtectiveStop => await broker.SubmitProtectiveStopAsync(
                    ToProtectiveStop(intent),
                    cancellationToken),
                OrderIntentKind.PositionExit => await broker.SubmitExitOrderAsync(
                    ToExitOrder(intent),
                    cancellationToken),
                OrderIntentKind.StrategyEntry or OrderIntentKind.OperatorEntry =>
                    await broker.SubmitOrderAsync(ToEntryOrder(intent), cancellationToken),
                _ => throw new InvalidOperationException(
                    $"Unsupported durable order intent kind {intent.Kind}.")
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var racedOrder = await broker.GetOrderByClientOrderIdAsync(
                intent.ClientOrderId,
                cancellationToken);
            if (racedOrder is not null)
            {
                logger.LogWarning(
                    exception,
                    "Broker submission returned an error but lookup found client order ID {ClientOrderId}; adopting it.",
                    intent.ClientOrderId);
                var submittedState = await events.GetCurrentAsync(
                    intent.ClientOrderId,
                    cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Intent {intent.IntentId:N} disappeared after broker submission.");
                return await AdoptBrokerOrderAsync(
                    intent,
                    submittedState,
                    racedOrder,
                    cancellationToken);
            }

            if (exception is BrokerOrderRejectedException rejection)
            {
                await events.TransitionAsync(
                    new OrderTransitionRequest(
                        intent.ClientOrderId,
                        OrderState.Submitted,
                        OrderState.Rejected,
                        Source: "engine",
                        LocalTimestampUtc: timeProvider.GetUtcNow(),
                        PayloadJson: JsonSerializer.Serialize(new
                        {
                            rejection.Operation,
                            rejection.StatusCode,
                            rejection.Message
                        })),
                    cancellationToken);
            }

            throw;
        }
        OrderTransitionResult acknowledged;
        try
        {
            using var acknowledgementTimeout = new CancellationTokenSource(options.OrphanTimeout);
            acknowledged = await events.TransitionAsync(
                new OrderTransitionRequest(
                    intent.ClientOrderId,
                    OrderState.Submitted,
                    OrderState.Acked,
                    Source: "broker_rest",
                    LocalTimestampUtc: timeProvider.GetUtcNow(),
                    BrokerTimestampUtc: receipt.BrokerAcceptedAtUtc,
                    BrokerOrderId: receipt.BrokerOrderId,
                    PayloadJson: JsonSerializer.Serialize(receipt)),
                acknowledgementTimeout.Token);
            admission?.Clear(AcknowledgementJournalBlockSource(intent.IntentId));
        }
        catch (Exception exception)
        {
            admission?.Block(
                AcknowledgementJournalBlockSource(intent.IntentId),
                "BROKER_ACK_JOURNAL_FAILED",
                $"Broker accepted {intent.ClientOrderId}, but its acknowledgement could not be journaled: {exception.Message}",
                timeProvider.GetUtcNow());
            logger.LogCritical(
                exception,
                "Broker accepted an order but the mandatory acknowledgement journal failed. IntentId={IntentId} ClientOrderId={ClientOrderId} BrokerOrderId={BrokerOrderId}.",
                intent.IntentId,
                intent.ClientOrderId,
                receipt.BrokerOrderId);
            throw;
        }
        return new OrderSubmissionResult(
            acknowledged.Snapshot.BrokerOrderId ?? receipt.BrokerOrderId,
            intent.ClientOrderId,
            intent.IntentId,
            acknowledged.Snapshot.BrokerTimestampUtc ?? receipt.BrokerAcceptedAtUtc,
            ResolveSubmittedOutsideRegularHours(intent.RequestJson));
    }

    private static string AcknowledgementJournalBlockSource(Guid intentId) =>
        $"broker_ack_journal:{intentId:N}";

    private async Task<OrderSubmissionResult> AdoptBrokerOrderAsync(
        OrderIntentRecord intent,
        OrderStateSnapshot current,
        ActiveBrokerOrder brokerOrder,
        CancellationToken cancellationToken)
    {
        using var request = JsonDocument.Parse(intent.RequestJson);
        var root = request.RootElement;
        var expectedExtendedHours = ResolveSubmittedOutsideRegularHours(root);
        const string expectedOrderClass = "simple";
        if (!brokerOrder.ClientOrderId.Equals(intent.ClientOrderId, StringComparison.Ordinal) ||
            !brokerOrder.Ticker.Equals(intent.Symbol, StringComparison.OrdinalIgnoreCase) ||
            !brokerOrder.Side.Equals(intent.Side, StringComparison.OrdinalIgnoreCase) ||
            brokerOrder.Qty != intent.RequestedQuantity ||
            !brokerOrder.OrderType.Equals(intent.OrderType, StringComparison.OrdinalIgnoreCase) ||
            String.IsNullOrWhiteSpace(brokerOrder.TimeInForce) ||
            !brokerOrder.TimeInForce.Equals(intent.TimeInForce, StringComparison.OrdinalIgnoreCase) ||
            intent.OrderType is "limit" or "stop_limit" &&
            brokerOrder.LimitPrice != intent.LimitPrice ||
            intent.OrderType is "stop" or "stop_limit" &&
            brokerOrder.StopPrice != intent.StopPrice ||
            String.IsNullOrWhiteSpace(brokerOrder.OrderClass) ||
            !brokerOrder.OrderClass.Equals(expectedOrderClass, StringComparison.OrdinalIgnoreCase) ||
            brokerOrder.ExtendedHours is null ||
            brokerOrder.ExtendedHours.Value != expectedExtendedHours)
        {
            throw new InvalidOperationException(
                $"Broker order '{brokerOrder.OrderId}' does not match owned intent {intent.IntentId:N}.");
        }

        if (current.State == OrderState.Intent)
        {
            current = (await events.TransitionAsync(
                new OrderTransitionRequest(
                    intent.ClientOrderId,
                    OrderState.Intent,
                    OrderState.Submitted,
                    Source: "engine",
                    LocalTimestampUtc: timeProvider.GetUtcNow(),
                    PayloadJson: intent.RequestJson),
                cancellationToken)).Snapshot;
        }

        var update = BrokerOrderUpdateFactory.Create(brokerOrder);
        var projection = brokerOrder.FilledQuantity > 0m
            ? new OrderFillProjection(
                brokerOrder.Ticker,
                brokerOrder.Side,
                brokerOrder.FilledQuantity,
                brokerOrder.FilledAveragePrice ?? throw new InvalidOperationException(
                    $"Broker order '{brokerOrder.OrderId}' has fills without an average price."),
                $"rest:{brokerOrder.OrderId}:{brokerOrder.FilledQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            : null;
        var adopted = projection is null
            ? await lifecycle.ApplyBrokerUpdateAsync(update, cancellationToken)
            : await lifecycle.ApplyBrokerUpdateWithFillAsync(update, projection, cancellationToken);
        if (adopted.State is OrderState.Rejected or OrderState.Canceled or OrderState.Expired)
        {
            throw new InvalidOperationException(
                $"Broker order '{brokerOrder.OrderId}' is terminal in state {adopted.State.ToStorageValue()}.");
        }

        logger.LogWarning(
            "Adopted broker order {BrokerOrderId} for recovered client order ID {ClientOrderId} in state {State}.",
            brokerOrder.OrderId,
            intent.ClientOrderId,
            adopted.State.ToStorageValue());
        admission?.Clear(AcknowledgementJournalBlockSource(intent.IntentId));
        return new OrderSubmissionResult(
            brokerOrder.OrderId,
            intent.ClientOrderId,
            intent.IntentId,
            adopted.BrokerTimestampUtc ?? brokerOrder.UpdatedAt,
            ResolveSubmittedOutsideRegularHours(intent.RequestJson));
    }

    private OrderSubmissionResult ResolveCompletedDispatch(
        OrderIntentRecord intent,
        OrderStateSnapshot state)
    {
        if (state.State is (OrderState.Acked or OrderState.PartiallyFilled or OrderState.Filled) &&
            !String.IsNullOrWhiteSpace(state.BrokerOrderId))
        {
            admission?.Clear(AcknowledgementJournalBlockSource(intent.IntentId));
            return new OrderSubmissionResult(
                state.BrokerOrderId,
                intent.ClientOrderId,
                intent.IntentId,
                state.BrokerTimestampUtc ?? state.LocalTimestampUtc,
                ResolveSubmittedOutsideRegularHours(intent.RequestJson));
        }

        throw new InvalidOperationException(
            $"Order '{intent.ClientOrderId}' cannot dispatch from state {state.State.ToStorageValue()}.");
    }

    private static BrokerEntryOrder ToEntryOrder(OrderIntentRecord intent)
    {
        using var request = JsonDocument.Parse(intent.RequestJson);
        var root = request.RootElement;
        var takeProfitPrice = ReadRequiredDecimal(root, "takeProfitPrice");
        var quantity = Decimal.ToInt32(intent.RequestedQuantity);
        var order = new FinalizedOrder(
            intent.Symbol,
            intent.StrategyId,
            quantity,
            intent.LimitPrice ?? throw new InvalidOperationException("Entry intent has no limit/reference price."),
            intent.StopPrice ?? throw new InvalidOperationException("Entry intent has no stop price."),
            takeProfitPrice,
            intent.CreatedAtUtc,
            intent.ClientOrderId);
        return new BrokerEntryOrder(
            order,
            intent.Side,
            intent.OrderType,
            intent.TimeInForce,
            ResolveSubmittedOutsideRegularHours(intent.RequestJson));
    }

    private static ProtectiveStopOrder ToProtectiveStop(OrderIntentRecord intent) => new(
        intent.Symbol,
        intent.Side,
        intent.RequestedQuantity,
        intent.StopPrice ?? throw new InvalidOperationException("Protective intent has no stop price."),
        intent.TimeInForce,
        intent.ClientOrderId);

    private static BrokerExitOrder ToExitOrder(OrderIntentRecord intent) => new(
        intent.Symbol,
        intent.Side,
        intent.RequestedQuantity,
        intent.OrderType,
        intent.TimeInForce,
        intent.LimitPrice,
        intent.StopPrice,
        ResolveSubmittedOutsideRegularHours(intent.RequestJson),
        intent.ClientOrderId);

    private async Task ValidatePositionExitOwnershipAsync(
        OrderIntentRecord intent,
        CancellationToken cancellationToken)
    {
        using var request = JsonDocument.Parse(intent.RequestJson);
        var root = request.RootElement;
        var expectedPositionGenerationEventId = ReadRequiredInt64(
            root,
            "expectedPositionGenerationEventId");
        var expectedPositionGenerationClientOrderId = ReadRequiredString(
            root,
            "expectedPositionGenerationClientOrderId");
        var position = await positions.GetCurrentAsync(
            intent.AccountId,
            intent.Symbol,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Position exit {intent.IntentId:N} no longer owns an open {intent.Symbol} position.");
        if (position.PositionGenerationEventId != expectedPositionGenerationEventId ||
            !position.PositionGenerationClientOrderId.Equals(
                expectedPositionGenerationClientOrderId,
                StringComparison.Ordinal) ||
            Math.Abs(position.Quantity) < intent.RequestedQuantity ||
            position.Quantity > 0m != intent.Side.Equals("sell", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Position exit {intent.IntentId:N} is stale for the current {intent.Symbol} position generation.");
        }
    }

    /// <summary>
    /// Prevents crash recovery from dispatching protection that belongs to an
    /// earlier position generation after the position was flattened or reopened.
    /// Broker-only repair intents do not carry a local generation and are instead
    /// validated by the reconciliation service that creates them.
    /// </summary>
    private async Task ValidateProtectiveStopOwnershipAsync(
        OrderIntentRecord intent,
        CancellationToken cancellationToken)
    {
        var expectedGeneration = intent.PositionGenerationEventId!.Value;
        var position = await positions.GetCurrentAsync(
            intent.AccountId,
            intent.Symbol,
            cancellationToken);
        var ownsCurrentPosition = position is not null &&
            position.Quantity != 0m &&
            position.PositionGenerationEventId == expectedGeneration &&
            Math.Abs(position.Quantity) >= intent.RequestedQuantity &&
            (position.Quantity > 0m) == intent.Side.Equals("sell", StringComparison.OrdinalIgnoreCase);
        if (ownsCurrentPosition)
        {
            return;
        }

        var current = await events.GetCurrentAsync(intent.ClientOrderId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Protective intent {intent.IntentId:N} has no lifecycle state.");
        if (!OrderStateMachine.IsTerminal(current.State))
        {
            await events.TransitionAsync(
                new OrderTransitionRequest(
                    intent.ClientOrderId,
                    current.State,
                    OrderState.Expired,
                    Source: "engine",
                    LocalTimestampUtc: timeProvider.GetUtcNow().ToUniversalTime(),
                    BrokerOrderId: current.BrokerOrderId,
                    PayloadJson: JsonSerializer.Serialize(new
                    {
                        action = "protective_dispatch_expired",
                        reason = "stale_position_generation",
                        expectedPositionGenerationEventId = expectedGeneration,
                        actualPositionGenerationEventId = position?.PositionGenerationEventId
                    })),
                cancellationToken);
        }

        throw new OrderDispatchNoLongerRequiredException(intent.IntentId);
    }

    private static decimal ReadRequiredDecimal(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.TryGetDecimal(out var value) && value > 0m)
            {
                return value;
            }
        }

        throw new InvalidOperationException(
            $"Persisted order request is missing positive '{propertyName}'.");
    }

    private static long ReadRequiredInt64(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.TryGetInt64(out var value) && value > 0)
            {
                return value;
            }
        }

        throw new InvalidOperationException(
            $"Persisted order request is missing positive '{propertyName}'.");
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String &&
                !String.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                return property.Value.GetString()!;
            }
        }

        throw new InvalidOperationException(
            $"Persisted order request is missing '{propertyName}'.");
    }

    private static bool ResolveSubmittedOutsideRegularHours(string requestJson)
    {
        using var request = JsonDocument.Parse(requestJson);
        return ResolveSubmittedOutsideRegularHours(request.RootElement);
    }

    private static bool ResolveSubmittedOutsideRegularHours(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals("submitOutsideRegularHours", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return property.Value.GetBoolean();
            }
        }

        return false;
    }
}

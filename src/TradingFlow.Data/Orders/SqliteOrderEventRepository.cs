using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

/// <summary>
/// Atomically validates and appends the lifecycle for one persisted order intent.
/// </summary>
public sealed class SqliteOrderEventRepository : IOrderEventRepository
{
    private readonly IDbContextFactory<TradingFlowDbContext> contextFactory;
    private readonly SemaphoreSlim transitionLock = new(1, 1);

    public SqliteOrderEventRepository(IDbContextFactory<TradingFlowDbContext> contextFactory)
    {
        this.contextFactory = contextFactory;
    }

    public async Task<OrderStateSnapshot?> GetCurrentAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        var normalizedClientOrderId = NormalizeClientOrderId(clientOrderId);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var current = await context.OrderEvents
            .AsNoTracking()
            .Where(record => record.ClientOrderId == normalizedClientOrderId)
            .OrderByDescending(record => record.EventId)
            .FirstOrDefaultAsync(cancellationToken);
        return current is null ? null : ToSnapshot(current);
    }

    public async Task<IReadOnlyList<OrderStateSnapshot>> ListReconcilableAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = NormalizeAccountId(accountId);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ownedClientOrderIds = context.OrderIntents
            .Where(record => record.AccountId == normalizedAccountId)
            .Select(record => record.ClientOrderId);
        var latestEventIds = context.OrderEvents
            .Where(record => ownedClientOrderIds.Contains(record.ClientOrderId))
            .GroupBy(record => record.ClientOrderId)
            .Select(group => group.Max(record => record.EventId));
        var records = await context.OrderEvents
            .AsNoTracking()
            .Where(record => latestEventIds.Contains(record.EventId))
            .OrderBy(record => record.ClientOrderId)
            .ToListAsync(cancellationToken);

        var brokerOrderIds = records
            .Where(record => !String.IsNullOrWhiteSpace(record.BrokerOrderId))
            .Select(record => record.BrokerOrderId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var accountedFills = await context.PositionEvents
            .AsNoTracking()
            .Where(record =>
                record.AccountId == normalizedAccountId &&
                brokerOrderIds.Contains(record.BrokerOrderId))
            .GroupBy(record => record.BrokerOrderId)
            .Select(group => new { BrokerOrderId = group.Key, Quantity = group.Sum(record => record.FillQuantity) })
            .ToDictionaryAsync(record => record.BrokerOrderId, record => record.Quantity, StringComparer.Ordinal, cancellationToken);

        return records
            .Where(record =>
            {
                var state = OrderStateMachine.ParseStorageValue(record.NewState);
                var accounted = !String.IsNullOrWhiteSpace(record.BrokerOrderId) &&
                    accountedFills.TryGetValue(record.BrokerOrderId, out var quantity)
                        ? quantity
                        : 0m;
                return IsReconcilable(state) ||
                    OrderStateMachine.IsTerminal(state) && (record.FilledQuantity ?? 0m) > accounted;
            })
            .Select(ToSnapshot)
            .ToArray();
    }

    public async Task<OrderStateSnapshot> RecordBrokerReplacementAsync(
        BrokerOrderReplacementTransition request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ownerClientOrderId = NormalizeClientOrderId(request.OwnerClientOrderId);
        if (String.IsNullOrWhiteSpace(request.PredecessorBrokerOrderId) ||
            String.IsNullOrWhiteSpace(request.SuccessorBrokerOrderId) ||
            request.PredecessorBrokerOrderId.Equals(request.SuccessorBrokerOrderId, StringComparison.Ordinal) ||
            request.BrokerTimestampUtc == default ||
            request.LocalTimestampUtc == default ||
            request.BrokerTimestampUtc.Offset != TimeSpan.Zero ||
            request.LocalTimestampUtc.Offset != TimeSpan.Zero ||
            String.IsNullOrWhiteSpace(request.PayloadJson))
        {
            throw new InvalidOperationException(
                "Broker replacement requires owner, distinct broker order IDs, UTC timestamps, and payload.");
        }

        await transitionLock.WaitAsync(cancellationToken);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await context.Database.OpenConnectionAsync(cancellationToken);
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            await using var transaction = connection.BeginTransaction(
                IsolationLevel.Serializable,
                deferred: false);
            context.Database.UseTransaction(transaction);
            var current = await context.OrderEvents
                .AsNoTracking()
                .Where(record => record.ClientOrderId == ownerClientOrderId)
                .OrderByDescending(record => record.EventId)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Protective owner '{ownerClientOrderId}' has no lifecycle state.");
            if (current.BrokerOrderId == request.SuccessorBrokerOrderId)
            {
                await transaction.CommitAsync(cancellationToken);
                return ToSnapshot(current);
            }

            if (current.BrokerOrderId != request.PredecessorBrokerOrderId ||
                OrderStateMachine.IsTerminal(OrderStateMachine.ParseStorageValue(current.NewState)))
            {
                throw new InvalidOperationException(
                    $"Protective owner '{ownerClientOrderId}' does not currently own predecessor order '{request.PredecessorBrokerOrderId}'.");
            }

            var replacementEvent = new OrderEventRecord
            {
                ClientOrderId = ownerClientOrderId,
                BrokerOrderId = request.SuccessorBrokerOrderId.Trim(),
                PreviousState = current.NewState,
                NewState = current.NewState,
                Source = "broker_rest",
                BrokerTimestampUtc = request.BrokerTimestampUtc,
                LocalTimestampUtc = request.LocalTimestampUtc,
                FilledQuantity = current.FilledQuantity,
                FillPrice = current.FillPrice,
                PayloadJson = request.PayloadJson,
                RunId = current.RunId,
                SchemaVersion = current.SchemaVersion,
                ConfigHash = current.ConfigHash,
                CodeVersion = current.CodeVersion
            };
            context.OrderEvents.Add(replacementEvent);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToSnapshot(replacementEvent);
        }
        finally
        {
            transitionLock.Release();
        }
    }

    public async Task<OrderTransitionResult> TransitionAsync(
        OrderTransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        await transitionLock.WaitAsync(cancellationToken);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await context.Database.OpenConnectionAsync(cancellationToken);
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            await using var transaction = connection.BeginTransaction(
                IsolationLevel.Serializable,
                deferred: false);
            context.Database.UseTransaction(transaction);
            var clientOrderId = NormalizeClientOrderId(request.ClientOrderId);
            var intent = await context.OrderIntents
                .AsNoTracking()
                .SingleOrDefaultAsync(record => record.ClientOrderId == clientOrderId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Cannot transition unknown order intent '{clientOrderId}'.");
            var current = await context.OrderEvents
                .AsNoTracking()
                .Where(record => record.ClientOrderId == clientOrderId)
                .OrderByDescending(record => record.EventId)
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Order intent '{clientOrderId}' has no initial lifecycle event.");
            var currentState = OrderStateMachine.ParseStorageValue(current.NewState);

            if (currentState == request.NewState)
            {
                if (IsExactReplay(current, request))
                {
                    var repairedPosition = await ApplyPositionFillAsync(
                        context,
                        intent,
                        current,
                        request,
                        cancellationToken);
                    if (repairedPosition is not null)
                    {
                        await context.SaveChangesAsync(cancellationToken);
                        await FinalizePositionGenerationAsync(
                            context,
                            repairedPosition,
                            cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                    }

                    return new OrderTransitionResult(ToSnapshot(current), Applied: repairedPosition is not null);
                }

                var forwardFillRepair = request.PositionFill is not null &&
                    request.ExpectedPreviousState == currentState &&
                    request.FilledQuantity > (current.FilledQuantity ?? 0m);
                var forwardPartialFill = currentState == OrderState.PartiallyFilled &&
                    request.ExpectedPreviousState == OrderState.PartiallyFilled &&
                    request.FilledQuantity > (current.FilledQuantity ?? 0m);
                if (!forwardFillRepair && !forwardPartialFill)
                {
                    throw new InvalidOperationException(
                        $"Conflicting duplicate {currentState.ToStorageValue()} event for '{clientOrderId}'.");
                }
            }
            else
            {
                if (currentState != request.ExpectedPreviousState)
                {
                    throw new InvalidOperationException(
                        $"Order '{clientOrderId}' is {currentState.ToStorageValue()}, not the expected {request.ExpectedPreviousState.ToStorageValue()} state.");
                }

                if (!OrderStateMachine.CanTransition(currentState, request.NewState))
                {
                    throw new InvalidOperationException(
                        $"Invalid order transition {currentState.ToStorageValue()} -> {request.NewState.ToStorageValue()} for '{clientOrderId}'.");
                }
            }

            if (request.LocalTimestampUtc.ToUniversalTime() < current.LocalTimestampUtc.ToUniversalTime())
            {
                throw new InvalidOperationException(
                    $"Order event time cannot move backwards for '{clientOrderId}'.");
            }

            if (request.BrokerTimestampUtc is { } brokerTimestamp &&
                current.BrokerTimestampUtc is { } currentBrokerTimestamp &&
                brokerTimestamp.ToUniversalTime() < currentBrokerTimestamp.ToUniversalTime())
            {
                throw new InvalidOperationException(
                    $"Broker event time cannot move backwards for '{clientOrderId}'.");
            }

            var brokerOrderId = ResolveBrokerOrderId(current.BrokerOrderId, request.BrokerOrderId);
            var orderEvent = new OrderEventRecord
            {
                ClientOrderId = clientOrderId,
                BrokerOrderId = brokerOrderId,
                PreviousState = currentState.ToStorageValue(),
                NewState = request.NewState.ToStorageValue(),
                Source = request.Source.Trim().ToLowerInvariant(),
                BrokerTimestampUtc = request.BrokerTimestampUtc?.ToUniversalTime(),
                LocalTimestampUtc = request.LocalTimestampUtc.ToUniversalTime(),
                FilledQuantity = request.FilledQuantity ?? current.FilledQuantity,
                FillPrice = request.FillPrice ?? current.FillPrice,
                PayloadJson = request.PayloadJson,
                RunId = intent.RunId,
                SchemaVersion = intent.SchemaVersion,
                ConfigHash = intent.ConfigHash,
                CodeVersion = intent.CodeVersion
            };
            context.OrderEvents.Add(orderEvent);
            await ApplyRiskReservationTransitionAsync(
                context,
                intent,
                current,
                request,
                cancellationToken);
            var appendedPosition = await ApplyPositionFillAsync(
                context,
                intent,
                orderEvent,
                request,
                cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await FinalizePositionGenerationAsync(
                context,
                appendedPosition,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new OrderTransitionResult(ToSnapshot(orderEvent), Applied: true);
        }
        finally
        {
            transitionLock.Release();
        }
    }

    private static async Task<PositionEventRecord?> ApplyPositionFillAsync(
        TradingFlowDbContext context,
        OrderIntentRecord intent,
        OrderEventRecord orderEvent,
        OrderTransitionRequest request,
        CancellationToken cancellationToken)
    {
        var projection = request.PositionFill;
        if (projection is null)
        {
            return null;
        }

        ValidatePositionFill(intent, request, projection);
        var accountId = intent.AccountId.Trim();
        var executionId = projection.ExecutionId.Trim();
        var existing = await context.PositionEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.AccountId == accountId && record.ExecutionId == executionId,
                cancellationToken);
        if (existing is not null)
        {
            EnsureExactPositionReplay(existing, intent, request, projection);
            return null;
        }

        var brokerOrderId = orderEvent.BrokerOrderId
            ?? throw new InvalidOperationException("A position fill requires broker order identity.");
        var accountedEvents = await context.PositionEvents
            .AsNoTracking()
            .Where(record =>
                record.AccountId == accountId &&
                record.BrokerOrderId == brokerOrderId)
            .Select(record => new { record.FillQuantity, record.FillPrice })
            .ToArrayAsync(cancellationToken);
        var accountedFill = accountedEvents.Sum(record => record.FillQuantity);
        var fillDelta = projection.BrokerCumulativeFillQuantity - accountedFill;
        if (fillDelta < 0m)
        {
            throw new InvalidOperationException(
                $"Position journal fill {accountedFill} exceeds broker cumulative fill " +
                $"{projection.BrokerCumulativeFillQuantity} for order {brokerOrderId}.");
        }

        if (fillDelta == 0m)
        {
            return null;
        }

        if (projection.AuthoritativeLastFillQuantity is { } lastFill && lastFill != fillDelta)
        {
            throw new InvalidOperationException(
                $"Stream execution delta {lastFill} does not bridge accounted fill {accountedFill} " +
                $"to cumulative fill {projection.BrokerCumulativeFillQuantity} for order {brokerOrderId}.");
        }

        var accountedCost = accountedEvents.Sum(record => record.FillQuantity * record.FillPrice);
        var executionFillPrice = projection.AuthoritativeLastFillPrice ??
            ((projection.BrokerCumulativeFillQuantity * projection.BrokerCumulativeAverageFillPrice) -
             accountedCost) / fillDelta;
        if (executionFillPrice <= 0m)
        {
            throw new InvalidOperationException(
                $"Derived execution price {executionFillPrice} is invalid for order {brokerOrderId}.");
        }

        var symbol = projection.Symbol.Trim().ToUpperInvariant();
        var previous = await context.PositionEvents
            .AsNoTracking()
            .Where(record => record.AccountId == accountId && record.Symbol == symbol)
            .OrderByDescending(record => record.PositionEventId)
            .FirstOrDefaultAsync(cancellationToken);
        var signedFillDelta = projection.Side.Trim().ToLowerInvariant() switch
        {
            "buy" => fillDelta,
            "sell" => -fillDelta,
            _ => throw new InvalidOperationException(
                $"Unsupported broker fill side '{projection.Side}'.")
        };
        var expectedQuantityAfter = (previous?.QuantityAfter ?? 0m) + signedFillDelta;
        if (projection.AuthoritativePositionQuantity is { } authoritativePosition &&
            authoritativePosition != expectedQuantityAfter)
        {
            throw new InvalidOperationException(
                $"Broker position {authoritativePosition} does not equal journal position " +
                $"{expectedQuantityAfter} after execution {executionId}.");
        }

        var startsNewGeneration = expectedQuantityAfter != 0m &&
            (previous is null ||
             previous.QuantityAfter == 0m ||
             Math.Sign(previous.QuantityAfter) != Math.Sign(expectedQuantityAfter));
        var positionEvent = new PositionEventRecord
        {
            AccountId = accountId,
            Symbol = symbol,
            StrategyId = ResolvePositionStrategy(previous, intent.StrategyId, expectedQuantityAfter),
            ExecutionStrategyId = intent.StrategyId,
            PositionGenerationEventId = startsNewGeneration
                ? 0
                : previous?.PositionGenerationEventId ?? 0,
            PositionGenerationClientOrderId = startsNewGeneration
                ? intent.ClientOrderId
                : ResolvePositionGenerationClientOrderId(previous),
            QuantityAfter = expectedQuantityAfter,
            FillQuantity = fillDelta,
            FillPrice = executionFillPrice,
            Side = projection.Side.Trim().ToLowerInvariant(),
            BrokerOrderId = brokerOrderId,
            ClientOrderId = intent.ClientOrderId,
            ExecutionId = executionId,
            Source = request.Source.Trim().ToLowerInvariant(),
            BrokerTimestampUtc = (request.BrokerTimestampUtc
                ?? throw new InvalidOperationException("A position fill requires broker time."))
                .ToUniversalTime(),
            LocalTimestampUtc = request.LocalTimestampUtc.ToUniversalTime(),
            PayloadJson = request.PayloadJson,
            RunId = intent.RunId,
            SchemaVersion = intent.SchemaVersion,
            ConfigHash = intent.ConfigHash,
            CodeVersion = intent.CodeVersion
        };
        context.PositionEvents.Add(positionEvent);
        await ApplyPositionBackedRiskAsync(
            context,
            positionEvent,
            cancellationToken);
        return positionEvent;
    }

    private static async Task FinalizePositionGenerationAsync(
        TradingFlowDbContext context,
        PositionEventRecord? positionEvent,
        CancellationToken cancellationToken)
    {
        if (positionEvent is null || positionEvent.PositionGenerationEventId != 0)
        {
            return;
        }

        if (positionEvent.QuantityAfter == 0m || positionEvent.PositionEventId <= 0)
        {
            throw new InvalidOperationException(
                "A position fill could not establish a stable position generation.");
        }

        positionEvent.PositionGenerationEventId = positionEvent.PositionEventId;
        await context.SaveChangesAsync(cancellationToken);
    }

    private static string ResolvePositionGenerationClientOrderId(PositionEventRecord? previous)
    {
        if (previous is null)
        {
            return String.Empty;
        }

        return !String.IsNullOrWhiteSpace(previous.PositionGenerationClientOrderId)
            ? previous.PositionGenerationClientOrderId
            : previous.ClientOrderId;
    }

    private static async Task ApplyPositionBackedRiskAsync(
        TradingFlowDbContext context,
        PositionEventRecord positionEvent,
        CancellationToken cancellationToken)
    {
        var reservations = await context.PortfolioRiskReservations
            .Where(record =>
                record.AccountId == positionEvent.AccountId &&
                record.Symbol == positionEvent.Symbol &&
                record.State != PortfolioRiskReservationState.Released)
            .ToArrayAsync(cancellationToken);
        if (reservations.Length == 0)
        {
            return;
        }

        if (reservations.Length != 1)
        {
            throw new InvalidOperationException(
                $"Account {positionEvent.AccountId} symbol {positionEvent.Symbol} has " +
                $"{reservations.Length} active portfolio owners.");
        }

        var reservation = reservations[0];
        var openQuantity = Math.Abs(positionEvent.QuantityAfter);
        var changedAtUtc = positionEvent.LocalTimestampUtc;
        var previousState = reservation.State;
        var previousOpenQuantity = reservation.OpenPositionQuantity;
        if (positionEvent.QuantityAfter == 0m)
        {
            if (reservation.PendingQuantity > 0m)
            {
                reservation.OpenPositionQuantity = 0m;
                ResizeReservationForOwnedQuantity(
                    reservation,
                    reservation.PendingQuantity);
                reservation.State = PortfolioRiskReservationState.PartiallyFilled;
                reservation.ReleasedAtUtc = null;
                reservation.ReleaseReason = "filled_quantity_flat_pending_entry_remainder";
            }
            else
            {
                if (reservation.State == PortfolioRiskReservationState.Released)
                {
                    return;
                }

                reservation.OpenPositionQuantity = 0m;
                ResizeReservationForOwnedQuantity(reservation, 0m);
                reservation.ReservedPositionSlots = 0;
                reservation.State = PortfolioRiskReservationState.Released;
                reservation.ReleasedAtUtc = changedAtUtc;
                reservation.ReleaseReason = "authoritative_position_flat";
            }
        }
        else
        {
            if (reservation.State == PortfolioRiskReservationState.BackingOpenPosition &&
                openQuantity > reservation.CumulativeFilledQuantity)
            {
                throw new InvalidOperationException(
                    $"Position quantity {openQuantity} exceeds owned entry quantity " +
                    $"{reservation.CumulativeFilledQuantity} for {positionEvent.Symbol}.");
            }

            if (reservation.OpenPositionQuantity == openQuantity)
            {
                return;
            }

            reservation.OpenPositionQuantity = openQuantity;
            if (reservation.State == PortfolioRiskReservationState.BackingOpenPosition)
            {
                ResizeReservationForOwnedQuantity(reservation, openQuantity);
            }
        }

        reservation.StateChangedAtUtc = changedAtUtc;
        reservation.Version = checked(reservation.Version + 1);
        context.RiskEvents.Add(new RiskEventRecord
        {
            EventType = reservation.State == PortfolioRiskReservationState.Released
                ? "portfolio_capacity_released"
                : "position_backed_capacity_updated",
            Severity = "information",
            Symbol = reservation.Symbol,
            ObservedValue = reservation.ReservedPortfolioRisk,
            LimitValue = reservation.MaxPortfolioRisk,
            OccurredAtUtc = changedAtUtc,
            DetailsJson = JsonSerializer.Serialize(new
            {
                reservation.IntentId,
                reservation.ClientOrderId,
                previousState = previousState.ToString(),
                state = reservation.State.ToString(),
                previousOpenQuantity,
                reservation.OpenPositionQuantity,
                positionEvent.ExecutionId,
                executionClientOrderId = positionEvent.ClientOrderId
            }),
            RunId = reservation.RunId,
            SchemaVersion = reservation.SchemaVersion,
            ConfigHash = reservation.ConfigHash,
            CodeVersion = reservation.CodeVersion
        });
    }

    private static void ResizeReservationForOwnedQuantity(
        PortfolioRiskReservationRecord reservation,
        decimal ownedQuantity)
    {
        reservation.ReservedBuyingPower = reservation.EntryPrice * ownedQuantity;
        reservation.ReservedGrossExposure = reservation.EntryPrice * ownedQuantity;
        reservation.ReservedNetExposure = Math.Sign(reservation.ReservedNetExposure) *
            reservation.ReservedGrossExposure;
        reservation.ReservedPortfolioRisk = reservation.RiskPerShare * ownedQuantity;
    }

    private static void ValidatePositionFill(
        OrderIntentRecord intent,
        OrderTransitionRequest request,
        OrderFillProjection projection)
    {
        if (request.Source is not ("broker_stream" or "broker_rest") ||
            String.IsNullOrWhiteSpace(intent.AccountId) ||
            String.IsNullOrWhiteSpace(projection.Symbol) ||
            String.IsNullOrWhiteSpace(projection.Side) ||
            String.IsNullOrWhiteSpace(projection.ExecutionId) ||
            projection.BrokerCumulativeFillQuantity <= 0m ||
            projection.BrokerCumulativeAverageFillPrice <= 0m ||
            request.FilledQuantity != projection.BrokerCumulativeFillQuantity ||
            request.FillPrice != projection.BrokerCumulativeAverageFillPrice)
        {
            throw new InvalidOperationException(
                "Position projection must match a positive authoritative broker fill.");
        }

        if (!intent.Symbol.Equals(projection.Symbol.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !intent.Side.Equals(projection.Side.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Position projection does not match immutable order intent identity.");
        }

        var hasAuthoritativeLastFill = projection.AuthoritativeLastFillQuantity is not null ||
            projection.AuthoritativeLastFillPrice is not null ||
            projection.AuthoritativePositionQuantity is not null;
        if (hasAuthoritativeLastFill &&
            (projection.AuthoritativeLastFillQuantity is null or <= 0m ||
             projection.AuthoritativeLastFillPrice is null or <= 0m ||
             projection.AuthoritativePositionQuantity is not { } position ||
             position != Decimal.Truncate(position)))
        {
            throw new InvalidOperationException(
                "Stream position projection requires positive execution quantity and whole-share position.");
        }
    }

    private static void EnsureExactPositionReplay(
        PositionEventRecord existing,
        OrderIntentRecord intent,
        OrderTransitionRequest request,
        OrderFillProjection projection)
    {
        if (!existing.AccountId.Equals(intent.AccountId, StringComparison.Ordinal) ||
            !existing.Symbol.Equals(projection.Symbol.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !existing.Side.Equals(projection.Side.Trim(), StringComparison.OrdinalIgnoreCase) ||
            !existing.BrokerOrderId.Equals(request.BrokerOrderId, StringComparison.Ordinal) ||
            !existing.ClientOrderId.Equals(intent.ClientOrderId, StringComparison.Ordinal) ||
            projection.AuthoritativeLastFillQuantity is { } lastFill && existing.FillQuantity != lastFill ||
            projection.AuthoritativeLastFillPrice is { } lastPrice && existing.FillPrice != lastPrice)
        {
            throw new InvalidOperationException(
                $"Execution '{projection.ExecutionId}' was replayed with conflicting position data.");
        }
    }

    private static string ResolvePositionStrategy(
        PositionEventRecord? previous,
        string executionStrategyId,
        decimal quantityAfter)
    {
        if (previous is null || previous.QuantityAfter == 0m)
        {
            return executionStrategyId;
        }

        if (quantityAfter == 0m ||
            Math.Sign(previous.QuantityAfter) == Math.Sign(quantityAfter))
        {
            return previous.StrategyId;
        }

        return executionStrategyId;
    }

    private static async Task ApplyRiskReservationTransitionAsync(
        TradingFlowDbContext context,
        OrderIntentRecord intent,
        OrderEventRecord current,
        OrderTransitionRequest request,
        CancellationToken cancellationToken)
    {
        var reservation = await context.PortfolioRiskReservations
            .SingleOrDefaultAsync(
                record => record.ClientOrderId == intent.ClientOrderId,
                cancellationToken);
        if (reservation is null || reservation.State == PortfolioRiskReservationState.Released)
        {
            return;
        }

        var previousReservationState = reservation.State;
        var cumulativeFilled = request.FilledQuantity ??
            current.FilledQuantity ??
            reservation.CumulativeFilledQuantity;
        if (cumulativeFilled < reservation.CumulativeFilledQuantity ||
            cumulativeFilled > reservation.RequestedQuantity)
        {
            throw new InvalidOperationException(
                $"Order '{intent.ClientOrderId}' has invalid cumulative fill {cumulativeFilled} " +
                $"for reserved quantity {reservation.RequestedQuantity}.");
        }

        var changedAtUtc = request.LocalTimestampUtc.ToUniversalTime();
        switch (request.NewState)
        {
            case OrderState.Acked:
                reservation.State = PortfolioRiskReservationState.BrokerAccepted;
                reservation.BrokerAcceptedAtUtc =
                    (request.BrokerTimestampUtc ?? request.LocalTimestampUtc).ToUniversalTime();
                break;
            case OrderState.PartiallyFilled:
                reservation.State = PortfolioRiskReservationState.PartiallyFilled;
                reservation.CumulativeFilledQuantity = cumulativeFilled;
                reservation.PendingQuantity = reservation.RequestedQuantity - cumulativeFilled;
                reservation.OpenPositionQuantity = cumulativeFilled;
                reservation.LatestFillAtUtc =
                    (request.BrokerTimestampUtc ?? request.LocalTimestampUtc).ToUniversalTime();
                break;
            case OrderState.Filled:
                if (cumulativeFilled != reservation.RequestedQuantity)
                {
                    throw new InvalidOperationException(
                        $"FILLED order '{intent.ClientOrderId}' reports {cumulativeFilled} of " +
                        $"{reservation.RequestedQuantity} reserved shares.");
                }

                reservation.State = PortfolioRiskReservationState.BackingOpenPosition;
                reservation.CumulativeFilledQuantity = cumulativeFilled;
                reservation.PendingQuantity = 0m;
                reservation.OpenPositionQuantity = cumulativeFilled;
                reservation.LatestFillAtUtc =
                    (request.BrokerTimestampUtc ?? request.LocalTimestampUtc).ToUniversalTime();
                break;
            case OrderState.Canceled or OrderState.Rejected or OrderState.Expired:
                reservation.CumulativeFilledQuantity = cumulativeFilled;
                reservation.PendingQuantity = 0m;
                if (cumulativeFilled == 0m)
                {
                    reservation.State = PortfolioRiskReservationState.Released;
                    reservation.ReleasedAtUtc = changedAtUtc;
                    reservation.ReleaseReason = request.NewState.ToStorageValue().ToLowerInvariant();
                }
                else
                {
                    var retainedFraction = cumulativeFilled / reservation.RequestedQuantity;
                    reservation.State = PortfolioRiskReservationState.BackingOpenPosition;
                    reservation.ReservedBuyingPower *= retainedFraction;
                    reservation.ReservedGrossExposure *= retainedFraction;
                    reservation.ReservedNetExposure *= retainedFraction;
                    reservation.ReservedPortfolioRisk *= retainedFraction;
                    reservation.CumulativeFilledQuantity = cumulativeFilled;
                    reservation.OpenPositionQuantity = cumulativeFilled;
                    reservation.LatestFillAtUtc ??=
                        (request.BrokerTimestampUtc ?? request.LocalTimestampUtc).ToUniversalTime();
                    reservation.ReleaseReason = "unfilled_remainder_released";
                }
                break;
            default:
                return;
        }

        reservation.StateChangedAtUtc = changedAtUtc;
        reservation.Version = checked(reservation.Version + 1);
        context.RiskEvents.Add(new RiskEventRecord
        {
            EventType = reservation.State == PortfolioRiskReservationState.Released
                ? "portfolio_capacity_released"
                : "portfolio_reservation_state_changed",
            Severity = "information",
            Symbol = reservation.Symbol,
            ObservedValue = reservation.ReservedPortfolioRisk,
            LimitValue = reservation.MaxPortfolioRisk,
            OccurredAtUtc = changedAtUtc,
            DetailsJson = JsonSerializer.Serialize(new
            {
                reservation.IntentId,
                reservation.ClientOrderId,
                previousState = previousReservationState.ToString(),
                state = reservation.State.ToString(),
                orderState = request.NewState.ToStorageValue(),
                reservation.CumulativeFilledQuantity,
                reservation.PendingQuantity,
                reservation.RequestedQuantity,
                reservation.ReleaseReason
            }),
            RunId = intent.RunId,
            SchemaVersion = intent.SchemaVersion,
            ConfigHash = intent.ConfigHash,
            CodeVersion = intent.CodeVersion
        });
    }

    private static bool IsExactReplay(OrderEventRecord current, OrderTransitionRequest request) =>
        SameOptional(current.BrokerOrderId, request.BrokerOrderId) &&
        current.FilledQuantity == request.FilledQuantity &&
        current.FillPrice == request.FillPrice;

    private static bool SameOptional(string? current, string? requested) =>
        String.IsNullOrWhiteSpace(requested) || String.Equals(current, requested, StringComparison.Ordinal);

    private static string? ResolveBrokerOrderId(string? current, string? requested)
    {
        var normalizedRequested = String.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        if (!String.IsNullOrWhiteSpace(current) &&
            normalizedRequested is not null &&
            !current.Equals(normalizedRequested, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Broker order ID cannot change from '{current}' to '{normalizedRequested}'.");
        }

        return current ?? normalizedRequested;
    }

    private static void ValidateRequest(OrderTransitionRequest request)
    {
        _ = NormalizeClientOrderId(request.ClientOrderId);
        if (String.IsNullOrWhiteSpace(request.Source) || String.IsNullOrWhiteSpace(request.PayloadJson))
        {
            throw new InvalidOperationException("Order event source and payload are required.");
        }

        var source = request.Source.Trim().ToLowerInvariant();
        if (source is not ("engine" or "broker_stream" or "broker_rest"))
        {
            throw new InvalidOperationException(
                $"Unsupported order event source '{request.Source}'.");
        }

        if (source is "broker_stream" or "broker_rest" &&
            (String.IsNullOrWhiteSpace(request.BrokerOrderId) || request.BrokerTimestampUtc is null))
        {
            throw new InvalidOperationException(
                "Broker-sourced order events require broker order ID and broker timestamp.");
        }

        if (request.FilledQuantity is < 0m || request.FillPrice is <= 0m)
        {
            throw new InvalidOperationException("Filled quantity cannot be negative and fill price must be positive.");
        }

        if (request.NewState is (OrderState.PartiallyFilled or OrderState.Filled) &&
            request.FilledQuantity is null or <= 0m)
        {
            throw new InvalidOperationException("Fill states require a positive cumulative filled quantity.");
        }
    }

    private static string NormalizeClientOrderId(string clientOrderId) =>
        !String.IsNullOrWhiteSpace(clientOrderId)
            ? clientOrderId.Trim()
            : throw new InvalidOperationException("Client order ID is required.");

    private static string NormalizeAccountId(string accountId) =>
        !String.IsNullOrWhiteSpace(accountId)
            ? accountId.Trim()
            : throw new InvalidOperationException("Account ID is required.");

    private static bool IsReconcilable(OrderState state) => state is
        OrderState.Submitted or
        OrderState.Acked or
        OrderState.PartiallyFilled or
        OrderState.CancelPending;

    private static OrderStateSnapshot ToSnapshot(OrderEventRecord orderEvent) => new(
        orderEvent.RunId,
        orderEvent.ClientOrderId,
        orderEvent.BrokerOrderId,
        OrderStateMachine.ParseStorageValue(orderEvent.NewState),
        orderEvent.LocalTimestampUtc,
        orderEvent.BrokerTimestampUtc,
        orderEvent.FilledQuantity,
        orderEvent.FillPrice,
        orderEvent.EventId);
}

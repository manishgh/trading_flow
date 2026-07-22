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
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var latestEventIds = context.OrderEvents
            .GroupBy(record => record.ClientOrderId)
            .Select(group => group.Max(record => record.EventId));
        var records = await context.OrderEvents
            .AsNoTracking()
            .Where(record => latestEventIds.Contains(record.EventId))
            .OrderBy(record => record.ClientOrderId)
            .ToListAsync(cancellationToken);

        return records
            .Where(record => IsReconcilable(OrderStateMachine.ParseStorageValue(record.NewState)))
            .Select(ToSnapshot)
            .ToArray();
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
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
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
                    return new OrderTransitionResult(ToSnapshot(current), Applied: false);
                }

                if (currentState != OrderState.PartiallyFilled ||
                    request.ExpectedPreviousState != OrderState.PartiallyFilled ||
                    request.FilledQuantity is null ||
                    request.FilledQuantity <= (current.FilledQuantity ?? 0m))
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
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new OrderTransitionResult(ToSnapshot(orderEvent), Applied: true);
        }
        finally
        {
            transitionLock.Release();
        }
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

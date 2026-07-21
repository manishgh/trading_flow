using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

/// <summary>
/// Commits an order intent as one append-only SQLite transaction.
/// </summary>
public sealed class SqliteOrderIntentRepository : IOrderIntentRepository
{
    private readonly IDbContextFactory<TradingFlowDbContext> contextFactory;
    private readonly SemaphoreSlim reservationLock = new(1, 1);

    public SqliteOrderIntentRepository(IDbContextFactory<TradingFlowDbContext> contextFactory)
    {
        this.contextFactory = contextFactory;
    }

    public async Task<OrderIntentRecord> ReserveAsync(
        ProductionRun run,
        OrderIntentReservation reservation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(reservation);
        ValidateReservation(run, reservation);

        await reservationLock.WaitAsync(cancellationToken);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            var existingRun = await context.ProductionRuns
                .SingleOrDefaultAsync(record => record.RunId == run.RunId, cancellationToken);
            var existingIntent = await context.OrderIntents
                .AsNoTracking()
                .SingleOrDefaultAsync(record => record.IntentId == reservation.IntentId, cancellationToken);
            if (existingIntent is not null)
            {
                if (existingRun is null)
                {
                    throw new InvalidOperationException(
                        $"Intent {reservation.IntentId:N} has no owning production run.");
                }

                EnsureRunCanSubmit(existingRun, run);
                EnsureSameLogicalIntent(existingIntent, run, reservation);
                return existingIntent;
            }

            if (existingRun is null)
            {
                context.ProductionRuns.Add(run);
            }
            else
            {
                EnsureRunCanSubmit(existingRun, run);
            }

            var previousSequence = await context.OrderIntents
                .Where(record =>
                    record.StrategyId == reservation.StrategyId &&
                    record.Side == reservation.Side &&
                    record.Symbol == reservation.Symbol &&
                    record.SessionDate == reservation.SessionDate)
                .MaxAsync(record => (int?)record.SequenceNumber, cancellationToken) ?? 0;
            var sequenceNumber = checked(previousSequence + 1);
            var clientOrderId = TradingFlow.Domain.Orders.ClientOrderIdFactory.Create(
                reservation.StrategyId,
                reservation.Side,
                reservation.Symbol,
                reservation.SessionDate,
                sequenceNumber,
                reservation.IntentId);

            var intent = new OrderIntentRecord
            {
                IntentId = reservation.IntentId,
                CandidateId = reservation.CandidateId,
                ClientOrderId = clientOrderId,
                StrategyId = reservation.StrategyId,
                Symbol = reservation.Symbol,
                Side = reservation.Side,
                OrderType = reservation.OrderType,
                TimeInForce = reservation.TimeInForce,
                RequestedQuantity = reservation.RequestedQuantity,
                LimitPrice = reservation.LimitPrice,
                StopPrice = reservation.StopPrice,
                SessionDate = reservation.SessionDate,
                SequenceNumber = sequenceNumber,
                CreatedAtUtc = reservation.CreatedAtUtc,
                RequestJson = reservation.RequestJson,
                RunId = run.RunId,
                SchemaVersion = run.SchemaVersion,
                ConfigHash = run.ConfigHash,
                CodeVersion = run.CodeVersion
            };

            context.OrderIntents.Add(intent);
            context.OrderEvents.Add(new OrderEventRecord
            {
                ClientOrderId = clientOrderId,
                PreviousState = null,
                NewState = OrderState.Intent.ToStorageValue(),
                Source = "engine",
                LocalTimestampUtc = reservation.CreatedAtUtc.ToUniversalTime(),
                PayloadJson = reservation.RequestJson,
                RunId = run.RunId,
                SchemaVersion = run.SchemaVersion,
                ConfigHash = run.ConfigHash,
                CodeVersion = run.CodeVersion
            });
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return intent;
        }
        finally
        {
            reservationLock.Release();
        }
    }

    private static void ValidateReservation(ProductionRun run, OrderIntentReservation reservation)
    {
        if (run.RunId == Guid.Empty || reservation.IntentId == Guid.Empty)
        {
            throw new InvalidOperationException("Run ID and intent ID are required.");
        }

        if (reservation.RequestedQuantity <= 0m || String.IsNullOrWhiteSpace(reservation.RequestJson))
        {
            throw new InvalidOperationException("Order intent quantity and request payload are required.");
        }

        if (run.ConfigHash.Length != 64 || String.IsNullOrWhiteSpace(run.CodeVersion))
        {
            throw new InvalidOperationException("Order intent provenance is incomplete.");
        }
    }

    private static void EnsureSameRun(ProductionRun existing, ProductionRun requested)
    {
        if (existing.SchemaVersion != requested.SchemaVersion ||
            !existing.ConfigHash.Equals(requested.ConfigHash, StringComparison.Ordinal) ||
            !existing.CodeVersion.Equals(requested.CodeVersion, StringComparison.Ordinal) ||
            !existing.Profile.Equals(requested.Profile, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Run {requested.RunId:N} already exists with different provenance.");
        }
    }

    private static void EnsureRunCanSubmit(ProductionRun existing, ProductionRun requested)
    {
        EnsureSameRun(existing, requested);
        if (!existing.Status.Equals("running", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot submit an order intent for run {requested.RunId:N} in state '{existing.Status}'.");
        }
    }

    private static void EnsureSameLogicalIntent(
        OrderIntentRecord existing,
        ProductionRun run,
        OrderIntentReservation requested)
    {
        if (existing.RunId != run.RunId ||
            existing.SchemaVersion != run.SchemaVersion ||
            !existing.ConfigHash.Equals(run.ConfigHash, StringComparison.Ordinal) ||
            !existing.CodeVersion.Equals(run.CodeVersion, StringComparison.Ordinal) ||
            existing.CandidateId != requested.CandidateId ||
            !existing.StrategyId.Equals(requested.StrategyId, StringComparison.Ordinal) ||
            !existing.Symbol.Equals(requested.Symbol, StringComparison.Ordinal) ||
            !existing.Side.Equals(requested.Side, StringComparison.Ordinal) ||
            !existing.OrderType.Equals(requested.OrderType, StringComparison.Ordinal) ||
            !existing.TimeInForce.Equals(requested.TimeInForce, StringComparison.Ordinal) ||
            existing.RequestedQuantity != requested.RequestedQuantity ||
            existing.LimitPrice != requested.LimitPrice ||
            existing.StopPrice != requested.StopPrice ||
            existing.SessionDate != requested.SessionDate ||
            !existing.RequestJson.Equals(requested.RequestJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Intent {requested.IntentId:N} was retried with a different order request.");
        }
    }
}

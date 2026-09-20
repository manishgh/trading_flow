using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Data.Orders;

/// <summary>
/// Persists authoritative account-position changes as an append-only, execution-idempotent journal.
/// </summary>
public sealed class SqlitePositionLedgerRepository(
    IDbContextFactory<TradingFlowDbContext> contextFactory) : IPositionLedgerRepository
{
    private readonly SemaphoreSlim appendLock = new(1, 1);

    public async Task<PositionLedgerSnapshot> AppendFillAsync(
        PositionFillAppendRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        await appendLock.WaitAsync(cancellationToken);
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await context.Database.OpenConnectionAsync(cancellationToken);
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            await using var transaction = connection.BeginTransaction(
                IsolationLevel.Serializable,
                deferred: false);
            context.Database.UseTransaction(transaction);
            var executionId = request.ExecutionId.Trim();
            var accountId = request.AccountId.Trim();
            var existing = await context.PositionEvents
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.AccountId == accountId && record.ExecutionId == executionId,
                    cancellationToken);
            if (existing is not null)
            {
                EnsureExactReplay(existing, request);
                return ToSnapshot(existing);
            }

            var symbol = request.Symbol.Trim().ToUpperInvariant();
            await ProductionRunPersistence.EnsureAsync(context, request.Run, cancellationToken);
            var previous = await context.PositionEvents
                .AsNoTracking()
                .Where(item => item.AccountId == accountId && item.Symbol == symbol)
                .OrderByDescending(item => item.PositionEventId)
                .FirstOrDefaultAsync(cancellationToken);
            var startsNewGeneration = StartsNewPositionGeneration(previous, request.QuantityAfter);
            var record = new PositionEventRecord
            {
                AccountId = accountId,
                Symbol = symbol,
                StrategyId = ResolvePositionStrategy(previous, request),
                ExecutionStrategyId = request.ExecutionStrategyId.Trim(),
                PositionGenerationEventId = startsNewGeneration
                    ? 0
                    : previous?.PositionGenerationEventId ?? 0,
                PositionGenerationClientOrderId = startsNewGeneration
                    ? request.ClientOrderId.Trim()
                    : ResolvePositionGenerationClientOrderId(previous),
                QuantityAfter = request.QuantityAfter,
                FillQuantity = request.FillQuantity,
                FillPrice = request.FillPrice,
                Side = request.Side.Trim().ToLowerInvariant(),
                BrokerOrderId = request.BrokerOrderId.Trim(),
                ClientOrderId = request.ClientOrderId.Trim(),
                ExecutionId = executionId,
                Source = request.Source.Trim().ToLowerInvariant(),
                BrokerTimestampUtc = request.BrokerTimestampUtc.ToUniversalTime(),
                LocalTimestampUtc = request.LocalTimestampUtc.ToUniversalTime(),
                PayloadJson = request.PayloadJson,
                RunId = request.Run.RunId,
                SchemaVersion = request.Run.SchemaVersion,
                ConfigHash = request.Run.ConfigHash,
                CodeVersion = request.Run.CodeVersion
            };
            context.PositionEvents.Add(record);
            await UpdatePositionBackedRiskAsync(
                context,
                request,
                accountId,
                symbol,
                cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            if (startsNewGeneration)
            {
                // SQLite assigns the append-only event id. Persisting that id back onto
                // the same row gives every later partial fill/exit a stable generation.
                record.PositionGenerationEventId = record.PositionEventId;
                await context.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return ToSnapshot(record);
        }
        finally
        {
            appendLock.Release();
        }
    }

    private static async Task UpdatePositionBackedRiskAsync(
        TradingFlowDbContext context,
        PositionFillAppendRequest request,
        string accountId,
        string symbol,
        CancellationToken cancellationToken)
    {
        var reservations = await context.PortfolioRiskReservations
            .Where(record =>
                record.AccountId == accountId &&
                record.Symbol == symbol &&
                record.State != PortfolioRiskReservationState.Released)
            .ToArrayAsync(cancellationToken);
        if (reservations.Length == 0)
        {
            return;
        }

        if (reservations.Length != 1)
        {
            throw new InvalidOperationException(
                $"Symbol {symbol} has {reservations.Length} active portfolio owners.");
        }

        var reservation = reservations[0];
        var changedAtUtc = request.LocalTimestampUtc.ToUniversalTime();
        var previousState = reservation.State;
        var previousOpenQuantity = reservation.OpenPositionQuantity;
        reservation.OpenPositionQuantity = Math.Abs(request.QuantityAfter);
        if (request.QuantityAfter == 0m)
        {
            if (reservation.PendingQuantity > 0m)
            {
                ResizeReservationForOwnedQuantity(
                    reservation,
                    reservation.PendingQuantity);
                reservation.State = PortfolioRiskReservationState.PartiallyFilled;
                reservation.ReleasedAtUtc = null;
                reservation.ReleaseReason = "filled_quantity_flat_pending_entry_remainder";
            }
            else
            {
                ResizeReservationForOwnedQuantity(reservation, 0m);
                reservation.ReservedPositionSlots = 0;
                reservation.State = PortfolioRiskReservationState.Released;
                reservation.ReleasedAtUtc = changedAtUtc;
                reservation.ReleaseReason = "authoritative_position_flat";
            }
        }
        else if (reservation.State == PortfolioRiskReservationState.BackingOpenPosition)
        {
            if (reservation.OpenPositionQuantity > reservation.CumulativeFilledQuantity)
            {
                throw new InvalidOperationException(
                    $"Position quantity {reservation.OpenPositionQuantity} exceeds owned entry quantity " +
                    $"{reservation.CumulativeFilledQuantity} for {symbol}.");
            }

            ResizeReservationForOwnedQuantity(reservation, reservation.OpenPositionQuantity);
        }

        reservation.StateChangedAtUtc = changedAtUtc;
        reservation.Version = checked(reservation.Version + 1);
        context.RiskEvents.Add(new RiskEventRecord
        {
            EventType = reservation.State == PortfolioRiskReservationState.Released
                ? "portfolio_capacity_released"
                : "position_backed_capacity_updated",
            Severity = "information",
            Symbol = symbol,
            ObservedValue = reservation.ReservedPortfolioRisk,
            LimitValue = reservation.MaxPortfolioRisk,
            OccurredAtUtc = changedAtUtc,
            DetailsJson = JsonSerializer.Serialize(new
            {
                reservation.IntentId,
                entryClientOrderId = reservation.ClientOrderId,
                previousState = previousState.ToString(),
                state = reservation.State.ToString(),
                previousOpenQuantity,
                reservation.OpenPositionQuantity,
                request.ExecutionId,
                executionClientOrderId = request.ClientOrderId,
                request.QuantityAfter
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

    public async Task<IReadOnlyList<PositionLedgerSnapshot>> ListCurrentAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = NormalizeAccountId(accountId);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var latestIds = context.PositionEvents
            .Where(record => record.AccountId == normalizedAccountId)
            .GroupBy(record => record.Symbol)
            .Select(group => group.Max(record => record.PositionEventId));
        return await context.PositionEvents
            .AsNoTracking()
            .Where(record => latestIds.Contains(record.PositionEventId))
            .OrderBy(record => record.Symbol)
            .Select(record => new PositionLedgerSnapshot(
                record.AccountId,
                record.Symbol,
                record.QuantityAfter,
                record.StrategyId,
                record.BrokerTimestampUtc,
                record.LocalTimestampUtc,
                record.PositionEventId,
                record.PositionGenerationEventId,
                record.PositionGenerationClientOrderId,
                record.ClientOrderId,
                record.FillPrice,
                record.Side))
            .ToArrayAsync(cancellationToken);
    }

    public Task<IReadOnlyList<RunOwnedPositionLedgerSnapshot>> ListCurrentForRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("Run ID is required.", nameof(runId));
        }

        return ListCurrentOwnedAsync(runId, symbol: null, cancellationToken);
    }

    public Task<IReadOnlyList<RunOwnedPositionLedgerSnapshot>> ListCurrentOwnersForSymbolAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = !String.IsNullOrWhiteSpace(symbol)
            ? symbol.Trim().ToUpperInvariant()
            : throw new ArgumentException("Position symbol is required.", nameof(symbol));
        return ListCurrentOwnedAsync(runId: null, normalizedSymbol, cancellationToken);
    }

    private async Task<IReadOnlyList<RunOwnedPositionLedgerSnapshot>> ListCurrentOwnedAsync(
        Guid? runId,
        string? symbol,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var latestIds = context.PositionEvents
            .GroupBy(record => new { record.AccountId, record.Symbol })
            .Select(group => group.Max(record => record.PositionEventId));
        var query =
            from position in context.PositionEvents.AsNoTracking()
            join entry in context.OrderIntents.AsNoTracking()
                on position.PositionGenerationClientOrderId equals entry.ClientOrderId
            where latestIds.Contains(position.PositionEventId) && position.QuantityAfter != 0m
            select new { Position = position, OwningRunId = entry.RunId };

        if (runId is { } requestedRunId)
        {
            query = query.Where(item => item.OwningRunId == requestedRunId);
        }

        if (symbol is not null)
        {
            query = query.Where(item => item.Position.Symbol == symbol);
        }

        var rows = await query
            .OrderBy(item => item.Position.AccountId)
            .ThenBy(item => item.Position.Symbol)
            .ToArrayAsync(cancellationToken);
        return rows
            .Select(item => new RunOwnedPositionLedgerSnapshot(
                ToSnapshot(item.Position),
                item.OwningRunId))
            .ToArray();
    }

    public async Task<decimal> GetAccountedFillQuantityAsync(
        string accountId,
        string brokerOrderId,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = NormalizeAccountId(accountId);
        var normalized = !String.IsNullOrWhiteSpace(brokerOrderId)
            ? brokerOrderId.Trim()
            : throw new ArgumentException("Broker order ID is required.", nameof(brokerOrderId));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PositionEvents
            .Where(record =>
                record.AccountId == normalizedAccountId &&
                record.BrokerOrderId == normalized)
            .SumAsync(record => record.FillQuantity, cancellationToken);
    }

    private static void Validate(PositionFillAppendRequest request)
    {
        if (request.Run.RunId == Guid.Empty || request.Run.ConfigHash.Length != 64 ||
            String.IsNullOrWhiteSpace(request.Run.CodeVersion))
        {
            throw new InvalidOperationException("Position event provenance is incomplete.");
        }

        if (String.IsNullOrWhiteSpace(request.Symbol) ||
            String.IsNullOrWhiteSpace(request.AccountId) ||
            String.IsNullOrWhiteSpace(request.ExecutionStrategyId) ||
            String.IsNullOrWhiteSpace(request.Side) ||
            String.IsNullOrWhiteSpace(request.BrokerOrderId) ||
            String.IsNullOrWhiteSpace(request.ClientOrderId) ||
            String.IsNullOrWhiteSpace(request.ExecutionId) ||
            String.IsNullOrWhiteSpace(request.PayloadJson) ||
            request.FillQuantity <= 0m || request.FillPrice <= 0m)
        {
            throw new InvalidOperationException("A position fill requires identity, positive fill values, and payload.");
        }

        if (request.Source is not ("broker_stream" or "broker_rest"))
        {
            throw new InvalidOperationException($"Unsupported position event source '{request.Source}'.");
        }
    }

    private static void EnsureExactReplay(PositionEventRecord existing, PositionFillAppendRequest request)
    {
        if (!existing.AccountId.Equals(request.AccountId.Trim(), StringComparison.Ordinal) ||
            !existing.Symbol.Equals(request.Symbol.Trim(), StringComparison.OrdinalIgnoreCase) ||
            existing.QuantityAfter != request.QuantityAfter ||
            existing.FillQuantity != request.FillQuantity ||
            existing.FillPrice != request.FillPrice ||
            !existing.ExecutionStrategyId.Equals(request.ExecutionStrategyId.Trim(), StringComparison.Ordinal) ||
            !existing.BrokerOrderId.Equals(request.BrokerOrderId.Trim(), StringComparison.Ordinal) ||
            !existing.ClientOrderId.Equals(request.ClientOrderId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Execution '{request.ExecutionId}' was replayed with conflicting position data.");
        }
    }

    public async Task<PositionLedgerSnapshot?> GetCurrentAsync(
        string accountId,
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccountId = NormalizeAccountId(accountId);
        var normalized = !String.IsNullOrWhiteSpace(symbol)
            ? symbol.Trim().ToUpperInvariant()
            : throw new ArgumentException("Position symbol is required.", nameof(symbol));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var record = await context.PositionEvents
            .AsNoTracking()
            .Where(item => item.AccountId == normalizedAccountId && item.Symbol == normalized)
            .OrderByDescending(item => item.PositionEventId)
            .FirstOrDefaultAsync(cancellationToken);
        return record is null ? null : ToSnapshot(record);
    }

    private static PositionLedgerSnapshot ToSnapshot(PositionEventRecord record) => new(
        record.AccountId,
        record.Symbol,
        record.QuantityAfter,
        record.StrategyId,
        record.BrokerTimestampUtc,
        record.LocalTimestampUtc,
        record.PositionEventId,
        record.PositionGenerationEventId,
        record.PositionGenerationClientOrderId,
        record.ClientOrderId,
        record.FillPrice,
        record.Side);

    private static string ResolvePositionStrategy(
        PositionEventRecord? previous,
        PositionFillAppendRequest request)
    {
        var executionStrategy = request.ExecutionStrategyId.Trim();
        if (previous is null || previous.QuantityAfter == 0m)
        {
            return executionStrategy;
        }

        if (request.QuantityAfter == 0m)
        {
            return previous.StrategyId;
        }

        return Math.Sign(previous.QuantityAfter) == Math.Sign(request.QuantityAfter)
            ? previous.StrategyId
            : executionStrategy;
    }

    private static bool StartsNewPositionGeneration(
        PositionEventRecord? previous,
        decimal quantityAfter)
    {
        if (quantityAfter == 0m)
        {
            return false;
        }

        return previous is null ||
            previous.QuantityAfter == 0m ||
            Math.Sign(previous.QuantityAfter) != Math.Sign(quantityAfter);
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

    private static string NormalizeAccountId(string accountId) =>
        !String.IsNullOrWhiteSpace(accountId)
            ? accountId.Trim()
            : throw new ArgumentException("Position account ID is required.", nameof(accountId));
}

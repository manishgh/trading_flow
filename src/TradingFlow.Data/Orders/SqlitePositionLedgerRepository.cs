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
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var executionId = request.ExecutionId.Trim();
            var existing = await context.PositionEvents
                .AsNoTracking()
                .SingleOrDefaultAsync(record => record.ExecutionId == executionId, cancellationToken);
            if (existing is not null)
            {
                EnsureExactReplay(existing, request);
                return ToSnapshot(existing);
            }

            var symbol = request.Symbol.Trim().ToUpperInvariant();
            await ProductionRunPersistence.EnsureAsync(context, request.Run, cancellationToken);
            var record = new PositionEventRecord
            {
                Symbol = symbol,
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
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToSnapshot(record);
        }
        finally
        {
            appendLock.Release();
        }
    }

    public async Task<IReadOnlyList<PositionLedgerSnapshot>> ListCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var latestIds = context.PositionEvents
            .GroupBy(record => record.Symbol)
            .Select(group => group.Max(record => record.PositionEventId));
        return await context.PositionEvents
            .AsNoTracking()
            .Where(record => latestIds.Contains(record.PositionEventId))
            .OrderBy(record => record.Symbol)
            .Select(record => new PositionLedgerSnapshot(
                record.Symbol,
                record.QuantityAfter,
                record.BrokerTimestampUtc,
                record.LocalTimestampUtc,
                record.PositionEventId,
                record.ClientOrderId,
                record.FillPrice,
                record.Side))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<decimal> GetAccountedFillQuantityAsync(
        string brokerOrderId,
        CancellationToken cancellationToken = default)
    {
        var normalized = !String.IsNullOrWhiteSpace(brokerOrderId)
            ? brokerOrderId.Trim()
            : throw new ArgumentException("Broker order ID is required.", nameof(brokerOrderId));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PositionEvents
            .Where(record => record.BrokerOrderId == normalized)
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
        if (!existing.Symbol.Equals(request.Symbol.Trim(), StringComparison.OrdinalIgnoreCase) ||
            existing.QuantityAfter != request.QuantityAfter ||
            existing.FillQuantity != request.FillQuantity ||
            existing.FillPrice != request.FillPrice ||
            !existing.BrokerOrderId.Equals(request.BrokerOrderId.Trim(), StringComparison.Ordinal) ||
            !existing.ClientOrderId.Equals(request.ClientOrderId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Execution '{request.ExecutionId}' was replayed with conflicting position data.");
        }
    }

    public async Task<PositionLedgerSnapshot?> GetCurrentAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalized = !String.IsNullOrWhiteSpace(symbol)
            ? symbol.Trim().ToUpperInvariant()
            : throw new ArgumentException("Position symbol is required.", nameof(symbol));
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var record = await context.PositionEvents
            .AsNoTracking()
            .Where(item => item.Symbol == normalized)
            .OrderByDescending(item => item.PositionEventId)
            .FirstOrDefaultAsync(cancellationToken);
        return record is null ? null : ToSnapshot(record);
    }

    private static PositionLedgerSnapshot ToSnapshot(PositionEventRecord record) => new(
        record.Symbol,
        record.QuantityAfter,
        record.BrokerTimestampUtc,
        record.LocalTimestampUtc,
        record.PositionEventId,
        record.ClientOrderId,
        record.FillPrice,
        record.Side);
}

using Microsoft.Extensions.Logging;
using TradingFlow.Domain.Execution;
using TradingFlow.Domain.Locking;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Persistence;

namespace TradingFlow.Engine.Execution;

public sealed record PositionConflictOptions
{
    public PositionConflictOptions(bool allowMultiStrategySameSymbol)
    {
        if (allowMultiStrategySameSymbol)
        {
            throw new InvalidOperationException(
                "Multiple strategies cannot own the same symbol in the production profile.");
        }

        AllowMultiStrategySameSymbol = false;
    }

    public bool AllowMultiStrategySameSymbol { get; }
}

public sealed class PositionConflictException(
    RejectCode rejectCode,
    string message) : InvalidOperationException(message)
{
    public RejectCode RejectCode { get; } = rejectCode;
}

public interface IPositionConflictGuard
{
    Task<T> ExecuteEntryAsync<T>(
        string symbol,
        string strategyId,
        Func<CancellationToken, Task<T>> submit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Serializes entry admission per symbol and rejects cross-strategy ownership while
/// considering both filled positions and entry intents that have not reached a terminal state.
/// </summary>
public sealed class PositionConflictGuard(
    ITickerLockService locks,
    IPositionLedgerRepository positions,
    IOrderIntentRepository intents,
    PositionConflictOptions options,
    ILogger<PositionConflictGuard> logger) : IPositionConflictGuard
{
    private static readonly TimeSpan EntryLeaseTtl = TimeSpan.FromMinutes(2);

    public async Task<T> ExecuteEntryAsync<T>(
        string symbol,
        string strategyId,
        Func<CancellationToken, Task<T>> submit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submit);
        _ = options.AllowMultiStrategySameSymbol;
        var normalizedSymbol = Require(symbol, nameof(symbol)).ToUpperInvariant();
        var normalizedStrategy = Require(strategyId, nameof(strategyId));
        var lockKey = $"exe10:{normalizedSymbol}";
        var owner = $"entry:{Guid.NewGuid():N}";
        var acquired = await locks.TryAcquireLockAsync(
            lockKey,
            owner,
            EntryLeaseTtl,
            cancellationToken);
        if (!acquired)
        {
            throw new PositionConflictException(
                RejectCode.REJECT_RECONCILE_LOCK,
                $"Entry ownership for {normalizedSymbol} is being evaluated by another submission.");
        }

        try
        {
            await EnsureNoCrossStrategyConflictAsync(
                normalizedSymbol,
                normalizedStrategy,
                cancellationToken);
            return await submit(cancellationToken);
        }
        finally
        {
            try
            {
                await locks.ReleaseLockAsync(lockKey, owner, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogCritical(
                    exception,
                    "Entry lease release failed for {Symbol}; lease owner {Owner} will expire after {TtlSeconds}s.",
                    normalizedSymbol,
                    owner,
                    EntryLeaseTtl.TotalSeconds);
            }
        }
    }

    private async Task EnsureNoCrossStrategyConflictAsync(
        string symbol,
        string strategyId,
        CancellationToken cancellationToken)
    {
        var position = await positions.GetCurrentAsync(symbol, cancellationToken);
        if (position is { Quantity: not 0m } &&
            !position.StrategyId.Equals(strategyId, StringComparison.Ordinal))
        {
            throw Conflict(
                symbol,
                strategyId,
                position.StrategyId,
                $"open position quantity {position.Quantity}");
        }

        var conflictingIntent = (await intents.ListActiveForSymbolAsync(symbol, cancellationToken))
            .FirstOrDefault(intent =>
                !intent.StrategyId.Equals(strategyId, StringComparison.Ordinal));
        if (conflictingIntent is not null)
        {
            throw Conflict(
                symbol,
                strategyId,
                conflictingIntent.StrategyId,
                $"active order {conflictingIntent.ClientOrderId} in state {conflictingIntent.State.ToStorageValue()}");
        }
    }

    private static PositionConflictException Conflict(
        string symbol,
        string requestedStrategy,
        string owningStrategy,
        string source) =>
        new(
            RejectCode.REJECT_SETUP_INVALID,
            $"Position conflict for {symbol}: strategy '{requestedStrategy}' cannot enter while " +
            $"strategy '{owningStrategy}' owns {source}.");

    private static string Require(string value, string parameterName) =>
        !String.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException("A non-empty value is required.", parameterName);
}

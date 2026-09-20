namespace TradingFlow.Domain.Persistence;

public sealed record PositionLedgerSnapshot(
    string AccountId,
    string Symbol,
    decimal Quantity,
    string StrategyId,
    DateTimeOffset BrokerTimestampUtc,
    DateTimeOffset LocalTimestampUtc,
    long PositionEventId,
    long PositionGenerationEventId,
    string PositionGenerationClientOrderId,
    string LatestClientOrderId,
    decimal LatestFillPrice,
    string LatestFillSide);

public sealed record RunOwnedPositionLedgerSnapshot(
    PositionLedgerSnapshot Position,
    Guid OwningRunId);

public sealed record PositionFillAppendRequest(
    ProductionRun Run,
    string AccountId,
    string Symbol,
    string ExecutionStrategyId,
    decimal QuantityAfter,
    decimal FillQuantity,
    decimal FillPrice,
    string Side,
    string BrokerOrderId,
    string ClientOrderId,
    string ExecutionId,
    string Source,
    DateTimeOffset BrokerTimestampUtc,
    DateTimeOffset LocalTimestampUtc,
    string PayloadJson);

public interface IPositionLedgerRepository
{
    Task<decimal> GetAccountedFillQuantityAsync(
        string accountId,
        string brokerOrderId,
        CancellationToken cancellationToken = default);

    Task<PositionLedgerSnapshot?> GetCurrentAsync(
        string accountId,
        string symbol,
        CancellationToken cancellationToken = default);

    Task<PositionLedgerSnapshot> AppendFillAsync(
        PositionFillAppendRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PositionLedgerSnapshot>> ListCurrentAsync(
        string accountId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RunOwnedPositionLedgerSnapshot>> ListCurrentForRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RunOwnedPositionLedgerSnapshot>> ListCurrentOwnersForSymbolAsync(
        string symbol,
        CancellationToken cancellationToken = default);
}

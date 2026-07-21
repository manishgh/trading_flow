namespace TradingFlow.Domain.Persistence;

public sealed record PositionLedgerSnapshot(
    string Symbol,
    decimal Quantity,
    DateTimeOffset BrokerTimestampUtc,
    DateTimeOffset LocalTimestampUtc,
    long PositionEventId,
    string LatestClientOrderId,
    decimal LatestFillPrice,
    string LatestFillSide);

public sealed record PositionFillAppendRequest(
    ProductionRun Run,
    string Symbol,
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
        string brokerOrderId,
        CancellationToken cancellationToken = default);

    Task<PositionLedgerSnapshot?> GetCurrentAsync(
        string symbol,
        CancellationToken cancellationToken = default);

    Task<PositionLedgerSnapshot> AppendFillAsync(
        PositionFillAppendRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PositionLedgerSnapshot>> ListCurrentAsync(
        CancellationToken cancellationToken = default);
}

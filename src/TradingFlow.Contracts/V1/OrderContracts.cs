namespace TradingFlow.Contracts.V1;

public sealed record PreviewOrderRequest(
    string ContractVersion,
    string AccountId,
    Guid? CandidateId,
    int? CandidateVersion,
    Guid? CandidateRunId,
    Guid UniverseSnapshotId,
    StrategyReference Strategy,
    string Symbol,
    string Side,
    decimal Quantity,
    string OrderType,
    string TimeInForce,
    decimal? LimitPrice,
    decimal StopLossPrice,
    decimal? TakeProfitPrice,
    bool AllowExtendedHoursTrading,
    string ExecutionPolicy,
    string IdempotencyKey);

public sealed record OrderPreviewResponse(
    string ContractVersion,
    Guid PreviewId,
    string PreviewToken,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string AccountId,
    Guid? CandidateId,
    int? CandidateVersion,
    Guid UniverseSnapshotId,
    StrategyReference Strategy,
    string Symbol,
    string Side,
    decimal Quantity,
    string OrderType,
    string TimeInForce,
    decimal? LimitPrice,
    decimal StopLossPrice,
    decimal? TakeProfitPrice,
    decimal EstimatedNotional,
    decimal MaximumLoss,
    string QuoteIdentity,
    DateTimeOffset QuoteTimestampUtc,
    string RiskSnapshotVersion,
    string ExecutionPolicy,
    string IdempotencyKey,
    IReadOnlyList<string> Warnings);

public sealed record ConfirmOrderRequest(
    string ContractVersion,
    string PreviewToken,
    string IdempotencyKey);

public sealed record CancelOrderRequest(
    string ContractVersion,
    string AccountId,
    Guid OrderIntentId,
    string Reason,
    string IdempotencyKey);

public sealed record ClosePositionRequest(
    string ContractVersion,
    string AccountId,
    string Symbol,
    decimal? Quantity,
    string Reason,
    bool AllowExtendedHoursTrading,
    decimal? LimitPrice,
    string IdempotencyKey);

public sealed record OrderCommandResponse(
    string ContractVersion,
    Guid OrderIntentId,
    string ClientOrderId,
    string? BrokerOrderId,
    string State,
    string AccountId,
    string Symbol,
    string Side,
    decimal RequestedQuantity,
    decimal? FilledQuantity,
    decimal? AverageFillPrice,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string IdempotencyKey,
    string? RejectionCode,
    string? RejectionReason);

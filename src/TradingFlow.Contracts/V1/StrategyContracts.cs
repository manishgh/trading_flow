namespace TradingFlow.Contracts.V1;

public sealed record StrategyCatalogResponse(
    string ContractVersion,
    string Mode,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<StrategyCatalogItemResponse> Strategies);

public sealed record StrategyCatalogItemResponse(
    StrategyReference Identity,
    string Name,
    string Horizon,
    string Direction,
    string Lifecycle,
    bool UsesNews,
    IReadOnlyList<string> RequiredTimeframes,
    StrategyEvidenceSummaryResponse? LatestEvidence);

public sealed record StrategyEvidenceSummaryResponse(
    Guid EvidenceId,
    DateTimeOffset CompletedAtUtc,
    decimal? TotalReturnPct,
    decimal? MaxDrawdownPct,
    decimal? WinRatePct,
    int TradeCount,
    string Decision,
    string EvidenceSha256);

namespace TradingFlow.Contracts.V1;

public sealed record PortfolioResponse(string ContractVersion, bool Available, string Detail,
    DateTimeOffset ReadAtUtc, IReadOnlyList<PortfolioPositionResponse> Positions, decimal? Cash = null, decimal? Invested = null);
public sealed record PortfolioPositionResponse(string Symbol, decimal Quantity, decimal? AverageEntry,
    decimal? CostBasis, decimal? MarketValue, decimal? UnrealizedPl);
public sealed record OrderJournalEntry(Guid? OrderIntentId, string? AccountId, Guid RunId, Guid? CandidateId,
    string ClientOrderId, string? BrokerOrderId, string Symbol, string Side, string OrderType,
    string TimeInForce, decimal Quantity, decimal? LimitPrice, string State, decimal? FilledQuantity,
    decimal? FillPrice, DateTimeOffset UpdatedAtUtc);

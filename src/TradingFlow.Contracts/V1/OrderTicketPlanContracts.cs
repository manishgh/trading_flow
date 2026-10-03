namespace TradingFlow.Contracts.V1;

/// <summary>Advisory prices only. A plan never grants order admission.</summary>
public sealed record OrderTicketPlanRequest(string ContractVersion, string Symbol,
    StrategyReference? Strategy, decimal? LimitPrice = null);

public sealed record OrderTicketPlanResponse(string ContractVersion, string Symbol,
    StrategyReference? Strategy, decimal? Limit, decimal? Stop, decimal? Target,
    string StopNote, string TargetNote, decimal? RiskBudget);

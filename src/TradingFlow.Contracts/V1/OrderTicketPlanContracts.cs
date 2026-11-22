namespace TradingFlow.Contracts.V1;

/// <summary>Advisory prices only. A plan never grants order admission.</summary>
public sealed record OrderTicketPlanRequest(string ContractVersion, string Symbol,
    StrategyReference? Strategy, decimal? LimitPrice = null);

/// <param name="Direction">"long" or "short": which side the stop and target were planned for.</param>
public sealed record OrderTicketPlanResponse(string ContractVersion, string Symbol,
    StrategyReference? Strategy, decimal? Limit, decimal? Stop, decimal? Target,
    string StopNote, string TargetNote, decimal? RiskBudget, string Direction = "long");

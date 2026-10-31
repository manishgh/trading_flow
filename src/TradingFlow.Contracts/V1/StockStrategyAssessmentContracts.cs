namespace TradingFlow.Contracts.V1;

/// <summary>A selectable exact strategy and, when available, its recorded evaluation run.</summary>
public sealed record StockStrategyChoice(
    Guid? CandidateRunId,
    StrategyReference Strategy,
    string Name,
    string Mode,
    DateTimeOffset? RunStartedAtUtc);

/// <summary>Original shared-engine checks, never an order authorization.</summary>
public sealed record StockStrategyAssessment(
    string Symbol,
    StrategyReference Strategy,
    Guid? CandidateRunId,
    Guid? CandidateId,
    string State,
    string Detail,
    DateTimeOffset? EvaluatedAtUtc,
    DateTimeOffset? LatestCompletedCandleAtUtc,
    bool Expired,
    IReadOnlyList<GateEvaluationResponse> Checks,
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record StockStrategyAssessmentResponse(
    string ContractVersion,
    Guid? CandidateRunId,
    StrategyReference Strategy,
    DateTimeOffset ReadAtUtc,
    IReadOnlyList<StockStrategyAssessment> Stocks);

/// <summary>A bounded current observation against one exact strategy and an existing stock source.</summary>
public sealed record StartStockStrategyAssessmentRequest(string ContractVersion, string IdempotencyKey,
    StrategyReference Strategy, string Source, IReadOnlyList<string> Symbols);

/// <summary>Durable collection status and the resulting original checks, shared by Web and Android.</summary>
public sealed record StockStrategyAssessmentJob(string ContractVersion, Guid JobId, string Status,
    string Detail, StrategyReference Strategy, string ProfileSha256, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc, DateTimeOffset? FinishedAtUtc, StockStrategyAssessmentResponse? Result);

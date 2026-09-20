namespace TradingFlow.Contracts.V1;

public sealed record CandidateAuditResponse(
    string ContractVersion,
    CandidateStateResponse Candidate,
    IReadOnlyList<CandidateTransitionResponse> Transitions,
    IReadOnlyList<GateEvaluationResponse> GateEvaluations,
    MarketEvidenceResponse? MarketEvidence,
    CatalystEvidenceResponse? CatalystEvidence,
    ExecutionEvidenceResponse? ExecutionEvidence,
    string SemanticDecisionSha256,
    DateTimeOffset GeneratedAtUtc);

public sealed record GateEvaluationResponse(
    int Order,
    string GateName,
    bool Passed,
    string? RejectionCode,
    DateTimeOffset EvaluatedAtUtc,
    IReadOnlyDictionary<string, string?> Inputs);

public sealed record MarketEvidenceResponse(
    DateTimeOffset AsOfUtc,
    DateTimeOffset LatestCompletedCandleAtUtc,
    string Timeframe,
    string Session,
    decimal? BidPrice,
    decimal? AskPrice,
    decimal? LastPrice,
    decimal? SpreadBps,
    decimal? SameTimeRelativeVolume,
    int? RelativeVolumeSampleCount,
    string EvidenceSha256);

public sealed record CatalystEvidenceResponse(
    Guid CatalystResultId,
    string Provider,
    string ProviderArticleId,
    DateTimeOffset ProviderPublishedAtUtc,
    DateTimeOffset FirstReceivedAtUtc,
    string Category,
    string Direction,
    decimal CompositeScore,
    string? Headline,
    string? Url,
    string EvidenceSha256);

public sealed record ExecutionEvidenceResponse(
    Guid? OrderIntentId,
    string? ClientOrderId,
    string? BrokerOrderId,
    string State,
    decimal? RequestedQuantity,
    decimal? FilledQuantity,
    decimal? AverageFillPrice,
    DateTimeOffset? UpdatedAtUtc,
    string? RejectionCode);

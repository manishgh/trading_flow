using System.Text.Json;

namespace TradingFlow.Contracts.V1;

/// <summary>The engine's complete bounded-per-bar evidence, retained verbatim rather than reconstructed by clients.</summary>
public sealed record RunZoneEvaluationDetail(string StrategyId, string Ticker, string? CandidateId,
    JsonElement Evidence, string? ResolutionReason, RunZoneDecisionDetail? Admission = null);

/// <summary>Explicit paging over all source rows; an empty page never implies there were no evaluations.</summary>
public sealed record RunZoneEvaluationPage(long TotalCount, long Offset, int Limit, long? NextOffset,
    IReadOnlyList<RunZoneEvaluationDetail> Rows, string? UnavailableReason = null);

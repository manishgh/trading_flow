using TradingFlow.Domain.Execution;

namespace TradingFlow.Domain.Persistence;

public sealed class GateEvaluationRecord : OperationalRecord
{
    public long EvaluationId { get; set; }
    public Guid CandidateId { get; set; }
    public string? ClientOrderId { get; set; }
    public int GateOrder { get; set; }
    public string GateName { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public RejectCode? RejectCode { get; set; }
    public DateTimeOffset EvaluatedAtUtc { get; set; }
    public string InputsJson { get; set; } = string.Empty;
}

public sealed class CandidateRecord : OperationalRecord
{
    public Guid CandidateId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public DateTimeOffset DiscoveredAtUtc { get; set; }
    public string DiscoverySource { get; set; } = string.Empty;
    public string FinvizPreset { get; set; } = string.Empty;
    public string Horizon { get; set; } = string.Empty;
    public decimal? PreviousClose { get; set; }
    public decimal? LastPrice { get; set; }
    public decimal? GapPct { get; set; }
    public decimal? GapAtr { get; set; }
    public long? PremarketVolume { get; set; }
    public decimal? PremarketDollarVolume { get; set; }
    public decimal? SameTimeRvol { get; set; }
    public decimal? SpreadBps { get; set; }
    public int? QuoteAgeMs { get; set; }
    public decimal? BenchmarkReturn { get; set; }
    public decimal? SectorReturn { get; set; }
    public decimal? MarketExcessReturn { get; set; }
    public decimal? SectorExcessReturn { get; set; }
    public Guid? CatalystResultId { get; set; }
    public decimal? MarketConfirmationScore { get; set; }
    public string SetupScoresJson { get; set; } = "{}";
    public string? SelectedStrategy { get; set; }
    public string State { get; set; } = string.Empty;
    public string RejectReasonsJson { get; set; } = "[]";
}

public sealed class CatalystResultRecord : OperationalRecord
{
    public Guid CatalystResultId { get; set; }
    public Guid? CandidateId { get; set; }
    public string ProviderArticleId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public decimal CompositeScore { get; set; }
    public string RawComponentsJson { get; set; } = "{}";
    public string PenaltiesJson { get; set; } = "[]";
    public string DedupEvidenceJson { get; set; } = "{}";
    public string Stage1ProvenanceJson { get; set; } = "{}";
    public string? Stage2ProvenanceJson { get; set; }
    public string? ModelId { get; set; }
    public string? PromptVersion { get; set; }
    public DateTimeOffset EvaluatedAtUtc { get; set; }
}

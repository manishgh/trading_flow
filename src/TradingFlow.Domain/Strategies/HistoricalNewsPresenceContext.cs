using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingFlow.Domain.News;

namespace TradingFlow.Domain.Strategies;

/// <summary>Research-only assumptions, separate from actual receipt and classified catalyst evidence.</summary>
public static class HistoricalNewsDiagnosticPolicy
{
    public const string PolicyId = "captured-provider-version-next-session-24h.v1";
    public const string AdmissionPolicyId = "historical-news-presence-research-v1";
    public const string EvidenceChecked = "evidence_checked";
    public const string AvoidRecentCompanyNews = "avoid_recent_company_news";
    public const int FreshnessHours = 120;

    // Match the existing historical resolver's exact frozen policy bytes, without changing actual clocks.
    public static string PolicySha256 { get; } = Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new {
            ContractId = PolicyId, ProviderUpdateLag = TimeSpan.FromHours(24),
            ProcessingAllowance = TimeSpan.Zero, OfficialArchiveLag = TimeSpan.FromHours(24) })));
}

/// <summary>One selected article revision; all symbol associations belong to these exact source bytes.</summary>
public sealed record HistoricalNewsPresenceRevision(string Provider, string ArticleId, string RevisionId,
    string ContentSha256, DateTimeOffset PublishedAtUtc, DateTimeOffset UpdatedAtUtc,
    DateTimeOffset ActualReceivedAtUtc, DateTimeOffset AssumedAvailableAtUtc,
    ImmutableArray<string> Symbols, ImmutableArray<string> SourceSha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CompanyNewsAssessment? Assessment = null);

/// <summary>Decision-bound diagnostic evidence. An incomplete query is unknown, never an empty-news claim.</summary>
public sealed record HistoricalNewsPresenceContext(string Symbol, DateTimeOffset DecisionAtUtc,
    string PolicyId, string PolicySha256, string EvidenceSha256, bool QueryEvidenceComplete,
    string? UnknownReason, ImmutableArray<HistoricalNewsPresenceRevision> EligibleRevisions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HistoricalCompanyNewsAssessmentPolicy? AssessmentPolicy = null);

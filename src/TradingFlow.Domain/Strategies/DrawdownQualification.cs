using System.Text.Json.Serialization;

namespace TradingFlow.Domain.Strategies;

/// <summary>Optional inclusive bounds on one frozen daily diagnostic. No default filter.</summary>
public sealed record DrawdownQualificationRules(string Mode = "observe", string? Metric = null,
    decimal? MinValue = null, decimal? MaxValue = null, bool RequireSectorContext = false,
    bool RequireEventContext = false, int MinimumMaturedEpisodes = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool RejectAdverseEvents = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EventLookbackDays = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EventMaxCoverageAgeHours = null)
{
    public const string ContractVersion = "drawdown-observation-v1";
    public static readonly IReadOnlyList<string> Metrics = Array.AsReadOnly(new[] {
        "close_drawdown_fraction", "low_drawdown_fraction", "predecline_atr_depth", "close_location",
        "move_1_atr", "move_3_atr", "true_range_atr", "volume_median_ratio",
        "stock_return_5", "sector_return_5", "market_return_5", "stock_minus_sector_return_5" });

    [JsonIgnore] public bool UsesSectorContext => RequireSectorContext || Metric is
        "stock_return_5" or "sector_return_5" or "market_return_5" or "stock_minus_sector_return_5";
    [JsonIgnore] public bool UsesEventContext => RequireEventContext || RejectAdverseEvents;

    public void Validate()
    {
        if (Mode is not ("observe" or "gate") || MinimumMaturedEpisodes < 0 ||
            EventLookbackDays is <= 0 or > 365 || EventMaxCoverageAgeHours is <= 0 or > 168 ||
            !UsesEventContext && (EventLookbackDays is not null || EventMaxCoverageAgeHours is not null) ||
            Metric is not null && !Metrics.Contains(Metric) ||
            MinValue is { } min && MaxValue is { } max && min > max ||
            (MinValue is not null || MaxValue is not null) && Metric is null ||
            Mode == "gate" && MinValue is null && MaxValue is null &&
                !RequireSectorContext && !UsesEventContext && MinimumMaturedEpisodes == 0)
            throw new InvalidOperationException("Drawdown qualification requires a known mode/metric and explicit bounded gate or evidence requirement.");
    }
}

public sealed record DrawdownMetric(decimal? Value, string? UnknownReason = null);

public sealed record DrawdownObservation(string ContractVersion, DateTimeOffset SourceTimestamp,
    bool FeatureEligible, string? EligibilityReason, bool IsDeclining, DateTimeOffset? PeakTimestamp,
    decimal? PeakHigh, decimal? LowSincePeak, int? SessionsSincePeak,
    decimal? PredeclineAtr, decimal? LaggedAtr, IReadOnlyDictionary<string, DrawdownMetric> Metrics);

public sealed record DrawdownOutcome(int HorizonSessions, DateTimeOffset AvailableAtUtc,
    decimal CloseReturnFraction, decimal FavorableExcursionFraction, decimal AdverseExcursionFraction);

public sealed record DrawdownEpisode(string EpisodeId, DateTimeOffset PeakTimestamp,
    DateTimeOffset OnsetTimestamp, decimal PeakHigh, DateTimeOffset? RecoveredAtTimestamp,
    string OutcomeStatus, IReadOnlyList<DrawdownOutcome> Outcomes);

/// <summary>Required independent evidence before observed rows can be called session-complete history.</summary>
public sealed record DrawdownHistoryContract(string SecurityId, string AdjustmentVersion,
    IReadOnlyList<DateTimeOffset> ExpectedSessionTimestamps,
    DrawdownSecurityIdentity? Identity = null, DateTimeOffset? CalendarKnownAtUtc = null,
    string? CalendarSha256 = null);

public sealed record DrawdownQualificationAudit(DrawdownObservation Observation,
    IReadOnlyList<DrawdownEpisode> Episodes, int? MaturedPriorEpisodeCount, string? HistoryUnknownReason,
    string Verdict, string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DrawdownContextAudit? Context = null);

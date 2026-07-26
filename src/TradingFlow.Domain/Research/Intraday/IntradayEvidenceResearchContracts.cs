using TradingFlow.Domain.Market;

namespace TradingFlow.Domain.Research.Intraday;

public enum IntradayEvidenceEligibility
{
    PromotionEligible = 1,
    DiagnosticOnly = 2,
    Rejected = 3
}

public enum IntradayMorphology
{
    None = 0,
    OpeningRangeContinuation = 1,
    PullbackReclaim = 2,
    FailedBreak = 3
}

public sealed record IntradayClassifierValidation(
    int LabeledStoryCount,
    int DoubleLabeledStoryCount,
    decimal CompositeQualifierPrecision,
    decimal CompositeQualifierPrecision95LowerBound,
    decimal CompositeQualifierRecall,
    decimal CategoryMacroF1,
    decimal DirectionMacroF1,
    decimal MaterialityWeightedKappa,
    IReadOnlyList<CatalystNewsCategory> FrozenPromotableCategories,
    IReadOnlyDictionary<CatalystNewsCategory, int> UntouchedExamplesByPromotableCategory)
{
    public IReadOnlyList<string> ValidationBlockers
    {
        get
        {
            var blockers = new List<string>();
            if (CompositeQualifierPrecision < 0.80m)
            {
                blockers.Add("classifier_composite_precision_below_minimum");
            }

            if (CompositeQualifierPrecision95LowerBound < 0.70m)
            {
                blockers.Add("classifier_composite_precision_lower_bound_below_minimum");
            }

            if (CompositeQualifierRecall < 0.60m)
            {
                blockers.Add("classifier_composite_recall_below_minimum");
            }

            if (CategoryMacroF1 < 0.70m)
            {
                blockers.Add("classifier_category_macro_f1_below_minimum");
            }

            if (DirectionMacroF1 < 0.75m)
            {
                blockers.Add("classifier_direction_macro_f1_below_minimum");
            }

            if (MaterialityWeightedKappa < 0.60m)
            {
                blockers.Add("classifier_materiality_weighted_kappa_below_minimum");
            }

            if (LabeledStoryCount < 500)
            {
                blockers.Add("classifier_resolved_labels_below_minimum");
            }

            if (DoubleLabeledStoryCount < 100)
            {
                blockers.Add("classifier_double_labels_below_minimum");
            }

            var categories = FrozenPromotableCategories
                ?.Distinct()
                .ToArray() ?? [];
            if (categories.Length == 0 ||
                categories.Any(category =>
                    !Enum.IsDefined(category) ||
                    category is CatalystNewsCategory.PromotionalLowInformation or
                        CatalystNewsCategory.Unknown))
            {
                blockers.Add("classifier_promotable_categories_not_frozen");
            }
            else if (categories.Any(category =>
                         UntouchedExamplesByPromotableCategory is null ||
                         !UntouchedExamplesByPromotableCategory.TryGetValue(
                             category,
                             out var count) ||
                         count < 30))
            {
                blockers.Add("classifier_promotable_class_untouched_count_below_minimum");
            }

            return blockers;
        }
    }

    public bool MeetsTrackBMinimum => ValidationBlockers.Count == 0;
}

public sealed record IntradayB0EvidenceReadiness(
    bool SipOneMinuteBarsVerified,
    bool SipNbboQuotesVerified,
    bool OpeningAuctionStatusVerified,
    bool HaltResumeAndLuldStatusVerified,
    bool CorporateActionsVerified,
    bool PointInTimeSectorMembershipVerified,
    bool GlobalStoryClustersVerified)
{
    public IReadOnlyList<string> AdmissionBlockers
    {
        get
        {
            var blockers = new List<string>();
            AddIfMissing(SipOneMinuteBarsVerified, "sip_one_minute_bars_not_verified");
            AddIfMissing(SipNbboQuotesVerified, "sip_nbbo_quotes_not_verified");
            AddIfMissing(OpeningAuctionStatusVerified, "opening_auction_status_not_verified");
            AddIfMissing(
                HaltResumeAndLuldStatusVerified,
                "halt_resume_luld_status_not_verified");
            AddIfMissing(CorporateActionsVerified, "corporate_actions_not_verified");
            AddIfMissing(
                PointInTimeSectorMembershipVerified,
                "point_in_time_sector_membership_not_verified");
            AddIfMissing(GlobalStoryClustersVerified, "global_story_clusters_not_verified");
            return blockers;

            void AddIfMissing(bool verified, string reason)
            {
                if (!verified)
                {
                    blockers.Add(reason);
                }
            }
        }
    }
}

public sealed record IntradayQuoteEvidence(
    string Ticker,
    DateTimeOffset TimestampUtc,
    decimal Bid,
    decimal Ask,
    string Feed);

public sealed record PointInTimeSectorEvidence(
    string Ticker,
    string SectorCode,
    string BenchmarkTicker,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset EffectiveToUtc,
    DateTimeOffset ObservedAtUtc);

public sealed record IntradayEvidenceStudyOptions
{
    public IReadOnlyList<TimeSpan> Horizons { get; init; } =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15),
         TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60)];

    public IReadOnlyList<CatalystStudyPartitionDefinition> Partitions { get; init; } =
        Array.Empty<CatalystStudyPartitionDefinition>();

    public int PriorSessionTarget { get; init; } = 63;
    public int PriorSessionMinimum { get; init; } = 40;
    public TimeSpan OpeningRange { get; init; } = TimeSpan.FromMinutes(15);
    public decimal HolmFamilyWiseAlpha { get; init; } = 0.05m;
    public bool AllowProviderTimestampDiagnosticMode { get; init; }
}

public sealed record CatalystStudyPartitionDefinition(
    string Label,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);

public sealed record IntradayEvidenceStudyRequest(
    IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> BarsByTicker,
    IReadOnlyDictionary<string, IReadOnlyList<CatalystEvent>> CatalystsByTicker,
    IReadOnlyDictionary<string, IReadOnlyList<IntradayQuoteEvidence>> QuotesByTicker,
    IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> BenchmarkBarsByTicker,
    IReadOnlyList<PointInTimeSectorEvidence> SectorMembership,
    IReadOnlyList<ClassifiedCatalystEvidenceRow> Classifications,
    IntradayClassifierValidation ClassifierValidation,
    IntradayB0EvidenceReadiness B0Evidence,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string CandleTimeframe,
    IntradayEvidenceStudyOptions Options);

public sealed record IntradayEvidenceStudyReport(
    DateTimeOffset GeneratedAtUtc,
    IntradayEvidenceEligibility Eligibility,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<IntradayEventEvidenceObservation> Observations,
    IReadOnlyList<IntradayHolmPValue> HolmReadyPValues);

public sealed record IntradayEventEvidenceObservation(
    string Ticker,
    string GlobalStoryCluster,
    string NewsRevisionId,
    string Partition,
    string EventSession,
    string ResponseSession,
    DateTimeOffset PublishedAtUtc,
    DateTimeOffset? ObservedReceiptAtUtc,
    DateTimeOffset ClassifierCompletedAtUtc,
    DateTimeOffset AvailableAtUtc,
    DateTimeOffset ResponseAnchorCompletedAtUtc,
    CatalystNewsCategory Category,
    CatalystDirection Direction,
    CatalystMateriality Materiality,
    IntradayEvidenceEligibility Eligibility,
    string? CensorReason,
    decimal? CumulativeRegularSessionRvol,
    int ComparableSessionCount,
    decimal? RobustLogVolumeZScore,
    decimal? PremarketDollarVolume,
    decimal? SpreadBps,
    decimal? GapPct,
    IntradayMorphology Morphology,
    IReadOnlyList<IntradayMorphologyEvidence> MorphologyTimeline,
    IReadOnlyList<IntradayHorizonEvidence> Horizons);

public sealed record IntradayMorphologyEvidence(
    DateTimeOffset CompletedAtUtc,
    IntradayMorphology State,
    decimal Close,
    decimal? SessionVwap);

public sealed record IntradayHorizonEvidence(
    string Horizon,
    DateTimeOffset? TargetCompletedAtUtc,
    decimal? RawSignedReturnPct,
    decimal? SpyAbnormalReturnPct,
    decimal? SectorAbnormalReturnPct,
    decimal? ExecutableReturnPct,
    decimal? MfePct,
    decimal? MaePct,
    IntradayMorphology MorphologyAtTarget,
    bool IsCensored,
    string? CensorReason);

public sealed record IntradayHolmPValue(
    string Family,
    string Hypothesis,
    int SampleCount,
    decimal RawPValue,
    decimal AdjustedPValue,
    bool RejectedAtFamilyWiseAlpha,
    int HolmRank,
    int FamilySize);

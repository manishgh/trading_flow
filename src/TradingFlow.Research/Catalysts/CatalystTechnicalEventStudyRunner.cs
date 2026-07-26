using TradingFlow.Domain.Market;
using TradingFlow.Domain.Research.Intraday;
using TradingFlow.Engine.Indicators;
using TradingFlow.Engine.Market;
using System.Security.Cryptography;
using System.Text;

namespace TradingFlow.Research.Catalysts;

public sealed record CatalystEventStudyOptions
{
    public IReadOnlyList<TimeSpan> Horizons { get; init; } = new[]
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromMinutes(60)
    };

    public TimeSpan DeduplicationWindow { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan NoveltyLookback { get; init; } = TimeSpan.FromDays(7);
    public decimal PositiveSentimentThreshold { get; init; } = 0.15m;
    public decimal NegativeSentimentThreshold { get; init; } = -0.15m;
    public IReadOnlyList<CatalystStudyPartition> StudyPartitions { get; init; } =
        Array.Empty<CatalystStudyPartition>();

    /// <summary>
    /// Frozen report timestamp used for byte-identical evidence replay. When omitted,
    /// the study end is used because it is already part of the immutable study definition.
    /// </summary>
    public DateTimeOffset? ReportGeneratedAtUtc { get; init; }
}

public sealed record CatalystStudyPartition
{
    public CatalystStudyPartition(
        string label,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        EnsureUtc(startUtc, nameof(startUtc));
        EnsureUtc(endUtc, nameof(endUtc));
        if (endUtc <= startUtc)
        {
            throw new ArgumentException("Study partition end must follow its start.");
        }

        Label = label.Trim().ToLowerInvariant();
        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    public string Label { get; }
    public DateTimeOffset StartUtc { get; }
    public DateTimeOffset EndUtc { get; }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be a non-default UTC value.", parameterName);
        }
    }
}

public sealed record CatalystEventStudyReport(
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string CandleTimeframe,
    IReadOnlyList<string> Tickers,
    IReadOnlyList<CatalystStudyPartition> StudyPartitions,
    IReadOnlyList<CatalystEventStudyObservation> Observations,
    IReadOnlyList<CatalystEventStudyBucket> Buckets);

public sealed record CatalystEventStudyObservation(
    string Ticker,
    string StoryId,
    string RevisionId,
    string StudyPartition,
    string MarketSession,
    DateTimeOffset EventTimestampUtc,
    DateTimeOffset ProviderPublishedTimestampUtc,
    DateTimeOffset? ProviderUpdatedTimestampUtc,
    DateTimeOffset? ReceivedTimestampUtc,
    DateTimeOffset AvailableTimestampUtc,
    string AvailabilityEvidence,
    string ReturnEvidence,
    DateTimeOffset AnchorTimestampUtc,
    decimal AnchorBarOffsetMinutes,
    DateTimeOffset? FirstConfirmableTimestampUtc,
    decimal? FirstConfirmableDelayMinutes,
    decimal? PreNewsReturn15mPct,
    decimal? PreNewsReturn60mPct,
    decimal? PostNewsReturn15mPct,
    decimal? PostNewsReturn60mPct,
    bool? Ema10AboveEma20BeforeNews,
    bool? Ema10AboveEma20AfterNews,
    bool Ema10CrossedAboveEma20AfterNews,
    decimal? MacdHistogramBeforeNews,
    decimal? MacdHistogramAfterNews,
    bool MacdHistogramTurnedBullishAfterNews,
    bool VolumeExpandedAfterNews,
    string EventCategory,
    CatalystType CatalystType,
    string SentimentBand,
    decimal SentimentScore,
    decimal NoveltyScore,
    string NoveltyBand,
    string TechnicalRegime,
    string Headline,
    string? Provider,
    string? Source,
    string? Url,
    decimal AnchorClose,
    decimal? Vwap,
    decimal? Rsi,
    decimal? Atr,
    decimal? Ema10,
    decimal? Ema20,
    decimal? MacdHistogram,
    decimal? SlotRelativeVolume,
    decimal? SessionRelativeVolume,
    decimal? CumulativeRelativeVolume,
    IReadOnlyList<CatalystForwardReturn> ForwardReturns);

public sealed record CatalystForwardReturn(
    string Horizon,
    DateTimeOffset? TargetTimestampUtc,
    decimal? ReturnPct);

public sealed record CatalystEventStudyBucket(
    string StudyPartition,
    string MarketSession,
    string AvailabilityEvidence,
    string EventCategory,
    string SentimentBand,
    string NoveltyBand,
    string TechnicalRegime,
    int ObservationCount,
    int IndependentStoryCount,
    IReadOnlyList<CatalystBucketReturn> Returns);

public sealed record CatalystBucketReturn(
    string Horizon,
    int SampleCount,
    decimal AverageReturnPct,
    decimal MedianReturnPct,
    decimal WinRatePct);

public sealed class CatalystEventClassifier
{
    public string Classify(CatalystEvent catalyst)
    {
        var text = $"{catalyst.Headline} {catalyst.Summary}".ToLowerInvariant();
        if (ContainsAny(text, "earnings", "eps", "revenue", "guidance", "quarter", "q1", "q2", "q3", "q4"))
        {
            return "earnings_or_guidance";
        }

        if (ContainsAny(text, "fda", "clinical", "trial", "phase 1", "phase 2", "phase 3", "approval", "drug"))
        {
            return "biotech_regulatory";
        }

        if (ContainsAny(text, "contract", "award", "order", "partnership", "customer", "launch", "supply"))
        {
            return "commercial_catalyst";
        }

        if (ContainsAny(text, "upgrade", "price target raised", "initiates buy", "outperform"))
        {
            return "analyst_positive";
        }

        if (ContainsAny(text, "downgrade", "price target cut", "underperform", "sell rating"))
        {
            return "analyst_negative";
        }

        if (ContainsAny(text, "merger", "acquisition", "takeover", "buyout", "acquire"))
        {
            return "m_and_a";
        }

        if (ContainsAny(text, "offering", "shelf", "dilution", "registered direct", "atm"))
        {
            return "financing_or_dilution";
        }

        return catalyst.Type switch
        {
            CatalystType.EarningsRelease => "earnings_or_guidance",
            CatalystType.AnalystUpgrade => "analyst_positive",
            CatalystType.AnalystDowngrade => "analyst_negative",
            CatalystType.MergerAnnouncement => "m_and_a",
            CatalystType.ProductLaunch => "commercial_catalyst",
            CatalystType.RegulatoryFiling => "regulatory_filing",
            _ => "general_news"
        };
    }

    private static bool ContainsAny(string text, params string[] needles) => needles.Any(text.Contains);
}

public sealed class CatalystDeduper
{
    public IReadOnlyList<CatalystEvent> Deduplicate(IReadOnlyList<CatalystEvent> catalysts, TimeSpan duplicateWindow)
    {
        var output = new List<CatalystEvent>();
        var ordered = catalysts
            .Select(catalyst => new
            {
                Catalyst = catalyst,
                Availability = CatalystAvailability.Resolve(catalyst)
            })
            .OrderBy(item => item.Availability.AvailableAtUtc)
            .ThenBy(item => item.Availability.RevisionId, StringComparer.Ordinal)
            .ToArray();
        foreach (var item in ordered)
        {
            var catalyst = item.Catalyst;
            var contentKey = BuildContentKey(catalyst);
            var storyId = BuildStoryIdentity(catalyst);
            var duplicate = output.Any(existing =>
                existing.Ticker.Equals(catalyst.Ticker, StringComparison.OrdinalIgnoreCase) &&
                BuildContentKey(existing).Equals(contentKey, StringComparison.Ordinal) &&
                IsDuplicateRevision(
                    existing,
                    catalyst,
                    storyId,
                    item.Availability.AvailableAtUtc,
                    duplicateWindow));
            if (!duplicate)
            {
                output.Add(catalyst);
            }
        }

        return output;
    }

    private static bool IsDuplicateRevision(
        CatalystEvent existing,
        CatalystEvent current,
        string currentStoryId,
        DateTimeOffset currentAvailableAtUtc,
        TimeSpan duplicateWindow)
    {
        var existingAvailability = CatalystAvailability.Resolve(existing);
        var sameStory = BuildStoryIdentity(existing).Equals(currentStoryId, StringComparison.Ordinal);
        if (sameStory && HasStableStoryIdentity(existing) && HasStableStoryIdentity(current))
        {
            return existingAvailability.AvailableAtUtc == currentAvailableAtUtc;
        }

        var elapsed = currentAvailableAtUtc - existingAvailability.AvailableAtUtc;
        return elapsed >= TimeSpan.Zero && elapsed <= duplicateWindow;
    }

    private static bool HasStableStoryIdentity(CatalystEvent catalyst) =>
        !String.IsNullOrWhiteSpace(catalyst.ExternalId) ||
        !String.IsNullOrWhiteSpace(catalyst.Url);

    private static string BuildStoryIdentity(CatalystEvent catalyst)
    {
        var provider = catalyst.Provider?.Trim().ToLowerInvariant() ?? "unknown";
        if (!String.IsNullOrWhiteSpace(catalyst.ExternalId))
        {
            return $"{provider}:id:{catalyst.ExternalId.Trim().ToLowerInvariant()}";
        }

        return !String.IsNullOrWhiteSpace(catalyst.Url)
            ? $"{provider}:url:{catalyst.Url.Trim().ToLowerInvariant()}"
            : $"{provider}:headline:{NormalizeText(catalyst.Headline)}";
    }

    private static string BuildContentKey(CatalystEvent catalyst) =>
        $"{NormalizeText(catalyst.Headline)}|{NormalizeText(catalyst.Summary ?? String.Empty)}";

    internal static string NormalizeText(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

public sealed class CatalystNoveltyScorer
{
    public decimal Score(CatalystEvent catalyst, IReadOnlyList<CatalystEvent> priorCatalysts, TimeSpan lookback)
    {
        var currentTokens = Tokenize(catalyst.Headline);
        if (currentTokens.Count == 0)
        {
            return 0m;
        }

        var currentAvailability = CatalystAvailability.Resolve(catalyst).AvailableAtUtc;
        var recent = priorCatalysts
            .Where(x => x.Ticker.Equals(catalyst.Ticker, StringComparison.OrdinalIgnoreCase))
            .Select(x => new
            {
                Catalyst = x,
                AvailableAtUtc = CatalystAvailability.Resolve(x).AvailableAtUtc
            })
            .Where(x =>
                x.AvailableAtUtc < currentAvailability &&
                currentAvailability - x.AvailableAtUtc <= lookback)
            .ToArray();
        if (recent.Length == 0)
        {
            return 1m;
        }

        var maxSimilarity = recent.Max(x => Jaccard(currentTokens, Tokenize(x.Catalyst.Headline)));
        return Decimal.Round(Math.Clamp(1m - maxSimilarity, 0m, 1m), 4);
    }

    private static HashSet<string> Tokenize(string text) => CatalystDeduper.NormalizeText(text)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(x => x.Length > 2)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static decimal Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0m;
        }

        var intersection = left.Count(right.Contains);
        var union = left.Count + right.Count - intersection;
        return union == 0 ? 0m : (decimal)intersection / union;
    }
}

public sealed record CatalystAvailability(
    DateTimeOffset AvailableAtUtc,
    string Evidence,
    string StoryId,
    string RevisionId)
{
    public static CatalystAvailability Resolve(CatalystEvent catalyst)
    {
        ArgumentNullException.ThrowIfNull(catalyst);
        var availableAt = catalyst.Timestamp;
        var evidence = CatalystAvailabilityEvidence.ProviderTimestampOnly;
        if (catalyst.DecisionAvailableAt is not null)
        {
            if (catalyst.DecisionAvailableAt.Value.Offset != TimeSpan.Zero ||
                catalyst.DecisionAvailableAt.Value < catalyst.Timestamp)
            {
                throw new InvalidDataException(
                    "Catalyst decision availability must be UTC and cannot precede publication.");
            }

            availableAt = catalyst.DecisionAvailableAt.Value;
            evidence = String.IsNullOrWhiteSpace(catalyst.DecisionAvailabilityEvidence)
                ? CatalystAvailabilityEvidence.NewsAndAssessmentObservedTime
                : catalyst.DecisionAvailabilityEvidence.Trim();
        }
        else if (String.Equals(
                catalyst.AvailabilityEvidence,
                CatalystAvailabilityEvidence.ObservedReceiptTime,
                StringComparison.Ordinal) &&
            catalyst.ReceivedAt is not null)
        {
            availableAt = catalyst.ReceivedAt.Value;
            evidence = CatalystAvailabilityEvidence.ObservedReceiptTime;
        }
        else if (catalyst.UpdatedAt is not null &&
                 catalyst.UpdatedAt.Value > catalyst.Timestamp)
        {
            availableAt = catalyst.UpdatedAt.Value;
            evidence = CatalystAvailabilityEvidence.ProviderUpdatedTimestampOnly;
        }

        var storyId = BuildStoryId(catalyst);
        var revisionMaterial = String.Join(
            "|",
            storyId,
            availableAt.ToUniversalTime().ToString("O"),
            CatalystDeduper.NormalizeText(catalyst.Headline),
            CatalystDeduper.NormalizeText(catalyst.Summary ?? String.Empty));
        var revisionHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(revisionMaterial)))
            .ToLowerInvariant();
        return new CatalystAvailability(
            availableAt.ToUniversalTime(),
            evidence,
            storyId,
            $"sha256:{revisionHash}");
    }

    private static string BuildStoryId(CatalystEvent catalyst)
    {
        var provider = catalyst.Provider?.Trim().ToLowerInvariant() ?? "unknown";
        if (!String.IsNullOrWhiteSpace(catalyst.ExternalId))
        {
            return $"{provider}:id:{catalyst.ExternalId.Trim().ToLowerInvariant()}";
        }

        var identity = !String.IsNullOrWhiteSpace(catalyst.Url)
            ? catalyst.Url.Trim().ToLowerInvariant()
            : CatalystDeduper.NormalizeText(catalyst.Headline);
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return $"{provider}:sha256:{hash}";
    }
}

public sealed class CatalystTechnicalEventStudyRunner
{
    private const string ReturnEvidence =
        "pre_event_completed_close_to_horizon_completed_close_proxy";

    private readonly IExchangeSessionResolver? _sessionResolver;
    private readonly IndicatorEngine _indicatorEngine = new();
    private readonly CatalystEventClassifier _classifier = new();
    private readonly CatalystDeduper _deduper = new();
    private readonly CatalystNoveltyScorer _noveltyScorer = new();

    public CatalystTechnicalEventStudyRunner(
        IExchangeSessionResolver? sessionResolver = null)
    {
        _sessionResolver = sessionResolver;
    }

    /// <summary>
    /// Runs the evidence-grade Track B path. Unlike <see cref="Analyze"/>, this path
    /// requires supplied point-in-time classifier, quote, sector, and benchmark evidence
    /// and never falls back to keyword classification for promotion eligibility.
    /// </summary>
    public IntradayEvidenceStudyReport AnalyzeIntradayEvidence(
        IntradayEvidenceStudyRequest request)
    {
        var sessionResolver = _sessionResolver ??
            throw new InvalidOperationException(
                "Intraday evidence research requires an explicit exchange calendar.");
        return new IntradayParticipationResearchAnalyzer(sessionResolver).Analyze(request);
    }

    public CatalystEventStudyReport Analyze(
        IReadOnlyDictionary<string, IReadOnlyList<OhlcvBar>> barsByTicker,
        IReadOnlyDictionary<string, IReadOnlyList<CatalystEvent>> catalystsByTicker,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string candleTimeframe,
        CatalystEventStudyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(barsByTicker);
        ArgumentNullException.ThrowIfNull(catalystsByTicker);
        ArgumentException.ThrowIfNullOrWhiteSpace(candleTimeframe);

        options ??= new CatalystEventStudyOptions();
        var sessionResolver = _sessionResolver ??
            throw new InvalidOperationException(
                "Catalyst research requires an explicit point-in-time exchange calendar resolver.");
        var partitions = ValidateStudyPartitions(options.StudyPartitions, startUtc, endUtc);
        var barDuration = TimeframeParser.Parse(candleTimeframe);
        var observations = new List<CatalystEventStudyObservation>();
        var tickers = barsByTicker.Keys
            .Union(catalystsByTicker.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToArray();

        foreach (var ticker in tickers)
        {
            var bars = barsByTicker.TryGetValue(ticker, out var tickerBars)
                ? tickerBars.OrderBy(x => x.Timestamp).ToArray()
                : Array.Empty<OhlcvBar>();
            if (bars.Length == 0)
            {
                continue;
            }

            var snapshots = _indicatorEngine.Compute(bars);
            var rawCatalysts = catalystsByTicker.TryGetValue(ticker, out var tickerCatalysts)
                ? tickerCatalysts
                    .OrderBy(x => CatalystAvailability.Resolve(x).AvailableAtUtc)
                    .ToArray()
                : Array.Empty<CatalystEvent>();
            var deduped = _deduper.Deduplicate(rawCatalysts, options.DeduplicationWindow);
            var prior = new List<CatalystEvent>();
            foreach (var catalyst in deduped)
            {
                var availability = CatalystAvailability.Resolve(catalyst);
                var availableAt = availability.AvailableAtUtc;
                if (availableAt < startUtc)
                {
                    prior.Add(catalyst);
                    continue;
                }

                if (availableAt >= endUtc)
                {
                    break;
                }

                var anchorIndex = FindLastCompletedBarIndex(bars, availableAt, barDuration);
                if (anchorIndex < 0)
                {
                    prior.Add(catalyst);
                    continue;
                }

                var anchorBar = bars[anchorIndex];
                var snapshot = snapshots.ElementAtOrDefault(anchorIndex);
                var noveltyScore = _noveltyScorer.Score(catalyst, prior, options.NoveltyLookback);
                var partition = ResolveStudyPartition(partitions, availableAt);
                observations.Add(BuildObservation(
                    catalyst,
                    availability,
                    partition,
                    sessionResolver,
                    barDuration,
                    anchorBar,
                    snapshot,
                    bars,
                    snapshots,
                    anchorIndex,
                    options,
                    noveltyScore,
                    endUtc));
                prior.Add(catalyst);
            }
        }

        var reportGeneratedAtUtc = options.ReportGeneratedAtUtc ?? endUtc;
        if (reportGeneratedAtUtc == default ||
            reportGeneratedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The catalyst report timestamp must be a non-default UTC value.",
                nameof(options));
        }

        return new CatalystEventStudyReport(
            reportGeneratedAtUtc,
            startUtc,
            endUtc,
            candleTimeframe,
            tickers,
            partitions,
            observations.OrderBy(x => x.AvailableTimestampUtc).ToArray(),
            BuildBuckets(observations));
    }

    private CatalystEventStudyObservation BuildObservation(
        CatalystEvent catalyst,
        CatalystAvailability availability,
        CatalystStudyPartition partition,
        IExchangeSessionResolver sessionResolver,
        TimeSpan barDuration,
        OhlcvBar anchorBar,
        IndicatorSnapshot? snapshot,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int anchorIndex,
        CatalystEventStudyOptions options,
        decimal noveltyScore,
        DateTimeOffset studyEndUtc)
    {
        var availableAt = availability.AvailableAtUtc;
        var sentimentBand = catalyst.SentimentScore >= options.PositiveSentimentThreshold
            ? "positive"
            : catalyst.SentimentScore <= options.NegativeSentimentThreshold
                ? "negative"
                : "neutral";
        var noveltyBand = noveltyScore >= 0.75m ? "new" : noveltyScore >= 0.35m ? "related" : "duplicate_like";
        var technicalRegime = LabelTechnicalRegime(anchorBar, snapshot);
        var firstConfirmableIndex = FindFirstCompletedBarAfter(
            bars,
            availableAt,
            anchorIndex,
            barDuration);
        var firstConfirmableBar = firstConfirmableIndex >= 0 ? bars[firstConfirmableIndex] : null;
        var firstConfirmableSnapshot = firstConfirmableIndex >= 0 ? snapshots.ElementAtOrDefault(firstConfirmableIndex) : null;
        var anchorCompletedAt = anchorBar.Timestamp + barDuration;
        var firstConfirmableCompletedAt = firstConfirmableBar?.Timestamp + barDuration;
        var anchorOffsetMinutes = Decimal.Round((decimal)(anchorCompletedAt - availableAt).TotalMinutes, 4);
        decimal? firstConfirmableDelayMinutes = firstConfirmableBar is null
            ? null
            : Decimal.Round((decimal)(firstConfirmableCompletedAt!.Value - availableAt).TotalMinutes, 4);
        var ema10Above20Before = Ema10AboveEma20(snapshot);
        var ema10Above20After = Ema10AboveEma20(firstConfirmableSnapshot);
        var macdBefore = snapshot?.MacdHistogram;
        var macdAfter = firstConfirmableSnapshot?.MacdHistogram;

        return new CatalystEventStudyObservation(
            catalyst.Ticker.ToUpperInvariant(),
            availability.StoryId,
            availability.RevisionId,
            partition.Label,
            sessionResolver.Resolve(availableAt).Label,
            catalyst.Timestamp.ToUniversalTime(),
            catalyst.Timestamp.ToUniversalTime(),
            catalyst.UpdatedAt?.ToUniversalTime(),
            catalyst.ReceivedAt?.ToUniversalTime(),
            availableAt.ToUniversalTime(),
            availability.Evidence,
            ReturnEvidence,
            anchorCompletedAt.ToUniversalTime(),
            anchorOffsetMinutes,
            firstConfirmableCompletedAt?.ToUniversalTime(),
            firstConfirmableDelayMinutes,
            BuildTrailingReturn(bars, anchorIndex, TimeSpan.FromMinutes(15), barDuration),
            BuildTrailingReturn(bars, anchorIndex, TimeSpan.FromMinutes(60), barDuration),
            BuildForwardReturnPercent(
                bars,
                anchorIndex,
                availableAt,
                TimeSpan.FromMinutes(15),
                barDuration,
                studyEndUtc,
                sessionResolver),
            BuildForwardReturnPercent(
                bars,
                anchorIndex,
                availableAt,
                TimeSpan.FromMinutes(60),
                barDuration,
                studyEndUtc,
                sessionResolver),
            ema10Above20Before,
            ema10Above20After,
            ema10Above20Before == false && ema10Above20After == true,
            macdBefore,
            macdAfter,
            macdBefore is < 0m && macdAfter is >= 0m,
            IsVolumeExpanded(snapshot, firstConfirmableSnapshot),
            _classifier.Classify(catalyst),
            catalyst.Type,
            sentimentBand,
            catalyst.SentimentScore,
            noveltyScore,
            noveltyBand,
            technicalRegime,
            catalyst.Headline,
            catalyst.Provider,
            catalyst.Source,
            catalyst.Url,
            anchorBar.Close,
            snapshot?.Vwap,
            snapshot?.Rsi,
            snapshot?.Atr,
            snapshot?.Ema10,
            snapshot?.Ema20,
            snapshot?.MacdHistogram,
            snapshot?.SlotRelativeVolume,
            snapshot?.SessionRelativeVolume,
            snapshot?.RelativeVolume,
            options.Horizons.Select(horizon =>
                BuildForwardReturn(
                    bars,
                    anchorIndex,
                    availableAt,
                    horizon,
                    barDuration,
                    studyEndUtc,
                    sessionResolver)).ToArray());
    }

    private static IReadOnlyList<CatalystEventStudyBucket> BuildBuckets(IReadOnlyList<CatalystEventStudyObservation> observations)
    {
        return observations
            .GroupBy(x => new
            {
                x.StudyPartition,
                x.MarketSession,
                x.AvailabilityEvidence,
                x.EventCategory,
                x.SentimentBand,
                x.NoveltyBand,
                x.TechnicalRegime
            })
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key.StudyPartition)
            .ThenBy(x => x.Key.MarketSession)
            .ThenBy(x => x.Key.AvailabilityEvidence)
            .ThenBy(x => x.Key.EventCategory)
            .Select(group => new CatalystEventStudyBucket(
                group.Key.StudyPartition,
                group.Key.MarketSession,
                group.Key.AvailabilityEvidence,
                group.Key.EventCategory,
                group.Key.SentimentBand,
                group.Key.NoveltyBand,
                group.Key.TechnicalRegime,
                group.Count(),
                group.Select(item => item.StoryId).Distinct(StringComparer.Ordinal).Count(),
                BuildBucketReturns(group.ToArray())))
            .ToArray();
    }

    private static IReadOnlyList<CatalystBucketReturn> BuildBucketReturns(IReadOnlyList<CatalystEventStudyObservation> observations)
    {
        return observations
            .SelectMany(x => x.ForwardReturns)
            .GroupBy(x => x.Horizon)
            .OrderBy(x => ParseHorizonSortKey(x.Key))
            .Select(group =>
            {
                var values = group.Where(x => x.ReturnPct is not null).Select(x => x.ReturnPct!.Value).OrderBy(x => x).ToArray();
                if (values.Length == 0)
                {
                    return new CatalystBucketReturn(group.Key, 0, 0m, 0m, 0m);
                }

                var median = values.Length % 2 == 1
                    ? values[values.Length / 2]
                    : (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2m;
                return new CatalystBucketReturn(
                    group.Key,
                    values.Length,
                    Decimal.Round(values.Average(), 4),
                    Decimal.Round(median, 4),
                    Decimal.Round(values.Count(x => x > 0m) / (decimal)values.Length * 100m, 2));
            })
            .ToArray();
    }

    private static CatalystForwardReturn BuildForwardReturn(
        IReadOnlyList<OhlcvBar> bars,
        int anchorIndex,
        DateTimeOffset availableAt,
        TimeSpan horizon,
        TimeSpan barDuration,
        DateTimeOffset studyEndUtc,
        IExchangeSessionResolver sessionResolver)
    {
        var targetIndex = FindForwardTargetIndex(
            bars,
            anchorIndex,
            availableAt,
            horizon,
            barDuration,
            studyEndUtc,
            sessionResolver);
        if (targetIndex < 0)
        {
            return new CatalystForwardReturn(FormatHorizon(horizon), null, null);
        }

        return new CatalystForwardReturn(
            FormatHorizon(horizon),
            (bars[targetIndex].Timestamp + barDuration).ToUniversalTime(),
            PercentChange(bars[anchorIndex].Close, bars[targetIndex].Close));
    }

    private static decimal? BuildForwardReturnPercent(
        IReadOnlyList<OhlcvBar> bars,
        int anchorIndex,
        DateTimeOffset availableAt,
        TimeSpan horizon,
        TimeSpan barDuration,
        DateTimeOffset studyEndUtc,
        IExchangeSessionResolver sessionResolver)
    {
        var targetIndex = FindForwardTargetIndex(
            bars,
            anchorIndex,
            availableAt,
            horizon,
            barDuration,
            studyEndUtc,
            sessionResolver);
        return targetIndex < 0 ? null : PercentChange(bars[anchorIndex].Close, bars[targetIndex].Close);
    }

    private static int FindForwardTargetIndex(
        IReadOnlyList<OhlcvBar> bars,
        int anchorIndex,
        DateTimeOffset availableAt,
        TimeSpan horizon,
        TimeSpan barDuration,
        DateTimeOffset studyEndUtc,
        IExchangeSessionResolver sessionResolver)
    {
        var targetTime = availableAt + horizon;
        if (targetTime > studyEndUtc)
        {
            return -1;
        }

        var eventSession = sessionResolver.Resolve(availableAt);
        for (var i = anchorIndex + 1; i < bars.Count; i++)
        {
            var completedAt = bars[i].Timestamp + barDuration;
            if (completedAt > studyEndUtc)
            {
                return -1;
            }

            var completedSession = sessionResolver.Resolve(completedAt);
            if (completedSession.TradeDate != eventSession.TradeDate ||
                completedSession.Session != eventSession.Session)
            {
                return -1;
            }

            if (completedAt >= targetTime)
            {
                return i;
            }
        }

        return -1;
    }

    private static decimal? BuildTrailingReturn(
        IReadOnlyList<OhlcvBar> bars,
        int anchorIndex,
        TimeSpan lookback,
        TimeSpan barDuration)
    {
        var startTime = bars[anchorIndex].Timestamp + barDuration - lookback;
        var startIndex = FindLastCompletedBarIndex(bars, startTime, barDuration);
        return startIndex < 0 ? null : PercentChange(bars[startIndex].Close, bars[anchorIndex].Close);
    }

    private static int FindFirstCompletedBarAfter(
        IReadOnlyList<OhlcvBar> bars,
        DateTimeOffset availableAt,
        int anchorIndex,
        TimeSpan barDuration)
    {
        for (var i = Math.Max(anchorIndex + 1, 0); i < bars.Count; i++)
        {
            if (bars[i].Timestamp + barDuration > availableAt)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool? Ema10AboveEma20(IndicatorSnapshot? snapshot)
    {
        return snapshot?.Ema10 is null || snapshot.Ema20 is null
            ? null
            : snapshot.Ema10 >= snapshot.Ema20;
    }

    private static bool IsVolumeExpanded(IndicatorSnapshot? before, IndicatorSnapshot? after)
    {
        if (after is null)
        {
            return false;
        }

        if (after.SessionRelativeVolume is >= 1m || after.SlotRelativeVolume is >= 1m || after.RelativeVolume is >= 1m)
        {
            return true;
        }

        return before is not null && after.CurrentVolume > before.CurrentVolume;
    }

    private static int FindLastCompletedBarIndex(
        IReadOnlyList<OhlcvBar> bars,
        DateTimeOffset availableAt,
        TimeSpan barDuration)
    {
        var anchorIndex = -1;
        for (var i = 0; i < bars.Count; i++)
        {
            if (bars[i].Timestamp + barDuration <= availableAt)
            {
                anchorIndex = i;
                continue;
            }

            break;
        }

        return anchorIndex;
    }

    private static IReadOnlyList<CatalystStudyPartition> ValidateStudyPartitions(
        IReadOnlyList<CatalystStudyPartition> partitions,
        DateTimeOffset studyStartUtc,
        DateTimeOffset studyEndUtc)
    {
        if (studyStartUtc == default ||
            studyEndUtc == default ||
            studyStartUtc.Offset != TimeSpan.Zero ||
            studyEndUtc.Offset != TimeSpan.Zero ||
            studyEndUtc <= studyStartUtc)
        {
            throw new ArgumentException(
                "Study bounds must be valid UTC timestamps with end after start.");
        }

        var ordered = (partitions ?? throw new ArgumentNullException(nameof(partitions)))
            .OrderBy(partition => partition.StartUtc)
            .ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException(
                "Catalyst research requires caller-supplied chronological study partitions.",
                nameof(partitions));
        }

        if (ordered.Select(partition => partition.Label)
            .Distinct(StringComparer.Ordinal)
            .Count() != ordered.Length)
        {
            throw new ArgumentException(
                "Study partition labels must be unique.",
                nameof(partitions));
        }

        if (ordered[0].StartUtc != studyStartUtc ||
            ordered[^1].EndUtc != studyEndUtc)
        {
            throw new ArgumentException(
                "Study partitions must cover the declared study bounds exactly.",
                nameof(partitions));
        }

        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].EndUtc != ordered[index].StartUtc)
            {
                throw new ArgumentException(
                    "Study partitions must be chronological, contiguous, and non-overlapping.",
                    nameof(partitions));
            }
        }

        return ordered;
    }

    private static CatalystStudyPartition ResolveStudyPartition(
        IReadOnlyList<CatalystStudyPartition> partitions,
        DateTimeOffset availableAtUtc)
    {
        return partitions.Single(partition =>
            availableAtUtc >= partition.StartUtc &&
            availableAtUtc < partition.EndUtc);
    }

    private static string LabelTechnicalRegime(OhlcvBar bar, IndicatorSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return "unknown";
        }

        var aboveVwap = snapshot.Vwap is not null && bar.Close >= snapshot.Vwap;
        var emaBullish = snapshot.Ema10 is not null && snapshot.Ema20 is not null && snapshot.Ema10 >= snapshot.Ema20;
        var macdBullish = snapshot.MacdHistogram is not null && snapshot.MacdHistogram >= 0m;
        var belowVwap = snapshot.Vwap is not null && bar.Close < snapshot.Vwap;
        var emaBearish = snapshot.Ema10 is not null && snapshot.Ema20 is not null && snapshot.Ema10 < snapshot.Ema20;
        var macdBearish = snapshot.MacdHistogram is not null && snapshot.MacdHistogram < 0m;

        if (aboveVwap && emaBullish && macdBullish)
        {
            return "bullish_confirmed";
        }

        if (belowVwap && emaBearish && macdBearish)
        {
            return "bearish_confirmed";
        }

        if (aboveVwap || emaBullish || macdBullish)
        {
            return "mixed_bullish";
        }

        if (belowVwap || emaBearish || macdBearish)
        {
            return "mixed_bearish";
        }

        return "mixed";
    }

    private static decimal PercentChange(decimal start, decimal end)
    {
        return start == 0m ? 0m : Decimal.Round(((end / start) - 1m) * 100m, 4);
    }

    private static string FormatHorizon(TimeSpan horizon)
    {
        if (horizon.TotalDays >= 1 && horizon.TotalDays % 1 == 0)
        {
            return $"{(int)horizon.TotalDays}d";
        }

        if (horizon.TotalHours >= 1 && horizon.TotalHours % 1 == 0)
        {
            return $"{(int)horizon.TotalHours}h";
        }

        return $"{(int)horizon.TotalMinutes}m";
    }

    private static int ParseHorizonSortKey(string horizon)
    {
        if (horizon.EndsWith("d", StringComparison.OrdinalIgnoreCase) && int.TryParse(horizon[..^1], out var days))
        {
            return days * 24 * 60;
        }

        if (horizon.EndsWith("h", StringComparison.OrdinalIgnoreCase) && int.TryParse(horizon[..^1], out var hours))
        {
            return hours * 60;
        }

        return horizon.EndsWith("m", StringComparison.OrdinalIgnoreCase) && int.TryParse(horizon[..^1], out var minutes)
            ? minutes
            : int.MaxValue;
    }
}

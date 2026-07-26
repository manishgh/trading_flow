using TradingFlow.Domain.Research;

namespace TradingFlow.Research.Catalysts;

public sealed record ProviderUpdatedDailyNewsStudyOptions
{
    public ProviderUpdatedDailyNewsStudyOptions(
        string benchmarkSymbol,
        IReadOnlyList<int> horizonSessions,
        decimal roundTripCostBps,
        int minimumQuietPeriodHours = 24)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkSymbol);
        var horizons = (horizonSessions ?? throw new ArgumentNullException(nameof(horizonSessions)))
            .Distinct()
            .Order()
            .ToArray();
        if (horizons.Length == 0 || horizons.Any(value => value < 0))
        {
            throw new ArgumentException(
                "At least one non-negative trading-session horizon is required.",
                nameof(horizonSessions));
        }

        if (roundTripCostBps < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(roundTripCostBps));
        }

        if (minimumQuietPeriodHours <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumQuietPeriodHours));
        }

        BenchmarkSymbol = benchmarkSymbol.Trim().ToUpperInvariant();
        HorizonSessions = horizons;
        RoundTripCostBps = roundTripCostBps;
        MinimumQuietPeriodHours = minimumQuietPeriodHours;
    }

    public string BenchmarkSymbol { get; }
    public IReadOnlyList<int> HorizonSessions { get; }
    public decimal RoundTripCostBps { get; }
    public int MinimumQuietPeriodHours { get; }
}

public sealed record DailyNewsStoryCluster(
    string StoryId,
    string Provider,
    string ProviderArticleId,
    IReadOnlyList<string> RevisionIds,
    IReadOnlyList<DateTimeOffset> RevisionAvailableAtUtc,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> Categories,
    DateTimeOffset FirstProviderCreatedAtUtc,
    DateTimeOffset FirstProviderUpdatedAtUtc,
    DateTimeOffset LastProviderUpdatedAtUtc,
    string Headline,
    string ArticleUrl);

public sealed record DailyNewsEventObservation(
    string EventId,
    string StoryId,
    string Provider,
    string ProviderArticleId,
    string SourceRevisionId,
    string Symbol,
    string Headline,
    string ArticleUrl,
    DateTimeOffset ProviderCreatedAtUtc,
    DateTimeOffset EventAvailableAtUtc,
    string TimingBucket,
    DateOnly ResponseTradeDate,
    DateOnly EligibleEntryTradeDate,
    string StudySegment,
    IReadOnlyList<string> Categories,
    bool IsIndependentEpisodeStart,
    decimal? PriorNewsGapHours);

public sealed record DailyNewsEventHorizonReturn(
    string EventId,
    string StoryId,
    string Symbol,
    string StudySegment,
    string TimingBucket,
    DateOnly EntryTradeDate,
    DateOnly ExitTradeDate,
    int HorizonSessions,
    decimal? EntryOpen,
    decimal? ExitClose,
    decimal? GrossReturnPct,
    decimal? NetReturnPct,
    decimal? BenchmarkReturnPct,
    decimal? NetExcessReturnPct,
    bool CensoredByLaterNews,
    DateTimeOffset? CensoringNewsAtUtc,
    string? ExclusionReason)
{
    public bool IsUsable => ExclusionReason is null;
    public bool IsClean => IsUsable && !CensoredByLaterNews;
}

public sealed record DailyNewsOutcomeStatistics(
    int UsableCount,
    int CleanCount,
    decimal UsableMeanNetReturnPct,
    decimal UsableMedianNetReturnPct,
    decimal UsablePositiveRatePct,
    decimal UsableMeanNetExcessReturnPct,
    decimal CleanMeanNetReturnPct,
    decimal CleanMedianNetReturnPct,
    decimal CleanPositiveRatePct,
    decimal CleanMeanNetExcessReturnPct);

public sealed record DailyNewsEventStudySummary(
    string StudySegment,
    string TimingBucket,
    int HorizonSessions,
    DailyNewsOutcomeStatistics Outcomes);

public sealed record DailyNewsCategoryStudySummary(
    string StudySegment,
    string Category,
    string TimingBucket,
    int HorizonSessions,
    DailyNewsOutcomeStatistics Outcomes);

public sealed record ProviderUpdatedDailyNewsStudyReport(
    ProviderUpdatedDailyNewsStudyOptions Options,
    IReadOnlyList<DailyNewsStoryCluster> StoryClusters,
    IReadOnlyList<DailyNewsEventObservation> EventObservations,
    IReadOnlyList<DailyNewsEventHorizonReturn> HorizonReturns,
    IReadOnlyList<DailyNewsEventStudySummary> Summaries,
    IReadOnlyList<DailyNewsCategoryStudySummary> CategorySummaries,
    IReadOnlyDictionary<string, int> Exclusions,
    bool PromotionEligible,
    IReadOnlyList<string> PromotionBlockers);

/// <summary>
/// Conservative daily event study for historical Alpaca REST news. Provider updated-at is
/// the earliest allowed event clock; provider created-at is metadata only. Entry is always
/// at an exchange open that follows information availability.
/// </summary>
public sealed class ProviderUpdatedDailyNewsStudy
{
    private const string AvailabilityBlocker = "historical_news_first_seen_unproven";
    private const string RevisionBlocker = "historical_news_revision_history_unproven";
    private const string UniverseBlocker = "static_universe_survivorship_selection_bias";

    public ProviderUpdatedDailyNewsStudyReport Analyze(
        IReadOnlyCollection<NewsRevisionEvidenceRow> news,
        IReadOnlyCollection<MarketBarEvidenceRow> asTradedDailyBars,
        IReadOnlyCollection<ExchangeSessionEvidenceRow> sessions,
        ProviderUpdatedDailyNewsStudyOptions options,
        EvidenceStudyPartitions partitions)
    {
        ArgumentNullException.ThrowIfNull(news);
        ArgumentNullException.ThrowIfNull(asTradedDailyBars);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(partitions);
        if (news.Any(row =>
                row.AvailabilityEvidence !=
                NewsAvailabilityEvidence.ProviderUpdatedTimestampOnly))
        {
            throw new ArgumentException(
                "Historical daily news studies require provider updated-at availability.",
                nameof(news));
        }

        var orderedSessions = sessions
            .Where(row => row.Exchange == "XNYS")
            .OrderBy(row => row.TradeDate)
            .ToArray();
        if (orderedSessions.Length == 0)
        {
            throw new ArgumentException("XNYS exchange-session evidence is required.", nameof(sessions));
        }

        EnsureUniqueSessions(orderedSessions);
        var sessionsByDate = orderedSessions.ToDictionary(row => row.TradeDate);
        var sessionIndex = orderedSessions
            .Select((row, index) => (row.TradeDate, index))
            .ToDictionary(pair => pair.TradeDate, pair => pair.index);
        var bars = BuildBarIndex(asTradedDailyBars);
        var clusters = BuildClusters(news);
        var events = BuildEvents(
            clusters,
            orderedSessions,
            options.BenchmarkSymbol,
            partitions,
            TimeSpan.FromHours(options.MinimumQuietPeriodHours));
        var laterNews = BuildLaterNewsIndex(clusters, options.BenchmarkSymbol);
        var returns = new List<DailyNewsEventHorizonReturn>(
            events.Count * options.HorizonSessions.Count);
        foreach (var observation in events)
        {
            foreach (var horizon in options.HorizonSessions)
            {
                returns.Add(CalculateReturn(
                    observation,
                    horizon,
                    options,
                    orderedSessions,
                    sessionsByDate,
                    sessionIndex,
                    bars,
                    laterNews));
            }
        }

        var orderedReturns = returns
            .OrderBy(row => row.EntryTradeDate)
            .ThenBy(row => row.Symbol, StringComparer.Ordinal)
            .ThenBy(row => row.EventId, StringComparer.Ordinal)
            .ThenBy(row => row.HorizonSessions)
            .ToArray();
        var independentEventIds = events
            .Where(row => row.IsIndependentEpisodeStart)
            .Select(row => row.EventId)
            .ToHashSet(StringComparer.Ordinal);
        var independentReturns = orderedReturns
            .Where(row => independentEventIds.Contains(row.EventId))
            .ToArray();
        var summaries = BuildSummaries(independentReturns);
        var categorySummaries = BuildCategorySummaries(
            independentReturns,
            events);
        var exclusions = orderedReturns
            .Where(row => !row.IsUsable)
            .GroupBy(row => row.ExclusionReason!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new ProviderUpdatedDailyNewsStudyReport(
            options,
            clusters,
            events,
            orderedReturns,
            summaries,
            categorySummaries,
            exclusions,
            PromotionEligible: false,
            [AvailabilityBlocker, RevisionBlocker, UniverseBlocker]);
    }

    private static IReadOnlyList<DailyNewsStoryCluster> BuildClusters(
        IReadOnlyCollection<NewsRevisionEvidenceRow> news)
    {
        var clusters = new List<DailyNewsStoryCluster>();
        foreach (var story in news.GroupBy(
                     row => $"{row.Provider}|{row.ProviderArticleId}",
                     StringComparer.Ordinal))
        {
            var revisions = story
                .GroupBy(row => row.RevisionId, StringComparer.Ordinal)
                .Select(group =>
                {
                    var first = group.First();
                    if (group.Any(row =>
                            row.Provider != first.Provider ||
                            row.ProviderArticleId != first.ProviderArticleId ||
                            row.Headline != first.Headline ||
                            row.Summary != first.Summary ||
                            row.ArticleUrl != first.ArticleUrl ||
                            row.ProviderCreatedAtUtc != first.ProviderCreatedAtUtc ||
                            row.ProviderUpdatedAtUtc != first.ProviderUpdatedAtUtc))
                    {
                        throw new InvalidDataException(
                            $"News revision '{group.Key}' has conflicting normalized content.");
                    }

                    return first;
                })
                .OrderBy(row => row.ProviderUpdatedAtUtc)
                .ThenBy(row => row.RevisionId, StringComparer.Ordinal)
                .ToArray();
            var firstRevision = revisions[0];
            clusters.Add(new DailyNewsStoryCluster(
                EvidenceCanonicalJson.ComputeSha256(new
                {
                    firstRevision.Provider,
                    firstRevision.ProviderArticleId
                }),
                firstRevision.Provider,
                firstRevision.ProviderArticleId,
                revisions.Select(row => row.RevisionId).ToArray(),
                revisions.Select(row => row.ProviderUpdatedAtUtc).ToArray(),
                story.SelectMany(row => row.Symbols)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                story.SelectMany(row => row.Categories)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .DefaultIfEmpty("uncategorized")
                    .ToArray(),
                revisions.Min(row => row.ProviderCreatedAtUtc),
                revisions.Min(row => row.ProviderUpdatedAtUtc),
                revisions.Max(row => row.ProviderUpdatedAtUtc),
                firstRevision.Headline,
                firstRevision.ArticleUrl));
        }

        return clusters
            .OrderBy(cluster => cluster.FirstProviderUpdatedAtUtc)
            .ThenBy(cluster => cluster.StoryId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<DailyNewsEventObservation> BuildEvents(
        IReadOnlyList<DailyNewsStoryCluster> clusters,
        IReadOnlyList<ExchangeSessionEvidenceRow> sessions,
        string benchmarkSymbol,
        EvidenceStudyPartitions partitions,
        TimeSpan minimumQuietPeriod)
    {
        var events = new List<DailyNewsEventObservation>();
        foreach (var cluster in clusters)
        {
            var mapping = ResolveSessionMapping(cluster.FirstProviderUpdatedAtUtc, sessions);
            if (mapping is null)
            {
                continue;
            }

            var segment = ResolveSegment(mapping.Value.Entry.RegularOpenUtc, partitions);
            if (segment is null)
            {
                continue;
            }

            foreach (var symbol in cluster.Symbols.Where(value =>
                         !value.Equals(benchmarkSymbol, StringComparison.Ordinal)))
            {
                var eventId = EvidenceCanonicalJson.ComputeSha256(new
                {
                    cluster.StoryId,
                    Symbol = symbol,
                    EventAvailableAtUtc = cluster.FirstProviderUpdatedAtUtc
                });
                events.Add(new DailyNewsEventObservation(
                    eventId,
                    cluster.StoryId,
                    cluster.Provider,
                    cluster.ProviderArticleId,
                    cluster.RevisionIds[0],
                    symbol,
                    cluster.Headline,
                    cluster.ArticleUrl,
                    cluster.FirstProviderCreatedAtUtc,
                    cluster.FirstProviderUpdatedAtUtc,
                    mapping.Value.Timing,
                    mapping.Value.Response.TradeDate,
                    mapping.Value.Entry.TradeDate,
                    segment,
                    cluster.Categories,
                    false,
                    null));
            }
        }

        var ordered = events
            .OrderBy(row => row.EventAvailableAtUtc)
            .ThenBy(row => row.Symbol, StringComparer.Ordinal)
            .ThenBy(row => row.EventId, StringComparer.Ordinal)
            .ToArray();
        var previousBySymbol = new Dictionary<string, DateTimeOffset>(
            StringComparer.Ordinal);
        return ordered.Select(row =>
        {
            var hasPrior = previousBySymbol.TryGetValue(row.Symbol, out var prior);
            var gap = hasPrior ? row.EventAvailableAtUtc - prior : (TimeSpan?)null;
            previousBySymbol[row.Symbol] = row.EventAvailableAtUtc;
            return row with
            {
                IsIndependentEpisodeStart =
                    gap is null || gap.Value >= minimumQuietPeriod,
                PriorNewsGapHours =
                    gap is null ? null : (decimal)gap.Value.TotalHours
            };
        }).ToArray();
    }

    private static DailyNewsEventHorizonReturn CalculateReturn(
        DailyNewsEventObservation observation,
        int horizon,
        ProviderUpdatedDailyNewsStudyOptions options,
        IReadOnlyList<ExchangeSessionEvidenceRow> orderedSessions,
        IReadOnlyDictionary<DateOnly, ExchangeSessionEvidenceRow> sessionsByDate,
        IReadOnlyDictionary<DateOnly, int> sessionIndex,
        IReadOnlyDictionary<(string Symbol, DateOnly TradeDate), MarketBarEvidenceRow> bars,
        IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> laterNews)
    {
        if (!sessionIndex.TryGetValue(observation.EligibleEntryTradeDate, out var entryIndex))
        {
            return Excluded(observation, horizon, "entry_session_missing");
        }

        var exitIndex = entryIndex + horizon;
        if (exitIndex >= orderedSessions.Count)
        {
            return Excluded(observation, horizon, "exit_session_outside_calendar");
        }

        var exitDate = orderedSessions[exitIndex].TradeDate;
        if (!bars.TryGetValue((observation.Symbol, observation.EligibleEntryTradeDate), out var entry) ||
            !bars.TryGetValue((observation.Symbol, exitDate), out var exit))
        {
            return Excluded(
                observation,
                horizon,
                "issuer_bar_missing",
                exitDate);
        }

        if (!bars.TryGetValue(
                (options.BenchmarkSymbol, observation.EligibleEntryTradeDate),
                out var benchmarkEntry) ||
            !bars.TryGetValue((options.BenchmarkSymbol, exitDate), out var benchmarkExit))
        {
            return Excluded(
                observation,
                horizon,
                "benchmark_bar_missing",
                exitDate);
        }

        var entryOpen = EvidenceFixedDecimal.FromPriceUnits(entry.OpenPriceUnits);
        var exitClose = EvidenceFixedDecimal.FromPriceUnits(exit.ClosePriceUnits);
        var benchmarkOpen = EvidenceFixedDecimal.FromPriceUnits(benchmarkEntry.OpenPriceUnits);
        var benchmarkClose = EvidenceFixedDecimal.FromPriceUnits(benchmarkExit.ClosePriceUnits);
        if (entryOpen <= 0m || benchmarkOpen <= 0m)
        {
            return Excluded(observation, horizon, "non_positive_entry_price", exitDate);
        }

        var gross = PercentReturn(entryOpen, exitClose);
        var net = gross - options.RoundTripCostBps / 100m;
        var benchmark = PercentReturn(benchmarkOpen, benchmarkClose);
        var exitCloseUtc = sessionsByDate[exitDate].RegularCloseUtc;
        var censorAt = laterNews.TryGetValue(observation.Symbol, out var timestamps)
            ? FindFirstAfter(timestamps, observation.EventAvailableAtUtc)
            : default;
        if (censorAt > exitCloseUtc)
        {
            censorAt = default;
        }

        return new DailyNewsEventHorizonReturn(
            observation.EventId,
            observation.StoryId,
            observation.Symbol,
            observation.StudySegment,
            observation.TimingBucket,
            observation.EligibleEntryTradeDate,
            exitDate,
            horizon,
            entryOpen,
            exitClose,
            gross,
            net,
            benchmark,
            net - benchmark,
            censorAt != default,
            censorAt == default ? null : censorAt,
            null);
    }

    private static IReadOnlyList<DailyNewsEventStudySummary> BuildSummaries(
        IReadOnlyCollection<DailyNewsEventHorizonReturn> returns) =>
        returns
            .Where(row => row.IsUsable)
            .GroupBy(row => new
            {
                row.StudySegment,
                row.TimingBucket,
                row.HorizonSessions
            })
            .OrderBy(group => group.Key.StudySegment, StringComparer.Ordinal)
            .ThenBy(group => group.Key.TimingBucket, StringComparer.Ordinal)
            .ThenBy(group => group.Key.HorizonSessions)
            .Select(group => new DailyNewsEventStudySummary(
                group.Key.StudySegment,
                group.Key.TimingBucket,
                group.Key.HorizonSessions,
                Statistics(group)))
            .ToArray();

    private static IReadOnlyList<DailyNewsCategoryStudySummary> BuildCategorySummaries(
        IReadOnlyCollection<DailyNewsEventHorizonReturn> returns,
        IReadOnlyCollection<DailyNewsEventObservation> events)
    {
        var categoriesByEvent = events.ToDictionary(
            row => row.EventId,
            row => row.Categories,
            StringComparer.Ordinal);
        return returns
            .Where(row => row.IsUsable)
            .SelectMany(row => categoriesByEvent[row.EventId].Select(category => new
            {
                Category = category,
                Return = row
            }))
            .GroupBy(value => new
            {
                value.Return.StudySegment,
                value.Category,
                value.Return.TimingBucket,
                value.Return.HorizonSessions
            })
            .OrderBy(group => group.Key.StudySegment, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Category, StringComparer.Ordinal)
            .ThenBy(group => group.Key.TimingBucket, StringComparer.Ordinal)
            .ThenBy(group => group.Key.HorizonSessions)
            .Select(group => new DailyNewsCategoryStudySummary(
                group.Key.StudySegment,
                group.Key.Category,
                group.Key.TimingBucket,
                group.Key.HorizonSessions,
                Statistics(group.Select(value => value.Return))))
            .ToArray();
    }

    private static DailyNewsOutcomeStatistics Statistics(
        IEnumerable<DailyNewsEventHorizonReturn> source)
    {
        var usable = source.Where(row => row.IsUsable).ToArray();
        var clean = usable.Where(row => row.IsClean).ToArray();
        var usableReturns = usable
            .Select(row => row.NetReturnPct!.Value)
            .Order()
            .ToArray();
        var cleanReturns = clean
            .Select(row => row.NetReturnPct!.Value)
            .Order()
            .ToArray();
        return new DailyNewsOutcomeStatistics(
            usable.Length,
            clean.Length,
            Mean(usableReturns),
            Median(usableReturns),
            PositiveRate(usableReturns),
            Mean(usable.Select(row => row.NetExcessReturnPct!.Value).ToArray()),
            Mean(cleanReturns),
            Median(cleanReturns),
            PositiveRate(cleanReturns),
            Mean(clean.Select(row => row.NetExcessReturnPct!.Value).ToArray()));
    }

    private static decimal PositiveRate(IReadOnlyCollection<decimal> values) =>
        values.Count == 0
            ? 0m
            : values.Count(value => value > 0m) * 100m / values.Count;

    private static IReadOnlyDictionary<(string Symbol, DateOnly TradeDate), MarketBarEvidenceRow>
        BuildBarIndex(IReadOnlyCollection<MarketBarEvidenceRow> rows)
    {
        var daily = rows.Where(row =>
                row.Timeframe == "1d" &&
                row.Adjustment == "raw")
            .ToArray();
        if (daily.Length == 0)
        {
            throw new ArgumentException(
                "As-traded raw daily bars are required.",
                nameof(rows));
        }

        var groups = daily.GroupBy(row => (
            row.Symbol,
            DateOnly.FromDateTime(row.BarStartUtc.UtcDateTime)));
        var duplicates = groups.FirstOrDefault(group => group.Count() != 1);
        if (duplicates is not null)
        {
            throw new InvalidDataException(
                $"Daily bar '{duplicates.Key.Symbol}:{duplicates.Key.Item2:yyyy-MM-dd}' is duplicated.");
        }

        return groups.ToDictionary(group => group.Key, group => group.Single());
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> BuildLaterNewsIndex(
        IReadOnlyList<DailyNewsStoryCluster> clusters,
        string benchmarkSymbol)
    {
        var values = new Dictionary<string, IReadOnlyList<DateTimeOffset>>(StringComparer.Ordinal);
        foreach (var symbolGroup in clusters
                     .SelectMany(cluster => cluster.Symbols.Select(symbol => new
                     {
                         Symbol = symbol,
                         cluster.RevisionAvailableAtUtc
                     }))
                     .Where(value => value.Symbol != benchmarkSymbol)
                     .GroupBy(value => value.Symbol, StringComparer.Ordinal))
        {
            values[symbolGroup.Key] = symbolGroup
                .SelectMany(value => value.RevisionAvailableAtUtc)
                .Distinct()
                .Order()
                .ToArray();
        }

        return values;
    }

    private static (
        ExchangeSessionEvidenceRow Response,
        ExchangeSessionEvidenceRow Entry,
        string Timing)? ResolveSessionMapping(
        DateTimeOffset availableAtUtc,
        IReadOnlyList<ExchangeSessionEvidenceRow> sessions)
    {
        var newYork = ResolveNewYorkTimeZone();
        var localDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(availableAtUtc, newYork).DateTime);
        var sameIndex = FindSessionIndex(sessions, localDate);
        var insertionIndex = sameIndex >= 0 ? sameIndex : ~sameIndex;
        var sameDate = sameIndex >= 0 ? sessions[sameIndex] : null;
        var nextIndex = sameIndex >= 0 ? sameIndex + 1 : insertionIndex;
        var next = nextIndex < sessions.Count ? sessions[nextIndex] : null;
        if (sameDate is null)
        {
            return next is null ? null : (next, next, "closed");
        }

        var timing = availableAtUtc < sameDate.PremarketOpenUtc
            ? "overnight"
            : availableAtUtc < sameDate.RegularOpenUtc
                ? "premarket"
                : availableAtUtc < sameDate.RegularCloseUtc
                    ? "regular"
                    : availableAtUtc < sameDate.PostmarketCloseUtc
                        ? "postmarket"
                        : "overnight";
        var response = availableAtUtc < sameDate.RegularCloseUtc
            ? sameDate
            : next;
        var entry = availableAtUtc < sameDate.RegularOpenUtc
            ? sameDate
            : next;
        return response is null || entry is null
            ? null
            : (response, entry, timing);
    }

    private static int FindSessionIndex(
        IReadOnlyList<ExchangeSessionEvidenceRow> sessions,
        DateOnly tradeDate)
    {
        var low = 0;
        var high = sessions.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var comparison = sessions[middle].TradeDate.CompareTo(tradeDate);
            if (comparison == 0)
            {
                return middle;
            }

            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return ~low;
    }

    private static DateTimeOffset FindFirstAfter(
        IReadOnlyList<DateTimeOffset> timestamps,
        DateTimeOffset threshold)
    {
        var low = 0;
        var high = timestamps.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (timestamps[middle] <= threshold)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low < timestamps.Count ? timestamps[low] : default;
    }

    private static string? ResolveSegment(
        DateTimeOffset entryOpenUtc,
        EvidenceStudyPartitions partitions)
    {
        foreach (var window in new[]
                 {
                     partitions.Development,
                     partitions.Validation,
                     partitions.Holdout
                 })
        {
            if (entryOpenUtc >= window.StartUtc && entryOpenUtc < window.EndUtc)
            {
                return window.Name;
            }
        }

        return null;
    }

    private static void EnsureUniqueSessions(
        IReadOnlyCollection<ExchangeSessionEvidenceRow> sessions)
    {
        var duplicate = sessions.GroupBy(row => row.TradeDate)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Exchange session '{duplicate.Key:yyyy-MM-dd}' is duplicated.");
        }
    }

    private static DailyNewsEventHorizonReturn Excluded(
        DailyNewsEventObservation observation,
        int horizon,
        string reason,
        DateOnly? exitDate = null) =>
        new(
            observation.EventId,
            observation.StoryId,
            observation.Symbol,
            observation.StudySegment,
            observation.TimingBucket,
            observation.EligibleEntryTradeDate,
            exitDate ?? observation.EligibleEntryTradeDate,
            horizon,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            reason);

    private static decimal PercentReturn(decimal start, decimal end) =>
        (end / start - 1m) * 100m;

    private static decimal Mean(IReadOnlyCollection<decimal> values) =>
        values.Count == 0 ? 0m : values.Average();

    private static decimal Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0)
        {
            return 0m;
        }

        var middle = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2m
            : values[middle];
    }

    private static TimeZoneInfo ResolveNewYorkTimeZone()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        throw new InvalidOperationException(
            "A New York exchange timezone definition is required.");
    }
}

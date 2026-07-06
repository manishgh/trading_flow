using TradingFlow.Domain.Market;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Backtesting.Research;

public sealed record CatalystEventStudyOptions
{
    public IReadOnlyList<TimeSpan> Horizons { get; init; } = new[]
    {
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(4),
        TimeSpan.FromDays(1),
        TimeSpan.FromDays(3),
        TimeSpan.FromDays(5)
    };

    public TimeSpan DeduplicationWindow { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan NoveltyLookback { get; init; } = TimeSpan.FromDays(7);
    public decimal PositiveSentimentThreshold { get; init; } = 0.15m;
    public decimal NegativeSentimentThreshold { get; init; } = -0.15m;
}

public sealed record CatalystEventStudyReport(
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string CandleTimeframe,
    IReadOnlyList<string> Tickers,
    IReadOnlyList<CatalystEventStudyObservation> Observations,
    IReadOnlyList<CatalystEventStudyBucket> Buckets);

public sealed record CatalystEventStudyObservation(
    string Ticker,
    DateTimeOffset EventTimestampUtc,
    DateTimeOffset ProviderPublishedTimestampUtc,
    DateTimeOffset? ReceivedTimestampUtc,
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
    string EventCategory,
    string SentimentBand,
    string NoveltyBand,
    string TechnicalRegime,
    int ObservationCount,
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
        var ordered = catalysts.OrderBy(x => x.Timestamp).ToArray();
        foreach (var catalyst in ordered)
        {
            var key = BuildKey(catalyst);
            var duplicate = output.Any(existing =>
                existing.Ticker.Equals(catalyst.Ticker, StringComparison.OrdinalIgnoreCase) &&
                BuildKey(existing).Equals(key, StringComparison.OrdinalIgnoreCase) &&
                catalyst.Timestamp - existing.Timestamp <= duplicateWindow);
            if (!duplicate)
            {
                output.Add(catalyst);
            }
        }

        return output;
    }

    private static string BuildKey(CatalystEvent catalyst)
    {
        if (!string.IsNullOrWhiteSpace(catalyst.Url))
        {
            return catalyst.Url.Trim().ToLowerInvariant();
        }

        return NormalizeText(catalyst.Headline);
    }

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

        var recent = priorCatalysts
            .Where(x => x.Ticker.Equals(catalyst.Ticker, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Timestamp < catalyst.Timestamp && catalyst.Timestamp - x.Timestamp <= lookback)
            .ToArray();
        if (recent.Length == 0)
        {
            return 1m;
        }

        var maxSimilarity = recent.Max(x => Jaccard(currentTokens, Tokenize(x.Headline)));
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

public sealed class CatalystTechnicalEventStudyRunner
{
    private readonly IndicatorEngine _indicatorEngine = new();
    private readonly CatalystEventClassifier _classifier = new();
    private readonly CatalystDeduper _deduper = new();
    private readonly CatalystNoveltyScorer _noveltyScorer = new();

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
                ? tickerCatalysts.OrderBy(x => x.Timestamp).ToArray()
                : Array.Empty<CatalystEvent>();
            var deduped = _deduper.Deduplicate(rawCatalysts, options.DeduplicationWindow);
            var prior = new List<CatalystEvent>();
            foreach (var catalyst in deduped)
            {
                var anchorIndex = FindAnchorIndex(bars, catalyst.Timestamp);
                if (anchorIndex < 0)
                {
                    prior.Add(catalyst);
                    continue;
                }

                var anchorBar = bars[anchorIndex];
                var snapshot = snapshots.ElementAtOrDefault(anchorIndex);
                var noveltyScore = _noveltyScorer.Score(catalyst, prior, options.NoveltyLookback);
                observations.Add(BuildObservation(catalyst, anchorBar, snapshot, bars, snapshots, anchorIndex, options, noveltyScore));
                prior.Add(catalyst);
            }
        }

        return new CatalystEventStudyReport(
            DateTimeOffset.UtcNow,
            startUtc,
            endUtc,
            candleTimeframe,
            tickers,
            observations.OrderBy(x => x.EventTimestampUtc).ToArray(),
            BuildBuckets(observations));
    }

    private CatalystEventStudyObservation BuildObservation(
        CatalystEvent catalyst,
        OhlcvBar anchorBar,
        IndicatorSnapshot? snapshot,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<IndicatorSnapshot> snapshots,
        int anchorIndex,
        CatalystEventStudyOptions options,
        decimal noveltyScore)
    {
        var sentimentBand = catalyst.SentimentScore >= options.PositiveSentimentThreshold
            ? "positive"
            : catalyst.SentimentScore <= options.NegativeSentimentThreshold
                ? "negative"
                : "neutral";
        var noveltyBand = noveltyScore >= 0.75m ? "new" : noveltyScore >= 0.35m ? "related" : "duplicate_like";
        var technicalRegime = LabelTechnicalRegime(anchorBar, snapshot);
        var firstConfirmableIndex = FindFirstIndexAfter(bars, catalyst.Timestamp, anchorIndex);
        var firstConfirmableBar = firstConfirmableIndex >= 0 ? bars[firstConfirmableIndex] : null;
        var firstConfirmableSnapshot = firstConfirmableIndex >= 0 ? snapshots.ElementAtOrDefault(firstConfirmableIndex) : null;
        var anchorOffsetMinutes = Decimal.Round((decimal)(anchorBar.Timestamp - catalyst.Timestamp).TotalMinutes, 4);
        decimal? firstConfirmableDelayMinutes = firstConfirmableBar is null
            ? null
            : Decimal.Round((decimal)(firstConfirmableBar.Timestamp - catalyst.Timestamp).TotalMinutes, 4);
        var ema10Above20Before = Ema10AboveEma20(snapshot);
        var ema10Above20After = Ema10AboveEma20(firstConfirmableSnapshot);
        var macdBefore = snapshot?.MacdHistogram;
        var macdAfter = firstConfirmableSnapshot?.MacdHistogram;

        return new CatalystEventStudyObservation(
            catalyst.Ticker.ToUpperInvariant(),
            catalyst.Timestamp.ToUniversalTime(),
            catalyst.Timestamp.ToUniversalTime(),
            catalyst.ReceivedAt?.ToUniversalTime(),
            anchorBar.Timestamp.ToUniversalTime(),
            anchorOffsetMinutes,
            firstConfirmableBar?.Timestamp.ToUniversalTime(),
            firstConfirmableDelayMinutes,
            BuildTrailingReturn(bars, anchorIndex, TimeSpan.FromMinutes(15)),
            BuildTrailingReturn(bars, anchorIndex, TimeSpan.FromMinutes(60)),
            BuildForwardReturnPercent(bars, anchorIndex, TimeSpan.FromMinutes(15)),
            BuildForwardReturnPercent(bars, anchorIndex, TimeSpan.FromMinutes(60)),
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
            options.Horizons.Select(horizon => BuildForwardReturn(bars, anchorIndex, horizon)).ToArray());
    }

    private static IReadOnlyList<CatalystEventStudyBucket> BuildBuckets(IReadOnlyList<CatalystEventStudyObservation> observations)
    {
        return observations
            .GroupBy(x => new { x.EventCategory, x.SentimentBand, x.NoveltyBand, x.TechnicalRegime })
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key.EventCategory)
            .Select(group => new CatalystEventStudyBucket(
                group.Key.EventCategory,
                group.Key.SentimentBand,
                group.Key.NoveltyBand,
                group.Key.TechnicalRegime,
                group.Count(),
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

    private static CatalystForwardReturn BuildForwardReturn(IReadOnlyList<OhlcvBar> bars, int anchorIndex, TimeSpan horizon)
    {
        var targetIndex = FindForwardTargetIndex(bars, anchorIndex, horizon);
        if (targetIndex < 0)
        {
            return new CatalystForwardReturn(FormatHorizon(horizon), null, null);
        }

        return new CatalystForwardReturn(
            FormatHorizon(horizon),
            bars[targetIndex].Timestamp.ToUniversalTime(),
            PercentChange(bars[anchorIndex].Close, bars[targetIndex].Close));
    }

    private static decimal? BuildForwardReturnPercent(IReadOnlyList<OhlcvBar> bars, int anchorIndex, TimeSpan horizon)
    {
        var targetIndex = FindForwardTargetIndex(bars, anchorIndex, horizon);
        return targetIndex < 0 ? null : PercentChange(bars[anchorIndex].Close, bars[targetIndex].Close);
    }

    private static int FindForwardTargetIndex(IReadOnlyList<OhlcvBar> bars, int anchorIndex, TimeSpan horizon)
    {
        var targetTime = bars[anchorIndex].Timestamp + horizon;
        for (var i = anchorIndex + 1; i < bars.Count; i++)
        {
            if (bars[i].Timestamp >= targetTime)
            {
                return i;
            }
        }

        return -1;
    }

    private static decimal? BuildTrailingReturn(IReadOnlyList<OhlcvBar> bars, int anchorIndex, TimeSpan lookback)
    {
        var startTime = bars[anchorIndex].Timestamp - lookback;
        var startIndex = FindAnchorIndex(bars, startTime);
        return startIndex < 0 ? null : PercentChange(bars[startIndex].Close, bars[anchorIndex].Close);
    }

    private static int FindFirstIndexAfter(IReadOnlyList<OhlcvBar> bars, DateTimeOffset timestamp, int anchorIndex)
    {
        for (var i = Math.Max(anchorIndex + 1, 0); i < bars.Count; i++)
        {
            if (bars[i].Timestamp > timestamp)
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

    private static int FindAnchorIndex(IReadOnlyList<OhlcvBar> bars, DateTimeOffset timestamp)
    {
        var anchorIndex = -1;
        for (var i = 0; i < bars.Count; i++)
        {
            if (bars[i].Timestamp <= timestamp)
            {
                anchorIndex = i;
                continue;
            }

            break;
        }

        return anchorIndex;
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

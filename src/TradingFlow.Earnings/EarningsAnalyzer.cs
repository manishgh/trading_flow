using TradingFlow.Domain.Earnings;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Engine.Indicators;

namespace TradingFlow.Earnings;

/// <summary>
/// Produces an advisory earnings assessment from provider results, related news, and completed bars.
/// No order-routing decision is made here.
/// </summary>
public sealed class EarningsAnalyzer
{
    // "results" is deliberately broad: the company's own release is titled
    // "<Company> Announces Second Quarter <Year> Results", which matched none of the narrower
    // terms, so the first article carrying the true publication time was being discarded. The
    // ticker/company match and the preview exclusion below keep unrelated articles out.
    private static readonly string[] ResultTerms =
    [
        "earnings", "results", "eps", "revenue", "guidance"
    ];

    private static readonly string[] PreviewTerms =
    [
        "earnings preview", "ahead of earnings", "what to expect", "set to report", "expected to report"
    ];

    private static readonly char[] HeadlineSeparators =
        [' ', '\t', '\r', '\n', ',', '.', ':', ';', '(', ')', '[', ']', '/', '\\', '-'];

    // Legal suffixes removed from the company name before looking for it in a headline; the first match wins.
    private static readonly string[] CompanySuffixes =
        [" Corporation", " Corp", " Incorporated", " Inc", " Holdings", " Holding", " Limited", " Ltd", " Plc", " Company", " Co"];

    private IndicatorEngine indicators;
    private readonly EarningsMonitorOptions options;

    public EarningsAnalyzer(IndicatorEngine indicators, EarningsMonitorOptions options)
    {
        this.indicators = indicators;
        this.options = options;
    }

    public void UpdateMarketSessionSchedules(
        IReadOnlyDictionary<DateOnly, MarketSessionSchedule> schedules)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        var profile = MarketEvidenceProfile.ProductionDefault.WithSessionSchedules(schedules);
        Volatile.Write(ref indicators, new IndicatorEngine(profile));
    }

    public EarningsAnalysisSnapshot Analyze(
        EarningsCalendarEvent calendarEvent,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<PersistedNewsItem> news,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        var normalizedNow = nowUtc.ToUniversalTime();
        var resultNews = FindResultNews(calendarEvent, news, normalizedNow);
        var resolvedResult = ResolveResult(calendarEvent, resultNews);
        var result = AssessResult(resolvedResult);
        var effectivePublicationTime = EffectivePublicationTime(calendarEvent, resultNews);
        var completedBars = bars
            .Where(bar => bar.Ticker.Equals(calendarEvent.Ticker, StringComparison.OrdinalIgnoreCase))
            .Where(bar => bar.Timeframe.Equals("5m", StringComparison.OrdinalIgnoreCase))
            .Where(bar => bar.Timestamp.AddMinutes(5) <= normalizedNow)
            .OrderBy(bar => bar.Timestamp)
            .ToArray();
        // Before the release the reference stops at the scheduled time; once the result is out it stops at the
        // earliest evidence of the release. Every later branch uses this same reference.
        var referenceCutoff = result == EarningsResultAssessment.Unknown && normalizedNow < calendarEvent.ScheduledAtUtc
            ? calendarEvent.ScheduledAtUtc
            : effectivePublicationTime;
        var engine = Volatile.Read(ref indicators);
        var referenceIndex = FindReferenceBarIndex(engine, completedBars, referenceCutoff, calendarEvent.ReleaseWindow);
        var referenceBars = referenceIndex < 0
            ? []
            : completedBars[Math.Max(0, referenceIndex - options.ReferenceBarCount + 1)..(referenceIndex + 1)];
        var referenceBar = referenceBars.LastOrDefault();
        var referenceHigh = referenceBars.Length > 0 ? referenceBars.Max(bar => bar.High) : (decimal?)null;
        var latestCompletedBar = completedBars.LastOrDefault();

        // A snapshot that stops before the breakout check, with whatever reference prices are already available.
        EarningsAnalysisSnapshot Unassessed(EarningsBreakoutAssessment breakout, string reason, OhlcvBar? latestBar) =>
            CreateSnapshot(calendarEvent, normalizedNow, result, breakout, reason, resultNews, resolvedResult,
                latestBar, referenceBar, referenceHigh);

        if (result == EarningsResultAssessment.Unknown && normalizedNow < calendarEvent.ScheduledAtUtc)
        {
            return Unassessed(EarningsBreakoutAssessment.AwaitingRelease, "Scheduled earnings have not been released.", latestCompletedBar);
        }

        if (completedBars.Length < 35)
        {
            return Unassessed(
                EarningsBreakoutAssessment.InsufficientData,
                $"Only {completedBars.Length} completed 5-minute bars are available; at least 35 are required.",
                latestCompletedBar);
        }

        var hasPostReleaseBar = completedBars.Any(bar => bar.Timestamp >= effectivePublicationTime);
        if (referenceBar is null || !hasPostReleaseBar)
        {
            return Unassessed(
                EarningsBreakoutAssessment.InsufficientData,
                "Completed pre-release and post-release bars are both required.",
                completedBars[^1]);
        }

        var latest = engine.Compute(completedBars)[^1];
        var referenceClose = referenceBar.Close;
        decimal? eventReturn = referenceClose == 0m
            ? null
            : (latest.CurrentPrice - referenceClose) / referenceClose * 100m;
        var (assessment, evidence) = AssessBreakout(resolvedResult, latest, referenceHigh!.Value);
        var postReleaseHighBar = completedBars[(referenceIndex + 1)..].MaxBy(bar => bar.High);

        return CreateSnapshot(
            calendarEvent,
            normalizedNow,
            result,
            assessment,
            $"{DescribeResult(result)}; {evidence}.",
            resultNews,
            resolvedResult,
            completedBars[^1],
            referenceBar,
            referenceHigh,
            postReleaseHighBar,
            latest.CurrentPrice,
            eventReturn,
            latest.Ema10,
            latest.Ema20,
            latest.MacdHistogram,
            latest.SlotRelativeVolume);
    }

    /// <summary>
    /// Index of the bar whose close is the price before the release, or -1 when none is available.
    /// It is the last completed regular-session 5-minute bar that ends by the cutoff. For a report the provider
    /// places before the open or after the close, the bar's whole session must also have closed by the cutoff:
    /// such a company does not publish mid-session, so a result first seen mid-session came out before that
    /// session opened, and that session's prices already include the reaction. When the calendar covers no
    /// regular-session bar before the cutoff, the last bar starting before the cutoff is used, as before.
    /// </summary>
    private static int FindReferenceBarIndex(
        IndicatorEngine engine,
        OhlcvBar[] completedBars,
        DateTimeOffset cutoff,
        EarningsReleaseWindow releaseWindow)
    {
        var releasedOutsideRegularHours = releaseWindow is
            EarningsReleaseWindow.BeforeMarketOpen or EarningsReleaseWindow.AfterMarketClose;
        var fallback = -1;
        for (var index = completedBars.Length - 1; index >= 0; index--)
        {
            var bar = completedBars[index];
            if (bar.Timestamp >= cutoff)
            {
                continue;
            }

            if (fallback < 0)
            {
                fallback = index;
            }

            var barEnd = bar.Timestamp.AddMinutes(5);
            if (barEnd > cutoff ||
                !engine.TryGetRegularSessionUtc(EarningsCalendarDates.ExchangeDate(bar.Timestamp), out var open, out var close) ||
                bar.Timestamp < open || barEnd > close ||
                releasedOutsideRegularHours && close > cutoff)
            {
                continue;
            }

            return index;
        }

        return fallback;
    }

    /// <summary>
    /// When the result became public: the result headline's time, else when the provider first showed the result,
    /// else when the event was received. It is capped at the 16:00 New York close for after-close reports and at
    /// the 04:00 pre-market open for before-open reports, so a late provider update cannot push it past the release.
    /// </summary>
    private static DateTimeOffset EffectivePublicationTime(EarningsCalendarEvent calendarEvent, PersistedNewsItem? resultNews)
    {
        var observed = resultNews?.Timestamp ??
            calendarEvent.ResultFirstSeenAtUtc ??
            calendarEvent.ProviderReceivedAtUtc;
        var exchangeHour = calendarEvent.ReleaseWindow switch
        {
            EarningsReleaseWindow.AfterMarketClose => 16,
            EarningsReleaseWindow.BeforeMarketOpen => 4,
            _ => (int?)null
        };
        if (exchangeHour is null)
        {
            return observed;
        }

        var cap = TimeZoneInfo.ConvertTimeToUtc(
            calendarEvent.ReportDateExchange.ToDateTime(new TimeOnly(exchangeHour.Value, 0)),
            EarningsTimeZones.NewYork);
        return observed > cap ? cap : observed;
    }

    /// <summary>
    /// Decides the breakout and lists the evidence. A breakout is a beat on both EPS and revenue with the price
    /// above the pre-release high. Volume is not a condition: after-hours 5-minute slots often have no trades, so a
    /// same-time volume baseline is rarely complete when results land. EMA and MACD are reported for context only.
    /// </summary>
    private static (EarningsBreakoutAssessment Assessment, string Evidence) AssessBreakout(
        ResolvedEarningsResult resolvedResult,
        IndicatorSnapshot latest,
        decimal referenceHigh)
    {
        var epsSurprise = resolvedResult.EpsSurprisePercent;
        var revenueSurprise = resolvedResult.RevenueSurprisePercent;
        var priceConfirmed = latest.CurrentPrice > referenceHigh;
        string[] evidence =
        [
            epsSurprise is null ? "EPS result not yet reported"
                : epsSurprise > 0m ? $"EPS beat by {epsSurprise:F1}%" : $"EPS did not beat ({epsSurprise:F1}%)",
            revenueSurprise is null ? "revenue result not yet reported"
                : revenueSurprise > 0m ? $"revenue beat by {revenueSurprise:F1}%" : $"revenue did not beat ({revenueSurprise:F1}%)",
            priceConfirmed
                ? $"close {latest.CurrentPrice:F2} is above pre-release high {referenceHigh:F2}"
                : $"close {latest.CurrentPrice:F2} is not above pre-release high {referenceHigh:F2}",
            $"for context: EMA10 {latest.Ema10:F2}, EMA20 {latest.Ema20:F2}, MACD histogram {latest.MacdHistogram:F4}"
        ];
        var assessment = epsSurprise <= 0m || revenueSurprise <= 0m || !priceConfirmed
            ? EarningsBreakoutAssessment.NotConfirmed
            : epsSurprise is null || revenueSurprise is null
                ? EarningsBreakoutAssessment.InsufficientData
                : EarningsBreakoutAssessment.Possible;
        return (assessment, String.Join("; ", evidence));
    }

    /// <summary>The opening sentence of an assessed card's reason.</summary>
    private static string DescribeResult(EarningsResultAssessment result) => result switch
    {
        EarningsResultAssessment.Positive => "Earnings result is positive",
        EarningsResultAssessment.Mixed => "Earnings result is mixed",
        EarningsResultAssessment.Negative => "Earnings result is negative",
        _ => "Earnings result is not yet available"
    };

    private static EarningsResultAssessment AssessResult(ResolvedEarningsResult result)
    {
        var surprises = new[] { result.EpsSurprisePercent, result.RevenueSurprisePercent }
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        if (surprises.Length == 0)
        {
            return EarningsResultAssessment.Unknown;
        }

        if (surprises.All(value => value >= 0m) && surprises.Any(value => value > 0m))
        {
            return EarningsResultAssessment.Positive;
        }

        if (surprises.All(value => value <= 0m) && surprises.Any(value => value < 0m))
        {
            return EarningsResultAssessment.Negative;
        }

        return EarningsResultAssessment.Mixed;
    }

    private static PersistedNewsItem? FindResultNews(
        EarningsCalendarEvent calendarEvent,
        IReadOnlyList<PersistedNewsItem> news,
        DateTimeOffset nowUtc)
    {
        var earliest = calendarEvent.ScheduledAtUtc.AddHours(-2);
        var candidates = news
            .Where(item => item.Ticker.Equals(calendarEvent.Ticker, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Timestamp >= earliest && item.Timestamp <= nowUtc)
            .Where(item => ResultTerms.Any(term =>
                item.Headline.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Where(item => EarningsNewsResultExtractor.TryExtract(item.Headline, out _) ||
                HeadlineNamesEvent(calendarEvent, item.Headline))
            .Where(item => !PreviewTerms.Any(term =>
                item.Headline.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (item.Summary?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)))
            .OrderBy(item => item.Timestamp)
            .ToArray();

        return candidates.FirstOrDefault(item => EarningsNewsResultExtractor.TryExtract(item.Headline, out _)) ??
            candidates.FirstOrDefault();
    }

    /// <summary>
    /// True when the headline names the event's stock: its ticker as a whole word, its company name without a
    /// legal suffix such as "Inc", or the first significant word of that name.
    /// </summary>
    private static bool HeadlineNamesEvent(EarningsCalendarEvent calendarEvent, string headline)
    {
        var words = headline.Split(
            HeadlineSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Any(word => word.Equals(calendarEvent.Ticker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var company = calendarEvent.CompanyName.Trim();
        foreach (var suffix in CompanySuffixes)
        {
            if (company.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                company = company[..^suffix.Length].TrimEnd('.', ',', ' ');
                break;
            }
        }

        if (company.Length >= 4 && headline.Contains(company, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var firstSignificantWord = company.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(w => w.Length >= 4 && !w.Equals("The", StringComparison.OrdinalIgnoreCase));

        if (firstSignificantWord != null && headline.Contains(firstSignificantWord, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static ResolvedEarningsResult ResolveResult(
        EarningsCalendarEvent calendarEvent,
        PersistedNewsItem? resultNews)
    {
        var providerEpsActual = calendarEvent.EpsActual ?? calendarEvent.ReportedEpsActual;
        var providerRevenueActual = calendarEvent.RevenueActualMillions;
        var extracted = resultNews is not null &&
            EarningsNewsResultExtractor.TryExtract(resultNews.Headline, out var newsResult)
                ? newsResult
                : EarningsNewsResult.Empty;
        var useNewsEps = providerEpsActual is null && extracted.EpsActual.HasValue;
        var useNewsRevenue = providerRevenueActual is null && extracted.RevenueActualMillions.HasValue;

        return new ResolvedEarningsResult(
            useNewsEps
                ? extracted.EpsEstimate
                : calendarEvent.EpsEstimate ?? calendarEvent.ReportedEpsEstimate,
            useNewsEps ? extracted.EpsActual : providerEpsActual,
            useNewsEps
                ? extracted.EpsSurprisePercent
                : calendarEvent.EpsSurprisePercent ?? calendarEvent.ReportedEpsSurprisePercent,
            useNewsRevenue ? extracted.RevenueEstimateMillions : calendarEvent.RevenueEstimateMillions,
            useNewsRevenue ? extracted.RevenueActualMillions : providerRevenueActual,
            useNewsRevenue ? extracted.RevenueSurprisePercent : calendarEvent.RevenueSurprisePercent,
            useNewsEps || useNewsRevenue
                ? $"{resultNews!.Provider} structured headline"
                : calendarEvent.Provider);
    }

    private static EarningsAnalysisSnapshot CreateSnapshot(
        EarningsCalendarEvent calendarEvent,
        DateTimeOffset analyzedAtUtc,
        EarningsResultAssessment result,
        EarningsBreakoutAssessment breakout,
        string reason,
        PersistedNewsItem? news,
        ResolvedEarningsResult resolvedResult,
        OhlcvBar? latestBar,
        OhlcvBar? referenceBar,
        decimal? referenceHigh,
        OhlcvBar? postReleaseHighBar = null,
        decimal? latestClose = null,
        decimal? eventReturn = null,
        decimal? ema10 = null,
        decimal? ema20 = null,
        decimal? macdHistogram = null,
        decimal? slotRelativeVolume = null) => new()
    {
        Id = Guid.NewGuid(),
        EarningsEventId = calendarEvent.Id,
        Ticker = calendarEvent.Ticker,
        AnalyzedAtUtc = analyzedAtUtc,
        ResultAssessment = result,
        BreakoutAssessment = breakout,
        Reason = reason,
        ResultNewsPublishedAtUtc = news?.Timestamp,
        NewsHeadline = news?.Headline,
        NewsUrl = news?.Url,
        NewsProvider = news?.Provider,
        NewsSentiment = news?.SentimentScore,
        ResultDataSource = resolvedResult.Source,
        EffectiveEpsEstimate = resolvedResult.EpsEstimate,
        EffectiveEpsActual = resolvedResult.EpsActual,
        EffectiveEpsSurprisePercent = resolvedResult.EpsSurprisePercent,
        EffectiveRevenueEstimateMillions = resolvedResult.RevenueEstimateMillions,
        EffectiveRevenueActualMillions = resolvedResult.RevenueActualMillions,
        EffectiveRevenueSurprisePercent = resolvedResult.RevenueSurprisePercent,
        LatestCompletedBarAtUtc = latestBar?.Timestamp,
        PreReleaseReferenceHigh = referenceHigh,
        PreReleaseReferenceClose = referenceBar?.Close,
        PreReleaseReferenceAtUtc = referenceBar?.Timestamp,
        PostReleaseHigh = postReleaseHighBar?.High,
        PostReleaseHighAtUtc = postReleaseHighBar?.Timestamp,
        LatestClose = latestClose ?? latestBar?.Close,
        EventReturnPercent = eventReturn,
        Ema10 = ema10,
        Ema20 = ema20,
        MacdHistogram = macdHistogram,
        SlotRelativeVolume = slotRelativeVolume
    };

    private sealed record ResolvedEarningsResult(
        decimal? EpsEstimate,
        decimal? EpsActual,
        decimal? EpsSurprisePercent,
        decimal? RevenueEstimateMillions,
        decimal? RevenueActualMillions,
        decimal? RevenueSurprisePercent,
        string Source);
}

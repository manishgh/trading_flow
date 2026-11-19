using System.Text.RegularExpressions;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.News;
using TradingFlow.Domain.Research;

namespace TradingFlow.Engine.Catalysts;

/// <summary>
/// Outcome of evaluating all point-in-time catalysts in a strategy's lookback window against frozen receipt delay,
/// allowed Tier-1 categories, and sentiment veto thresholds.
/// </summary>
public sealed record TierOneCatalystEvaluation(
    bool Qualified,
    CatalystEvent? SelectedCatalyst,
    string? MatchedCategory,
    decimal? SelectedAgeHours,
    string? RejectionReason);

/// <summary>
/// Evaluates a ticker's catalysts across the full configured lookback window under the frozen conservative
/// receipt-delay contract so a later minor headline cannot hide an earlier Tier-1 catalyst or erase a sentiment veto.
/// Preserves provider published/updated timestamps separately from observed HTTP receipt times: historical backtests
/// evaluate the explicit frozen assumption <c>max(created, updated) + delayMinutes</c> while live/paper/Desk paths
/// enforce <c>max(max(created, updated) + delayMinutes, observedReceiptUtc)</c>.
/// </summary>
public static class TierOneCatalystEvaluator
{
    private const RegexOptions PatternOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex FdaPattern = new(@"\bfda\b", PatternOptions, RegexTimeout);
    private static readonly Regex ContractPattern = new(@"\b(?:contract|commercial agreement|supply agreement|licensing agreement|strategic partnership|customer agreement)\b", PatternOptions, RegexTimeout);
    private static readonly Regex UpgradePattern = new(@"\b(?:upgrades?|upgraded|raises?|raised|boosts?|boosted|lifts?|lifted)\b", PatternOptions, RegexTimeout);
    private static readonly Regex DowngradePattern = new(@"\b(?:downgrades?|downgraded|cuts?|cut|lowers?|lowered)\b", PatternOptions, RegexTimeout);
    private static readonly Regex NegativeCuePattern = new(
        @"\b(?:misses?|missed|cuts?|cut|lowers?|lowered|withdraws?|withdrew|downgrades?|downgraded|rejects?|rejected|denies|denied|warning|complete response letter|crl|falls?\s+short|below\s+estimates?|below\s+expectations?|weak\s+guidance|lower\s+guidance)\b",
        PatternOptions,
        RegexTimeout);

    public static readonly IReadOnlySet<string> SupportedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "earnings",
        "guidance",
        "fda",
        "contract",
        "upgrade",
        "downgrade"
    };

    /// <summary>
    /// Parses and validates a comma-separated list of allowed Tier-1 catalyst category tokens. Throws
    /// <see cref="InvalidOperationException"/> when empty or containing any unsupported category name.
    /// </summary>
    public static IReadOnlySet<string> ParseAllowedCategories(string raw)
    {
        var tokens = (raw ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tokens.Length == 0)
        {
            throw new InvalidOperationException(
                "entry_rules.allowed_catalyst_categories must specify at least one supported category (earnings, guidance, fda, contract, upgrade, downgrade).");
        }

        var unknown = tokens.Where(token => !SupportedCategories.Contains(token)).ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"entry_rules.allowed_catalyst_categories contains unsupported category name(s): {string.Join(", ", unknown)}.");
        }

        return new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Frozen conservative historical research availability timestamp: <c>max(Timestamp, UpdatedAt) + delayMinutes</c>.
    /// Does not modify or backdate observed <see cref="CatalystEvent.ReceivedAt"/> or <see cref="CatalystEvent.DecisionAvailableAt"/>.
    /// </summary>
    public static DateTimeOffset AssumedHistoricalAvailableAtUtc(CatalystEvent catalyst, int receiptDelayMinutes)
    {
        var publishedUtc = catalyst.Timestamp.ToUniversalTime();
        var providerLatestUtc = catalyst.UpdatedAt is { } updated && updated.ToUniversalTime() > publishedUtc
            ? updated.ToUniversalTime()
            : publishedUtc;
        return providerLatestUtc.AddMinutes(Math.Max(0, receiptDelayMinutes));
    }

    /// <summary>
    /// Conservative point-in-time availability timestamp for a catalyst. In historical research mode
    /// (<paramref name="ignoreObservedReceiptForHistoricalResearch"/> = <c>true</c>), applies the explicit frozen
    /// assumption <c>max(created, updated) + delayMinutes</c>. In live/paper/Desk mode (<c>false</c>), enforces
    /// <c>max(max(created, updated) + delayMinutes, observedUtc)</c> without adding delay on top of an already-later
    /// observed <see cref="CatalystEvent.DecisionAvailableAt"/> or <see cref="CatalystEvent.ReceivedAt"/>.
    /// </summary>
    public static DateTimeOffset EffectiveAvailableAtUtc(
        CatalystEvent catalyst,
        int receiptDelayMinutes,
        bool ignoreObservedReceiptForHistoricalResearch = false)
    {
        var assumedUtc = AssumedHistoricalAvailableAtUtc(catalyst, receiptDelayMinutes);
        if (ignoreObservedReceiptForHistoricalResearch)
        {
            return assumedUtc;
        }

        var observed = (catalyst.DecisionAvailableAt ?? catalyst.ReceivedAt)?.ToUniversalTime();
        return observed is { } observedUtc && observedUtc > assumedUtc
            ? observedUtc
            : assumedUtc;
    }

    /// <summary>
    /// Evaluates every available catalyst for <paramref name="ticker"/> in <c>[windowStartUtc, signalEndTimeUtc]</c>
    /// (defaulting <c>windowStartUtc</c> to <c>signalEndTimeUtc - maxNewsAgeHours</c>) under
    /// <paramref name="receiptDelayMinutes"/>. Applies sentiment vetoes across the entire eligible window first so a
    /// later neutral headline cannot mask a vetoing story, then picks the newest story matching
    /// <paramref name="allowedCategories"/>.
    /// </summary>
    public static TierOneCatalystEvaluation Evaluate(
        string ticker,
        IReadOnlyList<CatalystEvent> catalysts,
        DateTimeOffset signalEndTimeUtc,
        decimal maxNewsAgeHours,
        int receiptDelayMinutes,
        IReadOnlySet<string> allowedCategories,
        decimal? vetoSentimentAtOrBelow = null,
        decimal? vetoSentimentAtOrAbove = null,
        DateTimeOffset? windowStartUtc = null,
        bool ignoreObservedReceiptForHistoricalResearch = false,
        bool requireNegativeBias = false)
    {
        var cutoffUtc = signalEndTimeUtc.ToUniversalTime();
        var earliestPublishedUtc = windowStartUtc?.ToUniversalTime()
            ?? cutoffUtc.AddHours(-(double)maxNewsAgeHours);
        var eligible = new List<(CatalystEvent Event, DateTimeOffset AvailableAtUtc, decimal AgeHours)>();

        foreach (var catalyst in catalysts)
        {
            if (!catalyst.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var availableAtUtc = EffectiveAvailableAtUtc(
                catalyst,
                receiptDelayMinutes,
                ignoreObservedReceiptForHistoricalResearch);
            if (availableAtUtc > cutoffUtc)
            {
                continue;
            }

            var publishedUtc = catalyst.Timestamp.ToUniversalTime();
            if (publishedUtc < earliestPublishedUtc || publishedUtc > cutoffUtc)
            {
                continue;
            }

            var ageHours = (decimal)(cutoffUtc - publishedUtc).TotalHours;
            eligible.Add((catalyst, availableAtUtc, ageHours));
        }

        if (eligible.Count == 0)
        {
            return new TierOneCatalystEvaluation(false, null, null, null, "tier1_catalyst_missing");
        }

        // Order newest availability first so both veto attribution and qualifying selection are deterministic.
        eligible.Sort((a, b) =>
        {
            var byAvailable = b.AvailableAtUtc.CompareTo(a.AvailableAtUtc);
            return byAvailable != 0 ? byAvailable : b.Event.Timestamp.CompareTo(a.Event.Timestamp);
        });

        if (vetoSentimentAtOrBelow is { } negativeThreshold)
        {
            var vetoing = eligible.FirstOrDefault(item => item.Event.SentimentScore <= negativeThreshold);
            if (vetoing.Event is not null)
            {
                return new TierOneCatalystEvaluation(
                    false,
                    vetoing.Event,
                    null,
                    vetoing.AgeHours,
                    $"negative_news_sentiment (Actual: {vetoing.Event.SentimentScore.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}, VetoBelow: {negativeThreshold.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}, Headline: {vetoing.Event.Headline})");
            }
        }

        if (vetoSentimentAtOrAbove is { } positiveThreshold)
        {
            var vetoing = eligible.FirstOrDefault(item => item.Event.SentimentScore >= positiveThreshold);
            if (vetoing.Event is not null)
            {
                return new TierOneCatalystEvaluation(
                    false,
                    vetoing.Event,
                    null,
                    vetoing.AgeHours,
                    $"positive_news_sentiment_veto (Actual: {vetoing.Event.SentimentScore.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}, VetoAbove: {positiveThreshold.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}, Headline: {vetoing.Event.Headline})");
            }
        }

        var sawCategoryWithoutNegativeBias = false;
        foreach (var candidate in eligible)
        {
            var (matched, hasNegativeDirectionCue) = MatchAllowedCategoryWithBias(candidate.Event, allowedCategories, cutoffUtc);
            if (matched is null)
            {
                continue;
            }

            if (requireNegativeBias && !hasNegativeDirectionCue && candidate.Event.SentimentScore >= 0m)
            {
                sawCategoryWithoutNegativeBias = true;
                continue;
            }

            return new TierOneCatalystEvaluation(
                true,
                candidate.Event,
                matched,
                candidate.AgeHours,
                null);
        }

        return new TierOneCatalystEvaluation(
            false,
            eligible[0].Event,
            null,
            eligible[0].AgeHours,
            sawCategoryWithoutNegativeBias
                ? "tier1_negative_catalyst_not_confirmed"
                : "tier1_catalyst_category_not_matched");
    }

    // Classifies one catalyst using the shared EconomicNewsRuleClassifier cues (excluding negated/speculative cues
    // and promotional items) plus explicit CatalystType metadata, returning the first matched allowed category token.
    public static string? MatchAllowedCategory(
        CatalystEvent catalyst,
        IReadOnlySet<string> allowedCategories,
        DateTimeOffset classifiedAtUtc) =>
        MatchAllowedCategoryWithBias(catalyst, allowedCategories, classifiedAtUtc).Category;

    // Evaluates category membership alongside whether the matched economic cues / headline express a clear negative
    // catalyst direction (for short news ORB confirmation when FinBERT sentiment is neutral/0).
    private static (string? Category, bool HasNegativeDirectionCue) MatchAllowedCategoryWithBias(
        CatalystEvent catalyst,
        IReadOnlySet<string> allowedCategories,
        DateTimeOffset classifiedAtUtc)
    {
        var input = new EconomicNewsRuleInput(
            catalyst.Headline ?? string.Empty,
            catalyst.Summary,
            Body: null,
            Symbols: string.IsNullOrWhiteSpace(catalyst.Ticker) ? null : [catalyst.Ticker],
            Provider: catalyst.Provider ?? string.Empty);
        var ruleResult = EconomicNewsRuleClassifier.Classify(input, classifiedAtUtc.ToUniversalTime());

        // Inspect non-uncertain economic cues so multi-event headlines (e.g., "beats earnings and raises guidance")
        // match cleanly while rumored/scheduled/negated headlines remain excluded.
        var activeCues = ruleResult.MatchedCues
            .Where(cue => !cue.AssertionUncertain && cue.Category != CatalystNewsCategory.PromotionalLowInformation)
            .ToArray();

        string? matched = null;
        if (allowedCategories.Contains("earnings") &&
            (catalyst.Type == CatalystType.EarningsRelease ||
             activeCues.Any(cue => cue.Category == CatalystNewsCategory.Earnings)))
        {
            matched = "earnings";
        }
        else if (allowedCategories.Contains("guidance") &&
            activeCues.Any(cue => cue.Category == CatalystNewsCategory.Guidance))
        {
            matched = "guidance";
        }
        else if (allowedCategories.Contains("fda") &&
            activeCues.Any(cue => cue.Category == CatalystNewsCategory.Regulatory && FdaPattern.IsMatch(cue.MatchedText)))
        {
            matched = "fda";
        }
        else if (allowedCategories.Contains("contract") &&
            activeCues.Any(cue => cue.Category == CatalystNewsCategory.MajorCommercialEvent && ContractPattern.IsMatch(cue.MatchedText)))
        {
            matched = "contract";
        }
        else if (allowedCategories.Contains("upgrade") &&
            (catalyst.Type == CatalystType.AnalystUpgrade ||
             activeCues.Any(cue =>
                 cue.Category == CatalystNewsCategory.AnalystAction &&
                 UpgradePattern.IsMatch(cue.MatchedText) &&
                 !DowngradePattern.IsMatch(cue.MatchedText))))
        {
            matched = "upgrade";
        }
        else if (allowedCategories.Contains("downgrade") &&
            (catalyst.Type == CatalystType.AnalystDowngrade ||
             activeCues.Any(cue =>
                 cue.Category == CatalystNewsCategory.AnalystAction &&
                 DowngradePattern.IsMatch(cue.MatchedText) &&
                 !UpgradePattern.IsMatch(cue.MatchedText))))
        {
            matched = "downgrade";
        }

        if (matched is null)
        {
            return (null, false);
        }

        var hasNegativeBias =
            matched == "downgrade" ||
            catalyst.Type == CatalystType.AnalystDowngrade ||
            ruleResult.Direction == CatalystDirection.Negative ||
            activeCues.Any(cue => NegativeCuePattern.IsMatch(cue.MatchedText)) ||
            (!string.IsNullOrWhiteSpace(catalyst.Headline) && NegativeCuePattern.IsMatch(catalyst.Headline));

        return (matched, hasNegativeBias);
    }
}

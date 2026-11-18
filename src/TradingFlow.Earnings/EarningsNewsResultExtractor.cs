using System.Globalization;
using System.Text.RegularExpressions;
using TradingFlow.Domain.Earnings;

namespace TradingFlow.Earnings;

/// <summary>
/// Extracts provider-reported earnings values only from explicit, structured result headlines.
/// Generic market-mover articles are deliberately rejected because they cannot establish the
/// earnings publication time or replace missing calendar actuals safely.
/// </summary>
public static partial class EarningsNewsResultExtractor
{
    /// <summary>
    /// True when a headline announces that results have been published, with or without figures: a structured
    /// result ("EPS $4.30 Beats $4.07 Estimate"), or a company release such as "Kandi Technologies Reports Unaudited
    /// Financial Results for the Nine Months Ended September 30, 2026". Announcements of a later release, delays,
    /// filing notices and non-financial "results" (see <see cref="FutureReleasePattern"/>) are not results. Figures
    /// are never read from a release headline; this only establishes that the release happened. Callers use
    /// <c>EarningsWatchWindow.HeadlineShowsRelease</c>, which also requires the slot's release window to have opened.
    /// </summary>
    public static bool IsResultsRelease(string headline) =>
        !String.IsNullOrWhiteSpace(headline) &&
        (TryExtract(headline, out _) ||
         ReleasePattern().IsMatch(headline) && !FutureReleasePattern().IsMatch(headline));

    public static bool TryExtract(string headline, out EarningsNewsResult result)
    {
        result = EarningsNewsResult.Empty;
        if (String.IsNullOrWhiteSpace(headline))
        {
            return false;
        }

        var eps = EpsPattern().Match(headline);
        var revenue = RevenuePattern().Match(headline);
        if (!eps.Success && !revenue.Success)
        {
            return false;
        }

        decimal? epsActual = null;
        decimal? epsEstimate = null;
        decimal? revenueActual = null;
        decimal? revenueEstimate = null;

        if (eps.Success)
        {
            if (!TryParseAccountingNumber(eps.Groups["actual"].Value, out var parsedEpsActual) ||
                !TryParseAccountingNumber(eps.Groups["estimate"].Value, out var parsedEpsEstimate))
            {
                return false;
            }

            epsActual = parsedEpsActual;
            epsEstimate = parsedEpsEstimate;
        }

        if (revenue.Success)
        {
            if (!TryParseScaledNumber(revenue.Groups["actual"].Value, revenue.Groups["actualScale"].Value, out var parsedRevenueActual) ||
                !TryParseScaledNumber(revenue.Groups["estimate"].Value, revenue.Groups["estimateScale"].Value, out var parsedRevenueEstimate))
            {
                return false;
            }

            revenueActual = parsedRevenueActual;
            revenueEstimate = parsedRevenueEstimate;
        }

        result = new EarningsNewsResult(
            epsEstimate,
            epsActual,
            EarningsNewsResult.SurprisePercent(epsActual, epsEstimate),
            revenueEstimate,
            revenueActual,
            EarningsNewsResult.SurprisePercent(revenueActual, revenueEstimate));
        return true;
    }

    private static bool TryParseAccountingNumber(string value, out decimal parsed)
    {
        var normalized = value.Trim();
        var negative = normalized.StartsWith('(') && normalized.EndsWith(')');
        normalized = normalized.Trim('(', ')').Replace(",", String.Empty, StringComparison.Ordinal);
        if (!Decimal.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out parsed))
        {
            return false;
        }

        parsed = negative ? -Math.Abs(parsed) : parsed;
        return true;
    }

    private static bool TryParseScaledNumber(string value, string scale, out decimal millions)
    {
        if (!TryParseAccountingNumber(value, out var parsed))
        {
            millions = 0m;
            return false;
        }

        millions = scale.ToUpperInvariant() switch
        {
            "B" => parsed * 1_000m,
            "K" => parsed / 1_000m,
            _ => parsed
        };
        return true;
    }

    [GeneratedRegex(@"\b(?:Adj\.?\s+)?EPS\s+\$?(?<actual>\(?-?[\d,]+(?:\.\d+)?\)?)\s+(?:Beat(?:s)?|Miss(?:es)?|Meet(?:s)?|In[ -]?Line(?:\s+With)?)\s+\$?(?<estimate>\(?-?[\d,]+(?:\.\d+)?\)?)\s+Estimate\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpsPattern();

    [GeneratedRegex(@"\b(?:Sales|Revenue)\s+\$?(?<actual>\(?-?[\d,]+(?:\.\d+)?\)?)(?<actualScale>[KMB])?\s+(?:Beat(?:s)?|Miss(?:es)?|Meet(?:s)?|In[ -]?Line(?:\s+With)?)\s+\$?(?<estimate>\(?-?[\d,]+(?:\.\d+)?\)?)(?<estimateScale>[KMB])?\s+Estimate\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RevenuePattern();

    // A company stating it has published results: a reporting verb in the present or past tense before "results"
    // or "earnings" in the same headline.
    [GeneratedRegex(@"\b(?:reports?|reported|announces?|announced|posts?|posted|releases?|released|delivers?|delivered)\b.*\b(?:results|earnings)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleasePattern();

    // Wording that announces a later release, its logistics or expectations rather than the results themselves,
    // including a release "on" a named weekday or calendar date ("Earnings Release on November 20"); delays and
    // filing notices; and non-financial "results" (annual meetings, trials, studies, tender offers, offerings).
    [GeneratedRegex(@"\b(?:to\s+(?:report|announce|release|post|host|present)|will\s+(?:report|announce|release|post|host)|conference\s+call|webcast|dates?|schedules?|scheduled|preview|ahead\s+of|expected\s+to|set\s+to|upcoming|next\s+week|today|tomorrow|tonight|timing|expects?|expectations|(?:after|before)\s+(?:the\s+)?(?:bell|close|open)|delay(?:s|ed)?|postpone(?:s|d)?|late|notice|annual\s+meeting|trials?|phase|topline|study|tender|offering|transcript|on\s+(?:mon|tues|wednes|thurs|fri|satur|sun)day|on\s+(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?\s+\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FutureReleasePattern();
}

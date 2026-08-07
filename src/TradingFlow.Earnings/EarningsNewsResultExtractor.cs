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
}

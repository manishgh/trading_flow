using System.Text.RegularExpressions;

namespace TradingFlow.Web.Services;

/// <summary>
/// Parses the symbol list a desk stream is opened with.
/// </summary>
/// <remarks>
/// The list arrives in a query string, so it is bounded and every entry must look
/// like a US equity symbol (letters, digits, dot or dash, starting with a letter).
/// Anything else is dropped rather than forwarded to the quote provider.
/// </remarks>
public static partial class DeskStreamTickers
{
    /// <summary>Most symbols one stream serves: the 250-symbol screener read cap plus room for held positions.</summary>
    public const int MaximumTickers = 300;

    public static IReadOnlySet<string> Parse(string? csv)
    {
        return (csv ?? String.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ticker => ticker.ToUpperInvariant())
            .Where(ticker => SymbolPattern().IsMatch(ticker))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumTickers)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^[A-Z][A-Z0-9.\\-]{0,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex SymbolPattern();
}

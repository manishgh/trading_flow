namespace TradingFlow.Alpaca;

/// <summary>
/// Translates the application's Finviz-compatible class-share notation to and
/// from Alpaca's dot notation at the provider boundary.
/// </summary>
public static class AlpacaSymbolMapper
{
    public static string ToCanonicalSymbol(string symbol) =>
        Normalize(symbol).Replace('.', '-');

    public static string ToProviderSymbol(string symbol) =>
        Normalize(symbol).Replace('-', '.');

    private static string Normalize(string symbol) =>
        !String.IsNullOrWhiteSpace(symbol)
            ? symbol.Trim().ToUpperInvariant()
            : throw new ArgumentException("Symbol is required.", nameof(symbol));
}

using System;

namespace TradingFlow.Alpaca;

public sealed record AlpacaOptions(
    Uri BaseUrl,
    string KeyId,
    string SecretKey,
    string TimeInForce = "gtc",
    string EntryOrderType = "limit",
    bool ExtendedHours = false,
    string MarketDataFeed = "sip",
    bool AllowIexFallback = false)
{
    private static readonly Uri MarketDataStreamBaseUrl = new("wss://stream.data.alpaca.markets/v2/");

    public static AlpacaOptions CreateDefault()
    {
        return new AlpacaOptions(
            new Uri("https://paper-api.alpaca.markets", UriKind.Absolute),
            string.Empty, // To be configured
            string.Empty, // To be configured
            "gtc",
            "limit",
            false,
            "sip",
            false
        );
    }

    public string ResolveMarketDataFeed()
    {
        var normalized = String.IsNullOrWhiteSpace(MarketDataFeed)
            ? "sip"
            : MarketDataFeed.Trim().ToLowerInvariant();

        if (normalized is not ("sip" or "iex" or "otc"))
        {
            throw new ArgumentException(
                $"Unsupported Alpaca market data feed '{MarketDataFeed}'. Use sip, iex, or otc.",
                nameof(MarketDataFeed));
        }

        if (normalized == "iex" && !AllowIexFallback)
        {
            throw new InvalidOperationException(
                "Alpaca IEX fallback is disabled. Configure the SIP feed or explicitly enable development-only IEX fallback.");
        }

        return normalized;
    }

    public Uri ResolveMarketDataStreamUrl()
    {
        return new Uri(MarketDataStreamBaseUrl, ResolveMarketDataFeed());
    }
}

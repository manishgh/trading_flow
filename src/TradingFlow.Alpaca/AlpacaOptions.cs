using System;

namespace TradingFlow.Alpaca;

public sealed record AlpacaOptions(
    Uri BaseUrl,
    string KeyId,
    string SecretKey,
    string TimeInForce = "gtc",
    string EntryOrderType = "limit",
    bool ExtendedHours = false,
    string MarketDataFeed = "sip")
{
    public static AlpacaOptions CreateDefault()
    {
        return new AlpacaOptions(
            new Uri("https://paper-api.alpaca.markets", UriKind.Absolute),
            string.Empty, // To be configured
            string.Empty, // To be configured
            "gtc",
            "limit",
            false,
            "sip"
        );
    }
}

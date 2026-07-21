using System;
using TradingFlow.Engine.Configuration;

namespace TradingFlow.Alpaca;

public sealed record AlpacaOptions(
    ProductionProfile Profile,
    string KeyId,
    string SecretKey,
    string TimeInForce = "gtc",
    string EntryOrderType = "limit",
    bool ExtendedHours = false,
    string MarketDataFeed = "sip",
    bool AllowIexFallback = false)
{
    public Uri BaseUrl => AlpacaEndpointResolver.Resolve(Profile).TradingRest;

    public static AlpacaOptions Create(ProductionProfile profile)
    {
        return new AlpacaOptions(
            profile,
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
        return new Uri(AlpacaEndpointResolver.Resolve(Profile).MarketDataStreamBase, ResolveMarketDataFeed());
    }

    public Uri ResolveTradingStreamUrl() => AlpacaEndpointResolver.Resolve(Profile).TradingStream;
}

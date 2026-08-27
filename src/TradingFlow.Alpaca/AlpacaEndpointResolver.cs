using TradingFlow.Engine.Configuration;

namespace TradingFlow.Alpaca;

public sealed record AlpacaEndpoints(
    Uri TradingRest,
    Uri TradingStream,
    Uri MarketDataRest,
    Uri MarketDataStreamBase,
    Uri NewsStream);

/// <summary>
/// Owns every Alpaca endpoint so credentials and caller-supplied URLs cannot redirect a profile.
/// Development intentionally uses paper trading endpoints; market data endpoints are profile-neutral.
/// </summary>
public static class AlpacaEndpointResolver
{
    private static readonly Uri PaperTradingRest = new("https://paper-api.alpaca.markets", UriKind.Absolute);
    private static readonly Uri PaperTradingStream = new("wss://paper-api.alpaca.markets/stream", UriKind.Absolute);
    private static readonly Uri LiveTradingRest = new("https://api.alpaca.markets", UriKind.Absolute);
    private static readonly Uri LiveTradingStream = new("wss://api.alpaca.markets/stream", UriKind.Absolute);
    private static readonly Uri MarketDataRest = new("https://data.alpaca.markets", UriKind.Absolute);
    private static readonly Uri MarketDataStreamBase = new("wss://stream.data.alpaca.markets/v2/", UriKind.Absolute);
    private static readonly Uri NewsStream = new("wss://stream.data.alpaca.markets/v1beta1/news", UriKind.Absolute);

    public static AlpacaEndpoints Resolve(ProductionProfile profile)
    {
        return profile switch
        {
            ProductionProfile.Development or ProductionProfile.Paper =>
                new AlpacaEndpoints(PaperTradingRest, PaperTradingStream, MarketDataRest, MarketDataStreamBase, NewsStream),
            ProductionProfile.Live =>
                new AlpacaEndpoints(LiveTradingRest, LiveTradingStream, MarketDataRest, MarketDataStreamBase, NewsStream),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported production profile.")
        };
    }
}

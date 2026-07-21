using TradingFlow.Etoro.Authentication;
using TradingFlow.Etoro.Configuration;
using TradingFlow.Etoro.Http;
using TradingFlow.Etoro.Insights;
using TradingFlow.Etoro.MarketData;
using TradingFlow.Etoro.Streaming;
using TradingFlow.Etoro.Trading;

namespace TradingFlow.Etoro;

public sealed record EtoroClientBundle(
    EtoroApiClient ApiClient,
    EtoroInstrumentResolver InstrumentResolver,
    EtoroMarketDataProvider MarketDataProvider,
    EtoroOrderRouter OrderRouter,
    EtoroPortfolioClient PortfolioClient,
    EtoroInsightsClient InsightsClient,
    EtoroWebSocketClient WebSocketClient);

public static class EtoroClientFactory
{
    public static EtoroClientBundle Create(EtoroOptions options, HttpMessageHandler? handler = null)
    {
        var httpClient = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: true);
        httpClient.BaseAddress = EnsureTrailingSlash(options.RestBaseUrl);
        httpClient.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);

        var credentialsProvider = new EtoroCredentialsProvider(options);
        var rateLimiter = new EtoroRateLimiter(options.RateLimits);
        var apiClient = new EtoroApiClient(httpClient, options, credentialsProvider, rateLimiter);
        var resolver = new EtoroInstrumentResolver(apiClient);
        var writeSafetyGuard = new EtoroWriteSafetyGuard();

        return new EtoroClientBundle(
            apiClient,
            resolver,
            new EtoroMarketDataProvider(apiClient, resolver),
            new EtoroOrderRouter(apiClient, options, resolver, writeSafetyGuard),
            new EtoroPortfolioClient(apiClient, options),
            new EtoroInsightsClient(apiClient),
            new EtoroWebSocketClient(options, credentialsProvider));
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
    }
}

using Microsoft.Extensions.Logging;
using TradingFlow.Alpaca;
using TradingFlow.Data.Csv;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Finviz;

namespace TradingFlow.Backtesting;

/// <summary>Constructs the same raw data adapters for research, CLI and hosted execution. Callers own caches and archive locations.</summary>
public sealed class ConfiguredDataProviders
{
    private readonly Func<AlpacaOptions> alpacaOptions;
    private readonly Func<FinvizOptions> finvizOptions;
    private readonly Func<string?> sentimentEndpoint;
    private readonly Func<HttpClient> createHttpClient;
    private readonly ILogger<AlpacaNewsProvider>? newsLogger;

    /// <summary>Accepts explicit host-owned sources; it never searches another application's configuration files.</summary>
    public ConfiguredDataProviders(Func<AlpacaOptions> alpacaOptions, Func<FinvizOptions> finvizOptions,
        Func<string?> sentimentEndpoint, Func<HttpClient>? createHttpClient = null,
        ILogger<AlpacaNewsProvider>? newsLogger = null)
    {
        this.alpacaOptions = alpacaOptions;
        this.finvizOptions = finvizOptions;
        this.sentimentEndpoint = sentimentEndpoint;
        this.createHttpClient = createHttpClient ?? (() => new HttpClient());
        this.newsLogger = newsLogger;
    }

    /// <summary>Standalone tools use their own environment; absent or explicitly blank credentials stay absent.</summary>
    public static ConfiguredDataProviders FromEnvironment(Func<string, string?>? readVariable = null)
    {
        var read = readVariable ?? Environment.GetEnvironmentVariable;
        return new(
            () => AlpacaOptions.Create(ProductionProfile.Paper) with {
                KeyId = read("ALPACA_KEY_ID") ?? String.Empty,
                SecretKey = read("ALPACA_SECRET_KEY") ?? String.Empty
            },
            () => FinvizOptions.CreateDefault() with { AuthToken = read("FINVIZ_API_KEY") ?? String.Empty },
            () => read("FINBERT_SENTIMENT_URL"));
    }

    /// <summary>Preserves host credentials and endpoint policy while taking the feed from the accepted run configuration.</summary>
    public AlpacaOptions AlpacaOptionsFor(BacktestRunConfig run) =>
        alpacaOptions() with { MarketDataFeed = run.Providers.Alpaca.DataFeed };

    /// <summary>Archives online responses with the caller's durable writer and preserves the run's requested price basis.</summary>
    public IMarketDataProvider CreateMarket(BacktestRunConfig run, IRawArchiveWriter rawArchiveWriter, string? csvRoot = null) =>
        run.Provider.ToLowerInvariant() switch {
            "csv" => new CsvMarketDataProvider(csvRoot ?? run.NormalizedRoot),
            "alpaca" => new AlpacaMarketDataProvider(createHttpClient(), AlpacaOptionsFor(run), rawArchiveWriter,
                adjustmentPolicy: run.Engine.ExecutionReplayPolicyId is ExecutionReplayPolicy.DailyRthFiveMinuteV1 or
                    ExecutionReplayPolicy.DailyRthSipPortfolioV1 or ExecutionReplayPolicy.IntradayRthFiveMinuteQuoteMarketV1 or
                    ExecutionReplayPolicy.PythonZoneBounceBreakdownV1 or ExecutionReplayPolicy.PythonZoneVolumeSpreadV1 ? "raw" : "all"),
            "finviz" => new FinvizMarketDataProvider(new FinvizClient(createHttpClient(), finvizOptions(), rawArchiveWriter)),
            _ => throw new NotSupportedException($"Unsupported market data provider: {run.Provider}")
        };

    /// <summary>Uses the accepted source, article budget and inference deadline without adding a fallback news provider.</summary>
    public ICatalystProvider? CreateNews(BacktestRunConfig run, IRawArchiveWriter rawArchiveWriter)
    {
        if (!run.News.Enabled) return null;
        return run.News.ProviderName.ToLowerInvariant() switch {
            "alpaca" => new AlpacaNewsProvider(createHttpClient(), AlpacaOptionsFor(run), rawArchiveWriter, newsLogger,
                CreateSentimentAnalyzer(run.News.SentimentTimeoutSeconds, sentimentEndpoint(), createHttpClient),
                run.News.MaxArticlesPerTicker),
            "finviz" => new FinvizNewsProvider(new FinvizClient(createHttpClient(), finvizOptions(), rawArchiveWriter)),
            "none" => null,
            _ => throw new NotSupportedException($"Unsupported news provider: {run.News.ProviderName}")
        };
    }

    /// <summary>Applies the configured sidecar deadline; absent sidecars use the analyzer that identifies itself as VADER.</summary>
    public static ISentimentAnalyzer CreateSentimentAnalyzer(int timeoutSeconds, string? endpoint,
        Func<HttpClient>? createHttpClient = null)
    {
        if (timeoutSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Sentiment timeout must be positive.");
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return new FinbertHttpSentimentAnalyzer((createHttpClient ?? (() => new HttpClient()))(), uri,
                TimeSpan.FromSeconds(timeoutSeconds));
        return new VaderSentimentAnalyzer();
    }
}

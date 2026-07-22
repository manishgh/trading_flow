using Microsoft.Extensions.Logging;
using TradingFlow.Backtesting;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Storage;

namespace TradingFlow.Web.Services;

/// <summary>
/// Centralizes the runtime object graph used by paper/live-style flows so
/// mobile automation and the standard paper runner use the same provider,
/// broker, and news construction logic.
/// </summary>
public sealed class PaperRuntimeFactory
{
    private readonly AlpacaCredentialProvider alpacaCredentials;
    private readonly ProjectPaths paths;
    private readonly ILogger<TradingFlow.Alpaca.AlpacaNewsProvider> alpacaNewsLogger;
    private readonly IRawArchiveWriter rawArchiveWriter;

    public PaperRuntimeFactory(
        AlpacaCredentialProvider alpacaCredentials,
        ProjectPaths paths,
        ILogger<TradingFlow.Alpaca.AlpacaNewsProvider>? alpacaNewsLogger = null,
        IRawArchiveWriter? rawArchiveWriter = null)
    {
        this.alpacaCredentials = alpacaCredentials;
        this.paths = paths;
        this.alpacaNewsLogger = alpacaNewsLogger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TradingFlow.Alpaca.AlpacaNewsProvider>.Instance;
        this.rawArchiveWriter = rawArchiveWriter ?? new FileSystemRawArchiveWriter(
            new RawArchiveOptions(Path.Combine(paths.DataRoot, "raw")));
    }

    public IRawArchiveWriter RawArchiveWriter => rawArchiveWriter;

    public BacktestRunConfig ResolveRunPaths(BacktestRunConfig run)
    {
        return run with
        {
            RawRoot = paths.ResolveRepositoryPath(run.RawRoot),
            NormalizedRoot = paths.ResolveRepositoryPath(run.NormalizedRoot),
            ResultsRoot = paths.ResolveRepositoryPath(run.ResultsRoot),
            Strategies = run.Strategies.Select(paths.ResolveRepositoryPath).ToArray()
        };
    }

    public IMarketDataProvider CreateProvider(BacktestRunConfig run)
    {
        return run.Provider.ToLowerInvariant() switch
        {
            "csv" => new TradingFlow.Data.Csv.CsvMarketDataProvider(run.NormalizedRoot),
            "alpaca" => new TradingFlow.Alpaca.AlpacaMarketDataProvider(
                new HttpClient(),
                TradingFlow.Alpaca.AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
                {
                    KeyId = alpacaCredentials.KeyId,
                    SecretKey = alpacaCredentials.SecretKey,
                    MarketDataFeed = run.Providers.Alpaca.DataFeed
                }),
            "finviz" => new TradingFlow.Finviz.FinvizMarketDataProvider(
                new TradingFlow.Finviz.FinvizClient(
                    new HttpClient(),
                    TradingFlow.Finviz.FinvizOptions.CreateDefault() with
                    {
                        AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? string.Empty
                    },
                    rawArchiveWriter)),
            _ => throw new NotSupportedException($"Unsupported market data provider: {run.Provider}")
        };
    }

    public ICatalystProvider? CreateNewsProvider(BacktestRunConfig run)
    {
        if (!run.News.Enabled)
        {
            return null;
        }

        return run.News.ProviderName.ToLowerInvariant() switch
        {
            "alpaca" => new TradingFlow.Alpaca.AlpacaNewsProvider(
                new HttpClient(),
                TradingFlow.Alpaca.AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
                {
                    KeyId = alpacaCredentials.KeyId,
                    SecretKey = alpacaCredentials.SecretKey,
                    MarketDataFeed = run.Providers.Alpaca.DataFeed
                },
                rawArchiveWriter,
                alpacaNewsLogger,
                CreateSentimentAnalyzer()),
            "finviz" => new TradingFlow.Finviz.FinvizNewsProvider(
                new TradingFlow.Finviz.FinvizClient(
                    new HttpClient(),
                    TradingFlow.Finviz.FinvizOptions.CreateDefault() with
                    {
                        AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? string.Empty
                    },
                    rawArchiveWriter)),
            "none" => null,
            _ => throw new NotSupportedException($"Unsupported news provider: {run.News.ProviderName}")
        };
    }

    public IBrokerClient? CreateBrokerClient(BacktestRunConfig run)
    {
        return run.Execution.Broker.ToLowerInvariant() switch
        {
            "alpaca" => new TradingFlow.Alpaca.AlpacaBrokerClient(
                new HttpClient(),
                new HttpClient(),
                TradingFlow.Alpaca.AlpacaOptions.Create(TradingFlow.Engine.Configuration.ProductionProfile.Paper) with
                {
                    KeyId = alpacaCredentials.KeyId,
                    SecretKey = alpacaCredentials.SecretKey
                },
                rawArchiveWriter),
            "none" => null,
            _ => throw new NotSupportedException($"Unsupported broker client: {run.Execution.Broker}")
        };
    }

    private static ISentimentAnalyzer CreateSentimentAnalyzer()
    {
        var endpoint = Environment.GetEnvironmentVariable("FINBERT_SENTIMENT_URL");
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return new TradingFlow.Alpaca.FinbertHttpSentimentAnalyzer(new HttpClient(), uri);
        }

        return new TradingFlow.Alpaca.VaderSentimentAnalyzer();
    }
}

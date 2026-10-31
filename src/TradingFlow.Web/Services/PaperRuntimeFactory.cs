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
    private readonly ConfiguredDataProviders dataProviders;
    private readonly IRawArchiveWriter rawArchiveWriter;

    /// <summary>Retains the host's archive owner and delegates raw adapter construction to the common factory.</summary>
    public PaperRuntimeFactory(
        AlpacaCredentialProvider alpacaCredentials,
        ProjectPaths paths,
        ILogger<TradingFlow.Alpaca.AlpacaNewsProvider>? alpacaNewsLogger = null,
        IRawArchiveWriter? rawArchiveWriter = null,
        ConfiguredDataProviders? dataProviders = null)
    {
        this.alpacaCredentials = alpacaCredentials;
        this.paths = paths;
        this.dataProviders = dataProviders ?? new ConfiguredDataProviders(
            () => TradingFlow.Alpaca.AlpacaOptions.Create(ProductionProfile.Paper) with {
                KeyId = alpacaCredentials.KeyId, SecretKey = alpacaCredentials.SecretKey
            },
            () => TradingFlow.Finviz.FinvizOptions.CreateDefault() with {
                AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? String.Empty
            },
            () => Environment.GetEnvironmentVariable("FINBERT_SENTIMENT_URL"), newsLogger: alpacaNewsLogger);
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

    /// <summary>Uses the shared raw market adapter with this host's explicit credentials and archive owner.</summary>
    public IMarketDataProvider CreateProvider(BacktestRunConfig run) => dataProviders.CreateMarket(run, rawArchiveWriter);

    /// <summary>Uses the same provider, budget and sentiment deadline as research and CLI execution.</summary>
    public ICatalystProvider? CreateNewsProvider(BacktestRunConfig run) => dataProviders.CreateNews(run, rawArchiveWriter);

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

    /// <summary>Uses the profile's inference deadline when a sidecar is configured; the fallback retains its actual analyzer identity.</summary>
    internal static ISentimentAnalyzer CreateSentimentAnalyzer(int timeoutSeconds, string? endpoint) =>
        ConfiguredDataProviders.CreateSentimentAnalyzer(timeoutSeconds, endpoint);
}

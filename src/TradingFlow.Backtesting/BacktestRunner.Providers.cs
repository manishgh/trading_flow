using System.Text.Json;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Data.Csv;
using TradingFlow.Data.Catalysts;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Market;
using TradingFlow.Domain.Orders;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Abstractions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Execution;
using TradingFlow.Engine.Market;
using TradingFlow.Engine.Pipeline;
using TradingFlow.Engine.Sessions;
using TradingFlow.Engine.Storage;
using TradingFlow.Engine.Strategies;
using TradingFlow.Engine.Universe;

namespace TradingFlow.Backtesting;

// Provider and credential wiring for BacktestRunner: market-data / news / sentiment provider
// creation, Alpaca option and secret resolution, and repository-file lookup. Split into a
// partial file for readability; behavior is identical to the inline version.
public sealed partial class BacktestRunner
{
    private static ICatalystProvider? CreateNewsProvider(BacktestRunConfig run)
    {
        if (!run.News.Enabled) return null;

        ICatalystProvider? provider = run.News.ProviderName.ToLowerInvariant() switch
        {
            "alpaca" => new TradingFlow.Alpaca.AlpacaNewsProvider(
                new HttpClient(),
                ResolveAlpacaOptions(run),
                sentimentAnalyzer: CreateSentimentAnalyzer(run.News.SentimentTimeoutSeconds),
                maxArticlesPerTicker: run.News.MaxArticlesPerTicker),
            "finviz" => new TradingFlow.Finviz.FinvizNewsProvider(
                new TradingFlow.Finviz.FinvizClient(
                    new HttpClient(),
                    TradingFlow.Finviz.FinvizOptions.CreateDefault() with { AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? "" }
                )),
            "none" => null,
            _ => throw new NotSupportedException($"Unsupported news provider: {run.News.ProviderName}")
        };

        if (provider is null ||
            run.CachePolicy.Equals("bypass", StringComparison.OrdinalIgnoreCase))
        {
            return provider;
        }

        return new CachedCatalystProvider(
            provider,
            Path.Combine(run.NormalizedRoot, GetCacheWindowSegment(run.TimeWindow)),
            run.CachePolicy);
    }

    private static TradingFlow.Engine.Abstractions.ISentimentAnalyzer CreateSentimentAnalyzer(int timeoutSeconds)
    {
        var endpoint = Environment.GetEnvironmentVariable("FINBERT_SENTIMENT_URL");
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return new TradingFlow.Alpaca.FinbertHttpSentimentAnalyzer(
                new HttpClient(),
                uri,
                requestTimeout: TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        }

        return new TradingFlow.Alpaca.VaderSentimentAnalyzer();
    }

    private static IMarketDataProvider CreateProvider(BacktestRunConfig run)
    {
        IMarketDataProvider provider = run.Provider.ToLowerInvariant() switch
        {
            "csv" => new CsvMarketDataProvider(run.NormalizedRoot),
            "alpaca" => new TradingFlow.Alpaca.AlpacaMarketDataProvider(
                new HttpClient(),
                ResolveAlpacaOptions(run)),
            "finviz" => new TradingFlow.Finviz.FinvizMarketDataProvider(
                new TradingFlow.Finviz.FinvizClient(
                    new HttpClient(),
                    TradingFlow.Finviz.FinvizOptions.CreateDefault() with { AuthToken = Environment.GetEnvironmentVariable("FINVIZ_API_KEY") ?? "" }
                )),
            _ => throw new NotSupportedException($"Unsupported market data provider: {run.Provider}")
        };

        if (run.Provider.Equals("csv", StringComparison.OrdinalIgnoreCase) ||
            run.CachePolicy.Equals("bypass", StringComparison.OrdinalIgnoreCase))
        {
            return provider;
        }

        return new CachedMarketDataProvider(
            provider,
            Path.Combine(run.NormalizedRoot, GetCacheWindowSegment(run.TimeWindow)),
            run.CachePolicy);
    }

    private static string GetCacheWindowSegment(TimeWindowConfig timeWindow)
    {
        if (timeWindow.Type.Equals("rolling", StringComparison.OrdinalIgnoreCase))
        {
            return $"{timeWindow.LookbackDays + Math.Max(timeWindow.WarmupLookbackDays, 0)}d";
        }

        if (timeWindow.Start is not null && timeWindow.End is not null)
        {
            return $"{timeWindow.Start:yyyyMMdd}-{timeWindow.End:yyyyMMdd}";
        }

        return "custom-window";
    }

    private static TradingFlow.Alpaca.AlpacaOptions ResolveAlpacaOptions(BacktestRunConfig run)
    {
        return TradingFlow.Alpaca.AlpacaOptions.CreateDefault() with
        {
            KeyId = ResolveSecret("Alpaca", "KeyId", "ALPACA_KEY_ID"),
            SecretKey = ResolveSecret("Alpaca", "SecretKey", "ALPACA_SECRET_KEY"),
            MarketDataFeed = run.Providers.Alpaca.DataFeed
        };
    }

    private static string ResolveSecret(string section, string key, string environmentVariable)
    {
        var settingsPath = FindRepositoryFile(Path.Combine("src", "TradingFlow.Web", "appsettings.local.json"));
        if (settingsPath is not null)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.TryGetProperty(section, out var sectionElement) &&
                sectionElement.TryGetProperty(key, out var keyElement))
            {
                var localValue = keyElement.GetString();
                if (!String.IsNullOrWhiteSpace(localValue))
                {
                    return localValue;
                }
            }

        }

        return Environment.GetEnvironmentVariable(environmentVariable) ?? String.Empty;
    }

    internal static string ResolveSecretForTesting(string section, string key, string environmentVariable)
    {
        return ResolveSecret(section, key, environmentVariable);
    }

    private static string? FindRepositoryFile(string relativePath)
    {
        foreach (var startPath in new[]
        {
            Environment.GetEnvironmentVariable("TRADINGFLOW_REPO_ROOT"),
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        })
        {
            if (String.IsNullOrWhiteSpace(startPath))
            {
                continue;
            }

            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }
}

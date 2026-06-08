using System.Text.Json;
using TradingFlow.Etoro;
using TradingFlow.Etoro.Configuration;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed class PaperEnvironmentService
{
    private readonly ConfigCatalogService catalog;

    public PaperEnvironmentService(ConfigCatalogService catalog)
    {
        this.catalog = catalog;
    }

    public async Task<PaperEnvironmentSnapshot> InspectAsync(string configPath, CancellationToken cancellationToken)
    {
        var config = catalog.GetConfig(configPath);
        var targetBroker = config.Config.Execution.Broker.ToLowerInvariant();
        
        var etoroApiKeyPresent = !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ETORO_DEMO_API_KEY"));
        var etoroUserKeyPresent = !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ETORO_DEMO_USER_KEY"));
        IReadOnlyDictionary<string, object?>? etoroCheck = null;

        if (targetBroker == "etoro" && etoroApiKeyPresent && etoroUserKeyPresent)
        {
            etoroCheck = await RunEtoroReadOnlyCheckAsync(cancellationToken);
        }

        var alpacaKeyIdPresent = !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALPACA_KEY_ID"));
        var alpacaSecretKeyPresent = !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ALPACA_SECRET_KEY"));
        IReadOnlyDictionary<string, object?>? alpacaCheck = null;

        if (targetBroker == "alpaca" && alpacaKeyIdPresent && alpacaSecretKeyPresent)
        {
            alpacaCheck = await RunAlpacaReadOnlyCheckAsync(cancellationToken);
        }

        return new PaperEnvironmentSnapshot(config, etoroApiKeyPresent, etoroUserKeyPresent, etoroCheck, alpacaKeyIdPresent, alpacaSecretKeyPresent, alpacaCheck);
    }

    private static async Task<IReadOnlyDictionary<string, object?>> RunAlpacaReadOnlyCheckAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        using var client = new HttpClient { BaseAddress = new Uri("https://paper-api.alpaca.markets") };
        client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", Environment.GetEnvironmentVariable("ALPACA_KEY_ID"));
        client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", Environment.GetEnvironmentVariable("ALPACA_SECRET_KEY"));

        result["account"] = await CaptureAsync(async () =>
        {
            var res = await client.GetAsync("/v2/account", cancellationToken);
            res.EnsureSuccessStatusCode();
            return SummarizeJson(JsonDocument.Parse(await res.Content.ReadAsStringAsync(cancellationToken)).RootElement);
        });

        result["positions"] = await CaptureAsync(async () =>
        {
            var res = await client.GetAsync("/v2/positions", cancellationToken);
            res.EnsureSuccessStatusCode();
            return SummarizeJson(JsonDocument.Parse(await res.Content.ReadAsStringAsync(cancellationToken)).RootElement);
        });

        return result;
    }

    private static async Task<IReadOnlyDictionary<string, object?>> RunEtoroReadOnlyCheckAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var bundle = EtoroClientFactory.Create(EtoroOptions.CreateDefault(EtoroEnvironment.Demo) with
        {
            Demo = new EtoroCredentialProfile("ETORO_DEMO_API_KEY", "ETORO_DEMO_USER_KEY", false),
            RequestTimeoutSeconds = 20,
            MaxRetries = 2
        });

        result["me"] = await CaptureAsync(async () => SummarizeJson(await bundle.ApiClient.GetAsync<JsonElement>("/me", cancellationToken)));
        var instrumentId = await bundle.InstrumentResolver.ResolveInstrumentIdAsync("AAPL", cancellationToken);
        result["aaplInstrumentId"] = instrumentId;
        result["rates"] = await CaptureAsync(async () =>
        {
            var rates = await bundle.MarketDataProvider.GetRatesAsync([instrumentId], cancellationToken);
            return new { count = rates.Count, first = rates.FirstOrDefault() };
        });
        result["portfolio"] = await CaptureAsync(async () =>
        {
            var portfolio = await bundle.PortfolioClient.GetPortfolioAsync(cancellationToken);
            return new
            {
                positions = portfolio.ClientPortfolio?.Positions?.Count ?? portfolio.Positions?.Count ?? 0,
                credit = portfolio.ClientPortfolio?.Credit
            };
        });
        result["pnl"] = await CaptureAsync(async () =>
        {
            var pnl = await bundle.PortfolioClient.GetPnlAsync(cancellationToken);
            return new
            {
                credit = pnl.ClientPortfolio?.Credit,
                unrealizedPnl = pnl.ClientPortfolio?.UnrealizedPnl,
                positions = pnl.ClientPortfolio?.Positions?.Count ?? 0
            };
        });

        return result;
    }

    private static async Task<object> CaptureAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return new { ok = true, value = await operation() };
        }
        catch (Exception exception)
        {
            return new { ok = false, error = exception.Message };
        }
    }

    private static object? SummarizeJson(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .Take(12)
                .ToDictionary(property => property.Name, property => property.Value.ValueKind.ToString(), StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => new { count = element.GetArrayLength() },
            _ => element.ToString()
        };
    }
}

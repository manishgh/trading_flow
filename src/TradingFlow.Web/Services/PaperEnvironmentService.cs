using System.Text.Json;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed class PaperEnvironmentService
{
    private readonly ConfigCatalogService catalog;
    private readonly AlpacaCredentialProvider alpacaCredentials;

    public PaperEnvironmentService(ConfigCatalogService catalog, AlpacaCredentialProvider alpacaCredentials)
    {
        this.catalog = catalog;
        this.alpacaCredentials = alpacaCredentials;
    }

    public async Task<PaperEnvironmentSnapshot> InspectAsync(string configPath, CancellationToken cancellationToken)
    {
        var config = catalog.GetConfig(configPath);
        var targetBroker = config.Config.Execution.Broker.ToLowerInvariant();

        var alpacaKeyIdPresent = !String.IsNullOrWhiteSpace(alpacaCredentials.KeyId);
        var alpacaSecretKeyPresent = !String.IsNullOrWhiteSpace(alpacaCredentials.SecretKey);
        IReadOnlyDictionary<string, object?>? alpacaCheck = null;

        if (targetBroker == "alpaca" && alpacaKeyIdPresent && alpacaSecretKeyPresent)
        {
            alpacaCheck = await RunAlpacaReadOnlyCheckAsync(alpacaCredentials, cancellationToken);
        }

        return new PaperEnvironmentSnapshot(config, alpacaKeyIdPresent, alpacaSecretKeyPresent, alpacaCheck);
    }

    private static async Task<IReadOnlyDictionary<string, object?>> RunAlpacaReadOnlyCheckAsync(AlpacaCredentialProvider credentials, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        using var client = new HttpClient { BaseAddress = new Uri("https://paper-api.alpaca.markets") };
        client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", credentials.KeyId);
        client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", credentials.SecretKey);

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

using System.Text.Json;
using TradingFlow.Alpaca;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Models;

namespace TradingFlow.Web.Services;

public sealed class PaperEnvironmentService
{
    private readonly ConfigCatalogService catalog;
    private readonly AlpacaCredentialProvider alpacaCredentials;
    private readonly IRawArchiveWriter rawArchiveWriter;

    public PaperEnvironmentService(
        ConfigCatalogService catalog,
        AlpacaCredentialProvider alpacaCredentials,
        IRawArchiveWriter rawArchiveWriter)
    {
        this.catalog = catalog;
        this.alpacaCredentials = alpacaCredentials;
        this.rawArchiveWriter = rawArchiveWriter;
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
            alpacaCheck = await RunAlpacaReadOnlyCheckAsync(alpacaCredentials, rawArchiveWriter, cancellationToken);
        }

        return new PaperEnvironmentSnapshot(config, alpacaKeyIdPresent, alpacaSecretKeyPresent, alpacaCheck);
    }

    private static async Task<IReadOnlyDictionary<string, object?>> RunAlpacaReadOnlyCheckAsync(
        AlpacaCredentialProvider credentials,
        IRawArchiveWriter rawArchiveWriter,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var options = AlpacaOptions.Create(ProductionProfile.Paper) with
        {
            KeyId = credentials.KeyId,
            SecretKey = credentials.SecretKey
        };
        using var client = new AlpacaTradingRestClient(new HttpClient(), options, rawArchiveWriter);

        result["account"] = await CaptureAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v2/account");
            var response = await client.SendAsync(
                request,
                "broker-account-check",
                cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(AlpacaTradingRestClient.DescribeFailure("account check", response));
            }

            using var document = JsonDocument.Parse(response.Payload);
            return SummarizeJson(document.RootElement);
        });

        result["positions"] = await CaptureAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v2/positions");
            var response = await client.SendAsync(
                request,
                "broker-position-check",
                cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(AlpacaTradingRestClient.DescribeFailure("position check", response));
            }

            using var document = JsonDocument.Parse(response.Payload);
            return SummarizeJson(document.RootElement);
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

using Microsoft.Extensions.Configuration;

namespace TradingFlow.Web.Services;

public sealed class AlpacaCredentialProvider
{
    private readonly IConfiguration configuration;

    public AlpacaCredentialProvider(IConfiguration configuration)
    {
        this.configuration = configuration;
    }

    public string KeyId => GetSecret("Alpaca:KeyId", "ALPACA_KEY_ID");

    public string SecretKey => GetSecret("Alpaca:SecretKey", "ALPACA_SECRET_KEY");

    public bool IsConfigured =>
        !String.IsNullOrWhiteSpace(KeyId) &&
        !String.IsNullOrWhiteSpace(SecretKey);

    private string GetSecret(string configurationKey, string environmentVariable)
    {
        return configuration[configurationKey]
            ?? Environment.GetEnvironmentVariable(environmentVariable)
            ?? String.Empty;
    }
}

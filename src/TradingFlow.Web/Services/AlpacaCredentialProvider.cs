using Microsoft.Extensions.Configuration;
using TradingFlow.Contracts.Transport;

namespace TradingFlow.Web.Services;

public sealed class AlpacaCredentialProvider
{
    private readonly IConfiguration configuration;
    private readonly Func<string, string?> readEnvironment;

    /// <summary>Reads the current host's configured secrets; an explicit blank environment value disables a local value.</summary>
    public AlpacaCredentialProvider(IConfiguration configuration, Func<string, string?>? readEnvironment = null)
    {
        this.configuration = configuration;
        this.readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
    }

    public string KeyId => GetSecret("Alpaca:KeyId", "ALPACA_KEY_ID");

    public string SecretKey => GetSecret("Alpaca:SecretKey", "ALPACA_SECRET_KEY");

    public bool IsConfigured =>
        !String.IsNullOrWhiteSpace(KeyId) &&
        !String.IsNullOrWhiteSpace(SecretKey);

    /// <summary>Fails production startup with setting names only when the host cannot contact required data or preparation services.</summary>
    public void RequireProductionConfiguration()
    {
        var missing = new List<string>();
        if (String.IsNullOrWhiteSpace(KeyId)) missing.Add("ALPACA_KEY_ID / Alpaca:KeyId");
        if (String.IsNullOrWhiteSpace(SecretKey)) missing.Add("ALPACA_SECRET_KEY / Alpaca:SecretKey");
        if (String.IsNullOrWhiteSpace(GetSecret(WarmupServiceAuthentication.ConfigurationKey, WarmupServiceAuthentication.EnvironmentKey)))
            missing.Add($"{WarmupServiceAuthentication.EnvironmentKey} / {WarmupServiceAuthentication.ConfigurationKey}");
        if (missing.Count > 0)
            throw new InvalidOperationException("Production host configuration is missing: " + String.Join(", ", missing) + ".");
    }

    /// <summary>Uses the host environment before its own configuration without searching filesystem ancestors.</summary>
    private string GetSecret(string configurationKey, string environmentVariable)
    {
        return readEnvironment(environmentVariable)
            ?? configuration[environmentVariable]
            ?? configuration[configurationKey]
            ?? String.Empty;
    }
}

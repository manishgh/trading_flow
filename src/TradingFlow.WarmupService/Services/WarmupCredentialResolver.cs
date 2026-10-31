using Microsoft.Extensions.Configuration;

namespace TradingFlow.WarmupService.Services;

public sealed class WarmupCredentialResolver(IConfiguration configuration, ILogger<WarmupCredentialResolver> logger)
{
    /// <summary>Uses explicit environment or this host's configuration only; no working-directory or Web-secret search is permitted.</summary>
    public string Resolve(string section, string key, string environmentVariable)
    {
        var environmentValue = Environment.GetEnvironmentVariable(environmentVariable);
        if (environmentValue is not null)
        {
            return environmentValue;
        }

        var configValue = configuration[$"{section}:{key}"];
        if (!String.IsNullOrWhiteSpace(configValue))
        {
            return configValue;
        }

        logger.LogWarning(
            "Credential {Section}:{Key} was not found. Set {EnvironmentVariable} or Key Vault mapped env vars before production use.",
            section,
            key,
            environmentVariable);
        return String.Empty;
    }

}

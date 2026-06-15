using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace TradingFlow.WarmupService.Services;

public sealed class WarmupCredentialResolver(IConfiguration configuration, ILogger<WarmupCredentialResolver> logger)
{
    public string Resolve(string section, string key, string environmentVariable)
    {
        var environmentValue = Environment.GetEnvironmentVariable(environmentVariable);
        if (!String.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        var configValue = configuration[$"{section}:{key}"];
        if (!String.IsNullOrWhiteSpace(configValue))
        {
            return configValue;
        }

        var webLocalPath = FindRepositoryFile(Path.Combine("src", "TradingFlow.Web", "appsettings.local.json"));
        if (webLocalPath is not null)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(webLocalPath));
            if (document.RootElement.TryGetProperty(section, out var sectionElement) &&
                sectionElement.TryGetProperty(key, out var keyElement) &&
                !String.IsNullOrWhiteSpace(keyElement.GetString()))
            {
                return keyElement.GetString()!;
            }
        }

        logger.LogWarning(
            "Credential {Section}:{Key} was not found. Set {EnvironmentVariable} or Key Vault mapped env vars before production use.",
            section,
            key,
            environmentVariable);
        return String.Empty;
    }

    private static string? FindRepositoryFile(string relativePath)
    {
        foreach (var startPath in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
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

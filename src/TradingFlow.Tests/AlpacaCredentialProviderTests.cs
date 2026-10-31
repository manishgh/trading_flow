using Microsoft.Extensions.Configuration;
using TradingFlow.Contracts.Transport;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class AlpacaCredentialProviderTests
{
    /// <summary>Production errors name missing inputs and never include configured secret values.</summary>
    [Fact]
    public void ProductionConfiguration_ListsMissingNamesWithoutSecretValues()
    {
        var credentials = new AlpacaCredentialProvider(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Alpaca:KeyId"] = "private-present-value" }).Build(), _ => null);
        var error = Assert.Throws<InvalidOperationException>(credentials.RequireProductionConfiguration);
        Assert.Contains("ALPACA_SECRET_KEY", error.Message);
        Assert.Contains(WarmupServiceAuthentication.EnvironmentKey, error.Message);
        Assert.DoesNotContain("private-present-value", error.Message);
        Assert.DoesNotContain("Alpaca:KeyId", error.Message);
    }

    /// <summary>An explicit blank environment value disables the corresponding host-local setting.</summary>
    [Fact]
    public void ExplicitBlankEnvironment_DoesNotFallBackToHostSettings()
    {
        var credentials = new AlpacaCredentialProvider(Configuration(), _ => String.Empty);
        Assert.False(credentials.IsConfigured);
        var error = Assert.Throws<InvalidOperationException>(credentials.RequireProductionConfiguration);
        Assert.Contains("ALPACA_KEY_ID", error.Message);
        Assert.Contains("ALPACA_SECRET_KEY", error.Message);
        Assert.Contains(WarmupServiceAuthentication.EnvironmentKey, error.Message);
    }

    /// <summary>Explicit current-host environment values and current-host configuration both satisfy the required inputs.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExplicitHostConfiguration_IsAccepted(bool environment)
    {
        var credentials = new AlpacaCredentialProvider(environment ? new ConfigurationBuilder().Build() : Configuration(),
            _ => environment ? "fixture-environment-value" : null);
        credentials.RequireProductionConfiguration();
        Assert.True(credentials.IsConfigured);
        Assert.Equal(environment ? "fixture-environment-value" : "fixture-host-key", credentials.KeyId);
    }

    /// <summary>Builds an isolated host configuration without environment or repository settings providers.</summary>
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["Alpaca:KeyId"] = "fixture-host-key", ["Alpaca:SecretKey"] = "fixture-host-secret",
        [WarmupServiceAuthentication.ConfigurationKey] = "fixture-service-key"
    }).Build();
}

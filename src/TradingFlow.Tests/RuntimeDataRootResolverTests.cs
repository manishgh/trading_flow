using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class RuntimeDataRootResolverTests
{
    [Fact]
    public void UiTestModeWithoutOverride_UsesIsolatedRepositoryTree()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "trading-flow-repository");

        var resolved = RuntimeDataRootResolver.Resolve(
            repositoryRoot,
            uiTestMode: true,
            configuredDataRoot: null);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(repositoryRoot, ".tmp", "ui-tests")),
            resolved);
        Assert.NotEqual(Path.GetFullPath(Path.Combine(repositoryRoot, "data")), resolved);
    }

    [Fact]
    public void ExplicitDataRoot_WinsOutsideUiTestMode()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "trading-flow-repository");
        var configured = Path.Combine(Path.GetTempPath(), "trading-flow-explicit-data");

        Assert.Equal(
            Path.GetFullPath(configured),
            RuntimeDataRootResolver.Resolve(repositoryRoot, false, configured));
    }

    [Fact]
    public void UiTestMode_RejectsOperationalRootOverride()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "trading-flow-repository");
        var operationalRoot = Path.Combine(repositoryRoot, "data");

        var error = Assert.Throws<InvalidOperationException>(() =>
            RuntimeDataRootResolver.Resolve(repositoryRoot, true, operationalRoot));

        Assert.Contains("isolated root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UiTestMode_AllowsExplicitChildOfIsolationRoot()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "trading-flow-repository");
        var isolatedData = Path.Combine(repositoryRoot, ".tmp", "ui-tests", "data");

        Assert.Equal(
            Path.GetFullPath(isolatedData),
            RuntimeDataRootResolver.Resolve(repositoryRoot, true, isolatedData));
    }

    // The environment variable wins, then the TradingFlow:DataRoot setting, then nothing (the repository default).
    [Theory]
    [InlineData("V:\\from-variable", "V:\\from-setting", false, "V:\\from-variable")]
    [InlineData(null, "V:\\from-setting", false, "V:\\from-setting")]
    [InlineData("  ", "V:\\from-setting", false, "V:\\from-setting")]
    [InlineData(null, null, false, null)]
    public void SelectConfigured_PrefersTheVariableThenTheSetting(string? variable, string? setting, bool uiTest, string? expected) =>
        Assert.Equal(expected, RuntimeDataRootResolver.SelectConfigured(variable, setting, uiTest));

    // A local settings file can never point a UI test at the operator's data; tests pass their root through the variable.
    [Fact]
    public void SelectConfigured_IgnoresTheSettingInUiTestMode()
    {
        Assert.Null(RuntimeDataRootResolver.SelectConfigured(null, "V:\\operator-data", uiTestMode: true));
        Assert.Equal("isolated", RuntimeDataRootResolver.SelectConfigured("isolated", "V:\\operator-data", uiTestMode: true));
    }

    // A relative configured folder is taken from the repository folder, not from the process's current folder.
    [Fact]
    public void RelativeDataRoot_IsResolvedFromTheRepositoryFolder()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "trading-flow-repository");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(repositoryRoot, "..", "shared-data")),
            RuntimeDataRootResolver.Resolve(repositoryRoot, false, Path.Combine("..", "shared-data")));
    }
}

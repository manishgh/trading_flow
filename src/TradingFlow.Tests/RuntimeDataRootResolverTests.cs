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
}

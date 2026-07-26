namespace TradingFlow.Tests;

public sealed class ProductionCompositionTests
{
    [Fact]
    public void Etoro_RemainsReferenceSource_AndIsExcludedFromProductionComposition()
    {
        var root = TestRepository.FindRoot();
        var etoroRoot = Path.Combine(root, "src", "TradingFlow.Etoro");
        Assert.True(Directory.Exists(etoroRoot), "The dormant eToro reference source must remain available.");

        AssertFileDoesNotContain(Path.Combine(root, "TradingFlow.sln"), "TradingFlow.Etoro");
        AssertFileDoesNotContain(Path.Combine(root, "TradingFlow.slnx"), "TradingFlow.Etoro");

        var productionProjects = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(etoroRoot, StringComparison.OrdinalIgnoreCase));
        foreach (var project in productionProjects)
        {
            AssertFileDoesNotContain(project, "TradingFlow.Etoro");
        }

        var productionEntryPoints = new[]
        {
            Path.Combine(root, "src", "TradingFlow.Web", "Program.cs"),
            Path.Combine(root, "src", "TradingFlow.Cli", "Program.cs"),
            Path.Combine(root, "src", "TradingFlow.WarmupService", "Program.cs")
        };
        foreach (var entryPoint in productionEntryPoints)
        {
            AssertFileDoesNotContain(entryPoint, "Etoro");
        }

        var deploymentFiles = Directory
            .EnumerateFiles(Path.Combine(root, "deploy"), "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".yaml", ".yml", ".bicep", ".json" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
        foreach (var deploymentFile in deploymentFiles)
        {
            AssertFileDoesNotContain(deploymentFile, "ETORO_");
        }
    }

    [Fact]
    public void ResearchAssembly_IsCompilerIsolatedFromRuntimeProjects()
    {
        var root = TestRepository.FindRoot();
        var runtimeProjects = new[]
        {
            Path.Combine(root, "src", "TradingFlow.Web", "TradingFlow.Web.csproj"),
            Path.Combine(root, "src", "TradingFlow.Backtesting", "TradingFlow.Backtesting.csproj"),
            Path.Combine(root, "src", "TradingFlow.Engine", "TradingFlow.Engine.csproj")
        };
        foreach (var project in runtimeProjects)
        {
            AssertFileDoesNotContain(project, "TradingFlow.Research");
        }

        var cliProject = Path.Combine(root, "src", "TradingFlow.Cli", "TradingFlow.Cli.csproj");
        Assert.Contains(
            "TradingFlow.Research",
            File.ReadAllText(cliProject),
            StringComparison.OrdinalIgnoreCase);

        var researchProject = Path.Combine(
            root,
            "src",
            "TradingFlow.Research",
            "TradingFlow.Research.csproj");
        foreach (var forbiddenReference in new[]
                 {
                     "TradingFlow.Data",
                     "TradingFlow.Alpaca",
                     "TradingFlow.Finviz",
                     "TradingFlow.Backtesting",
                     "Microsoft.Extensions.Http"
                 })
        {
            AssertFileDoesNotContain(researchProject, forbiddenReference);
        }

        var researchSources = Directory.EnumerateFiles(
            Path.Combine(root, "src", "TradingFlow.Research"),
            "*.cs",
            SearchOption.AllDirectories);
        foreach (var source in researchSources)
        {
            AssertFileDoesNotContain(source, "HttpClient");
            AssertFileDoesNotContain(source, "AlpacaMarketDataProvider");
            AssertFileDoesNotContain(source, "Finviz");
        }
    }

    private static void AssertFileDoesNotContain(string path, string value)
    {
        Assert.True(File.Exists(path), $"Required composition file is missing: {path}");
        Assert.DoesNotContain(value, File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }
}

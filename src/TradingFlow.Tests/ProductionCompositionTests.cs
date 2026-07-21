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

    private static void AssertFileDoesNotContain(string path, string value)
    {
        Assert.True(File.Exists(path), $"Required composition file is missing: {path}");
        Assert.DoesNotContain(value, File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }
}

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Pages;
using TradingFlow.Web.Services;
using Xunit;

namespace TradingFlow.Tests;

public sealed class PaperModelTests
{
    [Fact]
    public void OnGet_WithoutExplicitConfigDefaultsToCanonicalIntradayPaperConfig()
    {
        var repoRoot = FindRepositoryRoot();
        var catalog = CreateCatalog(repoRoot);
        var model = CreateModel(repoRoot, catalog);

        model.OnGet(configPath: null, strategyPath: null);

        Assert.EndsWith(
            Path.Combine("configs", "paper", "alpaca-paper.yaml"),
            model.ConfigPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            Path.Combine("configs", "strategies", "intraday-ross-vwap-ema-cumulative-volume.v8-adaptive-guard.yaml"),
            model.SelectedStrategyPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnGet_WithoutExplicitStrategyDefaultsToSelectedPaperConfigStrategy()
    {
        var repoRoot = FindRepositoryRoot();
        var catalog = CreateCatalog(repoRoot);
        var model = CreateModel(repoRoot, catalog);

        model.OnGet(
            Path.Combine(repoRoot, "configs", "paper", "alpaca-paper.yaml"),
            strategyPath: null);

        Assert.EndsWith(
            Path.Combine("configs", "strategies", "intraday-ross-vwap-ema-cumulative-volume.v8-adaptive-guard.yaml"),
            model.SelectedStrategyPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("setup_type: indicator_stack", model.StrategyYaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnGetStrategyDetails_NoNewsSwingStrategyReportsUsesNewsFalse()
    {
        var repoRoot = FindRepositoryRoot();
        var catalog = CreateCatalog(repoRoot);
        var model = CreateModel(repoRoot, catalog);
        var strategyPath = Path.Combine(repoRoot, "configs", "strategies", "swing-reversal-reclaim-bull-quality-no-news.v1.yaml");

        var result = model.OnGetStrategyDetails(strategyPath);
        var json = JsonSerializer.Serialize(((Microsoft.AspNetCore.Mvc.JsonResult)result).Value);
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.False(document.RootElement.GetProperty("usesNews").GetBoolean());
    }

    private static PaperModel CreateModel(string repoRoot, ConfigCatalogService catalog)
    {
        var configuration = new ConfigurationBuilder().Build();
        var credentialProvider = new AlpacaCredentialProvider(configuration);
        var paths = new ProjectPaths(repoRoot);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var paperJobs = new PaperJobService(
            new SimpleYamlReader(),
            scopeFactory.Object,
            credentialProvider,
            paths);
        return new PaperModel(
            catalog,
            new PaperEnvironmentService(catalog, credentialProvider),
            new RunConfigWriter(paths, new SimpleYamlReader(), AtomicFileArtifactWriter.Instance),
            paperJobs,
            AtomicFileArtifactWriter.Instance,
            NullLogger<PaperModel>.Instance);
    }

    private static ConfigCatalogService CreateCatalog(string repoRoot)
    {
        return new ConfigCatalogService(new ProjectPaths(repoRoot), new SimpleYamlReader());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "configs")) &&
                File.Exists(Path.Combine(directory.FullName, "TradingFlow.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find trading_flow repository root.");
    }
}

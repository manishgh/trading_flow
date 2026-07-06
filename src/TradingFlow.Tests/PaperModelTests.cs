using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Domain.Wishlists;
using TradingFlow.Web.Pages;
using TradingFlow.Web.Services;
using Xunit;

namespace TradingFlow.Tests;

public sealed class PaperModelTests
{
    [Fact]
    public async Task OnGet_WithoutExplicitConfigDefaultsToCanonicalIntradayPaperConfig()
    {
        var repoRoot = FindRepositoryRoot();
        var catalog = CreateCatalog(repoRoot);
        var model = CreateModel(repoRoot, catalog);

        await model.OnGetAsync(configPath: null, strategyPath: null, wishlistId: null, CancellationToken.None);

        Assert.EndsWith(
            Path.Combine("configs", "paper", "alpaca-paper.yaml"),
            model.ConfigPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(
            Path.Combine("configs", "strategies", "intraday-ema10-ema20-macd-volume.v1.yaml"),
            model.SelectedStrategyPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnGet_WithoutExplicitStrategyDefaultsToSelectedPaperConfigStrategy()
    {
        var repoRoot = FindRepositoryRoot();
        var catalog = CreateCatalog(repoRoot);
        var model = CreateModel(repoRoot, catalog);

        await model.OnGetAsync(
            Path.Combine(repoRoot, "configs", "paper", "alpaca-paper.yaml"),
            strategyPath: null,
            wishlistId: null,
            CancellationToken.None);

        Assert.EndsWith(
            Path.Combine("configs", "strategies", "intraday-ema10-ema20-macd-volume.v1.yaml"),
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
        var wishlistRepository = new Mock<IWishlistRepository>();
        wishlistRepository
            .Setup(repository => repository.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Wishlist>());
        return new PaperModel(
            catalog,
            new PaperEnvironmentService(catalog, credentialProvider),
            new RunConfigWriter(paths, new SimpleYamlReader(), AtomicFileArtifactWriter.Instance),
            paperJobs,
            AtomicFileArtifactWriter.Instance,
            wishlistRepository.Object,
            NullLogger<PaperModel>.Instance);
    }

    private static ConfigCatalogService CreateCatalog(string repoRoot)
    {
        return new ConfigCatalogService(new ProjectPaths(repoRoot), new SimpleYamlReader());
    }

    private static string FindRepositoryRoot()
    {
        return TestRepository.FindRoot();
    }
}


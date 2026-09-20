using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Domain.Wishlists;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingFlow.Data.Context;
using TradingFlow.Web.Pages;
using TradingFlow.Web.Services;
using TradingFlow.Web.Services.Wishlists;
using Xunit;

namespace TradingFlow.Tests;

public sealed class PaperModelTests
{
    [Fact]
    public async Task OnGet_WithoutExplicitConfigDefaultsToCanonicalProfileAndNoEligibleStrategy()
    {
        var repoRoot = FindRepositoryRoot();
        var catalog = CreateCatalog(repoRoot);
        var model = CreateModel(repoRoot, catalog);

        await model.OnGetAsync(configPath: null, strategyPath: null, wishlistId: null, CancellationToken.None);

        Assert.EndsWith(
            Path.Combine("configs", "paper", "alpaca-paper.yaml"),
            model.ConfigPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(model.SelectedStrategyPath);
        Assert.Empty(model.Strategies);
        Assert.False(model.SelectedStrategyUsesNews);
        Assert.False(model.NewsEnabled);
    }

    [Fact]
    public async Task OnGet_WithoutExplicitStrategyDoesNotFallBackToResearch()
    {
        var repoRoot = FindRepositoryRoot();
        var catalog = CreateCatalog(repoRoot);
        var model = CreateModel(repoRoot, catalog);

        await model.OnGetAsync(
            Path.Combine(repoRoot, "configs", "paper", "alpaca-paper.yaml"),
            strategyPath: null,
            wishlistId: null,
            CancellationToken.None);

        Assert.Empty(model.SelectedStrategyPath);
        Assert.Empty(model.Strategies);
    }

    [Fact]
    public async Task OnGetStrategyDetails_ArchivedStrategyIsNotPaperSelectable()
    {
        var repoRoot = FindRepositoryRoot();
        var catalog = CreateCatalog(repoRoot);
        var model = CreateModel(repoRoot, catalog);
        var strategyPath = Path.Combine(repoRoot, "configs", "strategies", "swing-reversal-reclaim-bull-quality-no-news.v1.yaml");

        var result = await model.OnGetStrategyDetailsAsync(strategyPath, "shadow", CancellationToken.None);
        var json = JsonSerializer.Serialize(((Microsoft.AspNetCore.Mvc.JsonResult)result).Value);
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task OnGetStrategyDetails_ResearchStrategyIsNotPaperSelectable()
    {
        var repoRoot = FindRepositoryRoot();
        var catalog = CreateCatalog(repoRoot);
        var model = CreateModel(repoRoot, catalog);
        var strategyPath = Path.Combine(repoRoot, "configs", "strategies", "minervini-trend-template-vcp.v4-trend-rider.yaml");

        var result = await model.OnGetStrategyDetailsAsync(strategyPath, "shadow", CancellationToken.None);
        var json = JsonSerializer.Serialize(((JsonResult)result).Value);
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task OnGetStrategyDetails_WithoutExplicitPaperModeFailsClosed()
    {
        var repoRoot = FindRepositoryRoot();
        var model = CreateModel(repoRoot, CreateCatalog(repoRoot));
        var strategyPath = Path.Combine(
            repoRoot,
            "configs",
            "strategies",
            "minervini-trend-template-vcp.v4-trend-rider.yaml");

        var result = await model.OnGetStrategyDetailsAsync(
            strategyPath,
            paperExecutionMode: null,
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("paper_execution_mode_invalid", JsonSerializer.Serialize(badRequest.Value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnPostRunLive_WithoutExplicitPaperModeFailsClosedBeforeRunCreation()
    {
        var repoRoot = FindRepositoryRoot();
        var model = CreateModel(repoRoot, CreateCatalog(repoRoot));
        var context = new DefaultHttpContext();
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["RunName"] = $"paper-missing-mode-{Guid.NewGuid():N}",
            ["BaseConfigPath"] = Path.Combine(repoRoot, "configs", "paper", "alpaca-paper.yaml")
        });
        model.PageContext = new PageContext { HttpContext = context };

        var result = await model.OnPostRunLive(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Contains(
            model.ModelState.Values.SelectMany(value => value.Errors),
            error => error.ErrorMessage == "paper_execution_mode_invalid");
    }

    [Fact]
    public async Task OnPostRunLive_WithoutPromotedStrategyFailsClosed()
    {
        var repoRoot = FindRepositoryRoot();
        var catalog = CreateCatalog(repoRoot);
        var wishlist = new Wishlist
        {
            Id = Guid.NewGuid(),
            Name = "paper-validation",
            IsDefault = true,
            Items =
            [
                new WishlistItem
                {
                    Id = Guid.NewGuid(),
                    Ticker = "MU",
                    Active = true
                }
            ]
        };
        var model = CreateModel(repoRoot, catalog, [wishlist]);
        var context = new DefaultHttpContext();
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["RunName"] = $"paper-invalid-extended-{Guid.NewGuid():N}",
            ["BaseConfigPath"] = Path.Combine(repoRoot, "configs", "paper", "alpaca-paper.yaml"),
            ["SelectedStrategyPath"] = Path.Combine(repoRoot, "configs", "strategies", "minervini-trend-template-vcp.v4-trend-rider.yaml"),
            ["PaperExecutionMode"] = "shadow",
            ["WishlistId"] = wishlist.Id.ToString(),
            ["OrderExpiration"] = "gtc",
            ["EntryOrderType"] = "market",
            ["AllowExtendedHoursTrading"] = "true",
            ["NewsEnabled"] = "false",
            ["ScreenerFilter"] = String.Empty
        });
        model.PageContext = new PageContext { HttpContext = context };

        var result = await model.OnPostRunLive(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.Contains(
            model.ModelState.Values.SelectMany(value => value.Errors),
            error => error.ErrorMessage.StartsWith(
                "no_paper_shadow_strategy",
                StringComparison.Ordinal));
        Assert.Equal("gtc", model.OrderExpiration);
        Assert.Equal("market", model.EntryOrderType);
        Assert.True(model.AllowExtendedHoursTrading);
    }

    [Fact]
    public async Task OnPostCreateExperiment_WithEditedParameters_PublishesDerivedAuthorizedArtifact()
    {
        var repoRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(
            repoRoot,
            "configs",
            "strategies",
            "minervini-trend-template-vcp.v4-trend-rider.yaml");
        var artifactCatalog = CreateStrategyArtifactCatalog(repoRoot);
        var artifactStoreRoot = Path.Combine(
            Path.GetTempPath(),
            "trading-flow-paper-page-derived-experiment",
            Guid.NewGuid().ToString("N"));
        var artifactStore = new StrategyExperimentArtifactStore(
            artifactStoreRoot,
            artifactCatalog,
            new SimpleYamlReader(),
            AtomicFileArtifactWriter.Instance);
        var authorizations = new StrategyAuthorizationTestRegistry();
        var writer = new RunConfigWriter(
            new ProjectPaths(repoRoot),
            new SimpleYamlReader(),
            AtomicFileArtifactWriter.Instance,
            artifactCatalog,
            artifactStore,
            authorizations);
        var model = CreateModel(repoRoot, CreateCatalog(repoRoot), configWriter: writer);
        model.PaperExecutionMode = "experiment";
        model.ExperimentSourceStrategyPath = sourcePath;
        model.PageContext = new PageContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "operator@example.test")],
                    "unit-test"))
            }
        };

        try
        {
            await model.OnGetAsync(null, null, null, CancellationToken.None);
            var sourceVolume = model.ExperimentParameters.MinVolumeSpike;
            model.ExperimentParameters.ApplyOverrides = true;
            model.ExperimentParameters.MinVolumeSpike = sourceVolume + 0.25m;
            model.ExperimentSemanticVersion = "1.1.0-paper";

            var result = await model.OnPostCreateExperimentAsync(CancellationToken.None);

            Assert.IsType<RedirectToPageResult>(result);
            var artifact = Assert.Single(artifactStore.List());
            Assert.Equal("1.1.0-paper", artifact.Identity.SemanticVersion);
            Assert.Equal(sourceVolume + 0.25m, artifact.ResolvedStrategy.EntryRules.MinVolumeSpike);
            Assert.Equal(
                artifact.Identity,
                Assert.Single(await authorizations.GetActiveGrantsAsync()).Identity);
        }
        finally
        {
            if (Directory.Exists(artifactStoreRoot))
            {
                Directory.Delete(artifactStoreRoot, recursive: true);
            }
        }
    }

    private static PaperModel CreateModel(
        string repoRoot,
        ConfigCatalogService catalog,
        IReadOnlyList<Wishlist>? wishlists = null,
        RunConfigWriter? configWriter = null)
    {
        var configuration = new ConfigurationBuilder().Build();
        var credentialProvider = new AlpacaCredentialProvider(configuration);
        var paths = new ProjectPaths(repoRoot);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var paperJobs = new PaperJobService(
            new SimpleYamlReader(),
            scopeFactory.Object,
            credentialProvider,
            paths,
            configCatalog: catalog,
            durableJobs: new Mock<TradingFlow.Domain.Jobs.IDurableJobRepository>().Object);
        var wishlistRepository = new Mock<IWishlistRepository>();
        wishlistRepository
            .Setup(repository => repository.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(wishlists ?? Array.Empty<Wishlist>());
        return new PaperModel(
            catalog,
            new PaperEnvironmentService(catalog, credentialProvider, new Mock<IRawArchiveWriter>().Object),
            configWriter ?? new RunConfigWriter(
                paths,
                new SimpleYamlReader(),
                AtomicFileArtifactWriter.Instance,
                CreateStrategyArtifactCatalog(repoRoot),
                new StrategyExperimentArtifactStore(
                    Path.Combine(Path.GetTempPath(), "trading-flow-empty-experiments", Guid.NewGuid().ToString("N")),
                    CreateStrategyArtifactCatalog(repoRoot),
                    new SimpleYamlReader(),
                    AtomicFileArtifactWriter.Instance),
                new StrategyAuthorizationTestRegistry()),
            paperJobs,
            wishlistRepository.Object,
            CreateScreenerPresetService(),
            CreateUniverseResolver(),
            NullLogger<PaperModel>.Instance);
    }

    private static IPaperRunUniverseSnapshotResolver CreateUniverseResolver()
    {
        var resolver = new Mock<IPaperRunUniverseSnapshotResolver>();
        resolver
            .Setup(service => service.ResolveAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<bool>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<Guid?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                IEnumerable<string> tickers,
                bool includeTickers,
                string source,
                string? query,
                bool includeScreener,
                Guid? _,
                string _,
                CancellationToken _) => new PaperRunUniverseSnapshot(
                    includeTickers ? tickers.ToArray() : ["SCREEN"],
                    includeTickers ? tickers.ToArray() : [],
                    includeScreener ? ["SCREEN"] : [],
                    includeScreener ? $"{source}+finviz" : source,
                    query,
                    DateTimeOffset.UtcNow));
        return resolver.Object;
    }

    /// <summary>
    /// A preset service over a throwaway in-memory journal. The paper screen
    /// reads saved screens on every load, so the real query path runs here
    /// rather than being mocked away.
    /// </summary>
    private static ScreenerPresetService CreateScreenerPresetService()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<TradingFlowDbContext>()
            .UseSqlite(connection)
            .Options;
        using (var database = new TradingFlowDbContext(options))
        {
            database.Database.EnsureCreated();
        }

        return new ScreenerPresetService(new PaperTestDbContextFactory(options), TimeProvider.System);
    }

    private sealed class PaperTestDbContextFactory(DbContextOptions<TradingFlowDbContext> options)
        : IDbContextFactory<TradingFlowDbContext>
    {
        public TradingFlowDbContext CreateDbContext() => new(options);
    }

    private static ConfigCatalogService CreateCatalog(string repoRoot)
    {
        var reader = new SimpleYamlReader();
        return new ConfigCatalogService(
            new ProjectPaths(repoRoot),
            reader,
            CreateStrategyArtifactCatalog(repoRoot, reader),
            new StrategyExperimentArtifactStore(
                Path.Combine(Path.GetTempPath(), "trading-flow-empty-experiments", Guid.NewGuid().ToString("N")),
                CreateStrategyArtifactCatalog(repoRoot, reader),
                reader,
                AtomicFileArtifactWriter.Instance),
            new StrategyAuthorizationTestRegistry());
    }

    private static StrategyArtifactCatalog CreateStrategyArtifactCatalog(
        string repoRoot,
        SimpleYamlReader? reader = null) =>
        new(
            repoRoot,
            Path.Combine(repoRoot, "configs", "strategy-catalog.json"),
            reader ?? new SimpleYamlReader());

    private static string FindRepositoryRoot()
    {
        return TestRepository.FindRoot();
    }
}


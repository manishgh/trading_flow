using System.Text.RegularExpressions;
using TradingFlow.Engine.Configuration;
using TradingFlow.Domain.Strategies;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class ConfigCatalogServiceTests
{
    [Fact]
    // Keep the normal UI/API selector bound to the exact reviewed Research identities and source files.
    public async Task GetStrategies_ReturnsCurrentResearchBootstrapSet()
    {
        var repoRoot = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new ConfigCatalogService(
            new ProjectPaths(repoRoot),
            reader,
            CreateStrategyCatalog(repoRoot, reader),
            CreateExperimentStore(repoRoot, reader),
            new StrategyAuthorizationTestRegistry());

        var strategies = await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest);

        // 19 November 2026: all ten approved swing research artifacts (five long + five short mirrors, including
        // Experiment V2-01 MTR-14 treatments for both 21-EMA strategies) are enabled for research backtests. The
        // same-day opening-range versions stay set aside, the ETF trend core and its control stay blocked (index and
        // sector funds only judge the trend), and ETF rotation stays parked.
        Assert.Equal(
            new[]
            {
                "technical.bollinger-squeeze-breakdown@1.0.0",
                "technical.bollinger-squeeze-breakout@1.0.0",
                "technical.ema21-rejection-short@1.0.0",
                "technical.ema21-rejection-short@1.1.0",
                "technical.ema21-trend-pullback@1.0.0",
                "technical.ema21-trend-pullback@1.1.0",
                "technical.news-orb-breakdown@1.0.0",
                "technical.news-orb-swing@1.0.0",
                "technical.parabolic-exhaustion-short@1.0.0",
                "technical.rubber-band-reclaim@1.0.0"
            },
            strategies
                .Where(item => !StrategyArtifactCatalogTests.IsTemporaryVariant(item.Identity))
                .Select(item => $"{item.Identity.StrategyId}@{item.Identity.SemanticVersion}")
                .Order(StringComparer.Ordinal));
        Assert.Empty(catalog.GetDisabledResearchStrategies());
    }

    [Fact]
    public async Task PaperCatalogs_StartEmptyAndNeverFallBackToResearch()
    {
        var repoRoot = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new ConfigCatalogService(
            new ProjectPaths(repoRoot),
            reader,
            CreateStrategyCatalog(repoRoot, reader),
            CreateExperimentStore(repoRoot, reader),
            new StrategyAuthorizationTestRegistry());

        Assert.Empty(await catalog.GetStrategiesAsync(StrategySelectionMode.RunPaperExperiment));
        Assert.Empty(await catalog.GetStrategiesAsync(StrategySelectionMode.RunPaperShadow));
        Assert.Empty(await catalog.GetStrategiesAsync(StrategySelectionMode.RunLive));
    }

    [Fact]
    public void GetConfig_ParsesCanonicalPaperProfilesAgainstLifecycleCatalog()
    {
        var repoRoot = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        var catalog = new ConfigCatalogService(
            new ProjectPaths(repoRoot),
            reader,
            CreateStrategyCatalog(repoRoot, reader),
            CreateExperimentStore(repoRoot, reader),
            new StrategyAuthorizationTestRegistry());

        var swing = catalog.GetConfig(Path.Combine(repoRoot, "configs", "paper", "alpaca-paper.yaml"));

        Assert.Empty(swing.Strategies);
    }

    [Fact]
    // A malformed required scalar excludes only that profile; selectable strategies are resolved separately from profile listing.
    public void GetBacktestConfigs_SkipsMalformedRunConfigWithoutHidingValidProfile()
    {
        var repoRoot = TestRepository.FindRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "trading-flow-catalog-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "configs", "backtest"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "configs", "strategies"));

        try
        {
            var sourceConfig = Path.Combine(
                repoRoot,
                "configs",
                "backtest",
                "swing-backtest-profile.yaml");
            var yaml = File.ReadAllText(sourceConfig);
            File.WriteAllText(Path.Combine(tempRoot, "configs", "backtest", "valid-ui-profile.yaml"), yaml);
            var malformedYaml = Regex.Replace(yaml, @"(?m)^run_name:.*$", "run_name:");
            Assert.NotEqual(yaml, malformedYaml);

            File.WriteAllText(Path.Combine(tempRoot, "configs", "backtest", "malformed-ui-profile.yaml"), malformedYaml);

            var reader = new SimpleYamlReader();
            var catalog = new ConfigCatalogService(
                new ProjectPaths(tempRoot),
                reader,
                CreateStrategyCatalog(repoRoot, reader),
                CreateExperimentStore(repoRoot, reader),
                new StrategyAuthorizationTestRegistry());

            var configs = catalog.GetBacktestConfigs();

            var profile = Assert.Single(configs);
            Assert.Equal("valid-ui-profile.yaml", profile.FileName);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    /// <summary>Research accepts the registered day artifact while operational candidate and trading paths retain their daily restriction.</summary>
    public async Task CandidateStrategyResolver_RequiresExactCatalogIdentityAndModeAuthorization()
    {
        var root = TestRepository.FindRoot();
        var reader = new SimpleYamlReader();
        // Re-enable the set-aside fixture strategies in a temp copy so the resolver is exercised on both day (ORB) and swing (ETF) identities.
        using var folder = new TemporaryFolder();
        var artifacts = RetiredStrategyFixtures.SetAsideEnabledCatalog(folder.Path, reader);
        var store = new StrategyExperimentArtifactStore(Path.Combine(folder.Path, "experiments"), artifacts, reader, TradingFlow.Engine.Storage.AtomicFileArtifactWriter.Instance);
        var catalog = new ConfigCatalogService(new ProjectPaths(RetiredStrategyFixtures.Root), reader, artifacts, store, new StrategyAuthorizationTestRegistry());
        var resolver = new StrategyIdentityResolver(catalog);
        var strategies = await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest);
        Assert.NotEmpty(strategies);
        foreach (var option in strategies)
        {
            var identity = new TradingFlow.Contracts.V1.StrategyReference(
                option.Identity.StrategyId, option.Identity.SemanticVersion, option.Identity.ContentSha256);
            await resolver.RequireBacktestStrategyAsync(identity);
            if (StrategyHorizonResolver.Resolve(option.Definition) == "day")
                await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.RequireSwingStrategyAsync(identity, "backtest"));
            else await resolver.RequireSwingStrategyAsync(identity, "backtest");
            await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.RequireSwingStrategyAsync(identity with { ContentSha256 = new string('0', 64) }, "backtest"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.RequireSwingStrategyAsync(identity with { SemanticVersion = "unregistered" }, "backtest"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.RequireSwingStrategyAsync(identity, "paper_experiment"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.RequireSwingStrategyAsync(identity, "paper_shadow"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.RequireSwingStrategyAsync(identity, "live"));
        }
    }
    private static StrategyArtifactCatalog CreateStrategyCatalog(string root, SimpleYamlReader reader) =>
        new(root, Path.Combine(root, "configs", "strategy-catalog.json"), reader);

    private static StrategyExperimentArtifactStore CreateExperimentStore(string root, SimpleYamlReader reader)
    {
        var catalog = CreateStrategyCatalog(root, reader);
        return new StrategyExperimentArtifactStore(
            Path.Combine(Path.GetTempPath(), "trading-flow-empty-experiments", Guid.NewGuid().ToString("N")),
            catalog,
            reader,
            TradingFlow.Engine.Storage.AtomicFileArtifactWriter.Instance);
    }
}

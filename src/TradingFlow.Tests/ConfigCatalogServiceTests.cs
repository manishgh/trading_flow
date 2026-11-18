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

        // 18 November 2026: every registered strategy is switched off. The same-day opening-range versions were set aside, the
        // ETF trend core and its control blocked (index and sector funds only judge the trend), ETF rotation stays parked,
        // and the Bollinger squeeze is registered but not yet runnable until its remaining approved rules are built.
        Assert.Empty(strategies);
        var disabled = catalog.GetDisabledResearchStrategies()
            .ToDictionary(item => $"{item.Artifact.Identity.StrategyId}@{item.Artifact.Identity.SemanticVersion}", item => item.Reason);
        Assert.Equal(new[] { "etf.buy-and-hold-control@1.0.0", "etf.rotation-12-1@1.0.0", "etf.trend-10m@1.0.0",
            "technical.bollinger-squeeze-breakout@1.0.0", "technical.opening-range-breakout@1.2.0",
            "technical.opening-range-breakout@2.0.0", "technical.opening-range-breakout@2.1.0" }, disabled.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("parked for later standalone research", disabled["etf.rotation-12-1@1.0.0"], StringComparison.Ordinal);
        Assert.Contains("Set aside 18 November 2026", disabled["technical.opening-range-breakout@2.1.0"], StringComparison.Ordinal);
        Assert.Contains("Not yet runnable", disabled["technical.bollinger-squeeze-breakout@1.0.0"], StringComparison.Ordinal);
        Assert.Contains("only to judge the market and sector trend", disabled["etf.trend-10m@1.0.0"], StringComparison.Ordinal);
        Assert.Contains("only to judge the market and sector trend", disabled["etf.buy-and-hold-control@1.0.0"], StringComparison.Ordinal);
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
        // Every operator strategy is switched off (18 Nov 2026); a test-owned catalog copy re-enables the set-aside ones so
        // the resolver is exercised on both day (opening-range) and swing (ETF) identities.
        using var folder = new TemporaryFolder();
        var artifacts = new StrategyArtifactCatalog(root, TestRepository.SetAsideStrategiesEnabledCatalog(folder.Path), reader);
        var store = new StrategyExperimentArtifactStore(Path.Combine(folder.Path, "experiments"), artifacts, reader, TradingFlow.Engine.Storage.AtomicFileArtifactWriter.Instance);
        var catalog = new ConfigCatalogService(new ProjectPaths(root), reader, artifacts, store, new StrategyAuthorizationTestRegistry());
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

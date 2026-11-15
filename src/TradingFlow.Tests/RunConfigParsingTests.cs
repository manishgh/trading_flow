using System.Text.Json;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Research;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class RunConfigParsingTests
{
    // Both execution writers and the experiment store must retain the exact policy used to compute each artifact's identity.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NextOpenEntryWindow_SurvivesGeneratedRunAndExperimentIdentities(bool optedIn)
    {
        var repository = FindRepositoryRoot();
        var root = Path.Combine(Path.GetTempPath(), $"tradingflow-entry-window-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var reader = new SimpleYamlReader();
            var window = optedIn ? new EntryWindowRules(EntryWindowRules.NextRegularOpen, 1, 420) : null;
            var (catalog, sourcePath) = CreateEntryWindowCatalog(root, repository, reader, window);
            var source = catalog.RequireSourcePath(sourcePath);
            Assert.Equal(window, source.ResolvedStrategy.Execution.EntryWindow);

            var experimentsRoot = Path.Combine(root, "experiments");
            var experiments = new StrategyExperimentArtifactStore(experimentsRoot, catalog, reader, AtomicFileArtifactWriter.Instance);
            var writer = new RunConfigWriter(new ProjectPaths(root), reader, AtomicFileArtifactWriter.Instance, catalog,
                experiments, new StrategyAuthorizationTestRegistry());
            var validator = new StrategyRunArtifactValidator(reader, catalog);
            var backtestProfile = Path.Combine(repository, "configs", "backtest", "swing-backtest-profile.yaml");
            var backtestPath = writer.WriteBacktestConfig(new BacktestRunRequest(backtestProfile, "entry_window_backtest",
                180, null, null, ["MSFT"], [sourcePath], 10_000m, 1m, 25m, 4, "use_cache", [],
                ExecutionReplayPolicyId: ExecutionReplayPolicy.DailyRthFiveMinuteV1));
            var backtestStrategyPath = Assert.Single(reader.ReadBacktestRun(backtestPath).Strategies);
            var backtest = validator.ReadAndValidateRuntimeArtifact(backtestStrategyPath, StrategySelectionMode.Backtest);
            Assert.Equal(source.Identity, backtest.Identity);
            Assert.Equal(window, backtest.Definition.Execution.EntryWindow);

            const string experimentVersion = "1.0.1";
            var experiment = writer.RegisterPaperExperiment(sourcePath, experimentVersion, "test-fixture", Guid.NewGuid(),
                new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
            var expectedHash = EvidenceCanonicalJson.ComputeSha256(new CanonicalStrategyDocument(source.Identity.StrategyId,
                experimentVersion, source.ResolvedStrategy, source.AdmissionProfile));
            Assert.Equal(expectedHash, experiment.Identity.ContentSha256);
            Assert.Equal(source.Identity, experiment.DerivedFromIdentity);
            var reopened = Assert.Single(new StrategyExperimentArtifactStore(experimentsRoot, catalog, reader,
                AtomicFileArtifactWriter.Instance).List());
            Assert.Equal(experiment.Identity, reopened.Identity);
            Assert.Equal(window, reopened.ResolvedStrategy.Execution.EntryWindow);

            var paperProfile = Path.Combine(repository, "configs", "paper", "alpaca-paper.yaml");
            var paperPath = writer.SaveTempConfig(paperProfile, ["MSFT"], experiment, StrategySelectionMode.RunPaperExperiment,
                runName: "entry_window_paper");
            var paperStrategyPath = Assert.Single(reader.ReadBacktestRun(paperPath).Strategies);
            var paper = validator.ReadAndValidatePaperArtifact(paperStrategyPath);
            Assert.Equal(experiment.Identity, paper.Manifest.Identity);
            Assert.Equal(window, paper.Definition.Execution.EntryWindow);

            if (!optedIn)
            {
                Assert.All(new[] { backtestStrategyPath, paperStrategyPath, reopened.SourcePath }, path =>
                    Assert.DoesNotContain("entry_window:", File.ReadAllText(path), StringComparison.Ordinal));
            }
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(root), ignoreCase: true);
            Directory.Delete(root, recursive: true);
        }
    }

    // Hand-write the opt-in on a test-owned fixture so the input does not depend on the serializer under test.
    private static (StrategyArtifactCatalog Catalog, string SourcePath) CreateEntryWindowCatalog(
        string root, string repository, SimpleYamlReader reader, EntryWindowRules? window)
    {
        const string sourceRelativePath = "configs/backtest/strategies/daily-swing.yaml";
        var sourcePath = Path.Combine(root, sourceRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        var yaml = File.ReadAllText(Path.Combine(repository, "src", "TradingFlow.Tests", "Fixtures", "Strategies",
            "daily-trailing-swing.fixture.yaml")).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (window is not null)
        {
            yaml = yaml.Replace("execution:\n",
                $"execution:\n  entry_window:\n    policy: {window.Policy}\n    version: {window.Version}\n    grace_seconds: {window.GraceSeconds}\n",
                StringComparison.Ordinal);
        }
        File.WriteAllText(sourcePath, yaml);
        var manifestPath = Path.Combine(root, "configs", "strategy-catalog.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            admissionProfiles = new[]
            {
                new
                {
                    profileId = "fixture", profileVersion = "1.0.0", parserProfile = "simple-yaml-strict-v1",
                    standardIndicatorLibrary = "Skender.Stock.Indicators", standardIndicatorLibraryVersion = "2.7.1",
                    derivedIndicatorProfile = "TradingFlow.DerivedIndicators", derivedIndicatorProfileVersion = "1.0.0",
                    riskPolicy = "account-risk-budget-v1", executionTimingPolicy = "completed-bar-next-executable-bar-v1"
                }
            },
            strategies = new[]
            {
                new
                {
                    sourcePath = sourceRelativePath, strategyId = "test.daily-trailing-swing", semanticVersion = "1.0.0",
                    lifecycle = "research", admissionProfileId = "fixture"
                }
            }
        }));
        return (new StrategyArtifactCatalog(root, manifestPath, reader), sourcePath);
    }

    [Fact]
    public void OpeningGapPolicy_SurvivesGeneratedRunFiles()
    {
        var reader = new SimpleYamlReader();
        var original = reader.ReadStrategy(RetiredStrategyFixtures.Strategy("swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml"));
        var path = Path.Combine(Path.GetTempPath(), $"opening-gap-roundtrip-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(path, RunConfigWriter.SerializeStrategyYaml(original));
            Assert.Equal(original.Execution, reader.ReadStrategy(path).Execution);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpeningGapPolicy_IsOptInAndRejectsUnknownValues()
    {
        var root = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategies = Path.Combine(RetiredStrategyFixtures.Root, "configs", "backtest", "strategies");
        var kept = Path.Combine(strategies, "swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml");
        Assert.Equal(ExecutionRules.KeepAndExitNextOpen, reader.ReadStrategy(kept).Execution.OpeningGapPolicy);
        Assert.Null(reader.ReadStrategy(Path.Combine(strategies, "recovery-sma5-news-avoidance.bt-v1.yaml")).Execution.OpeningGapPolicy);

        var invalid = Path.Combine(Path.GetTempPath(), $"opening-gap-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(invalid, File.ReadAllText(kept).Replace(ExecutionRules.KeepAndExitNextOpen, "guess"));
            Assert.Throws<InvalidOperationException>(() => reader.ReadStrategy(invalid));
        }
        finally
        {
            File.Delete(invalid);
        }
    }

    [Fact]
    public void BacktestResearchStrategies_UseDailySetupsUnlessDeclaredSameDay()
    {
        var root = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var paths = Directory.GetFiles(
            Path.Combine(root, "configs", "backtest", "strategies"),
            "*.yaml",
            SearchOption.TopDirectoryOnly);

        Assert.NotEmpty(paths);
        foreach (var path in paths)
        {
            var strategy = reader.ReadStrategy(path);
            if (StrategyHorizonResolver.Resolve(strategy) == "swing")
                Assert.Equal("1d", strategy.Timeframe, ignoreCase: true);
            else
                Assert.NotEqual("1d", strategy.Timeframe, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("src/TradingFlow.Tests/Fixtures/Strategies/daily-trailing-swing.fixture.yaml")]
    [InlineData("src/TradingFlow.Tests/Fixtures/RetiredStrategies/configs/backtest/strategies/swing-connors-rsi2-sma5-risk-managed.bt-v7.yaml")]
    [InlineData("src/TradingFlow.Tests/Fixtures/RetiredStrategies/configs/backtest/strategies/recovery-sma5-news-avoidance.bt-v1.yaml")]
    public void StrategySerialization_RoundTripsSharedSwingBrain(string strategyFile)
    {
        var root = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var original = reader.ReadStrategy(Path.Combine(root, strategyFile));
        var tempPath = Path.Combine(Path.GetTempPath(), $"swing-roundtrip-{Guid.NewGuid():N}.yaml");

        File.WriteAllText(tempPath, RunConfigWriter.SerializeStrategyYaml(original));
        try
        {
            var roundTripped = reader.ReadStrategy(tempPath);
            Assert.Equal(original.EntryRules, roundTripped.EntryRules);
            Assert.Equal(original.ExitRules, roundTripped.ExitRules);
            Assert.Equal(original.Execution, roundTripped.Execution);
            Assert.Equal(original.Regime, roundTripped.Regime);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void SwingBacktestProfile_UsesWishlistUniverseAndDailyStrategies()
    {
        var root = FindRepositoryRoot();
        var run = new SimpleYamlReader().ReadBacktestRun(
            Path.Combine(root, "configs", "backtest", "swing-backtest-profile.yaml"));

        // Its daily swing strategies were retired on 13 November 2026; a run names its strategies when planned.
        Assert.Empty(run.Strategies);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TradingFlow.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate TradingFlow.sln.");
    }
}

using Moq;
using TradingFlow.Contracts.V1;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Services;

namespace TradingFlow.Tests;

public sealed class WorkflowConfigurationTests
{
    [Fact]
    public async Task HistoricalPaperView_UsesDurableAttemptWhenGeneratedConfigIsMissing()
    {
        var repo = TestRepository.FindRoot();
        var temporary = Path.Combine(Path.GetTempPath(), "tradingflow-paper-view-" + Guid.NewGuid().ToString("N"));
        var artifact = Path.Combine(temporary, "live", "actual_run");
        Directory.CreateDirectory(Path.Combine(artifact, "MSFT"));
        try
        {
            var reader = new SimpleYamlReader();
            var artifacts = new StrategyArtifactCatalog(repo, Path.Combine(repo, "configs", "strategy-catalog.json"), reader);
            var store = new StrategyExperimentArtifactStore(Path.Combine(temporary, "experiments"), artifacts, reader, AtomicFileArtifactWriter.Instance);
            var catalog = new ConfigCatalogService(new ProjectPaths(repo), reader, artifacts, store, new StrategyAuthorizationTestRegistry());
            var audits = new Moq.Mock<TradingFlow.Domain.Audit.IDecisionAuditRepository>(Moq.MockBehavior.Strict);
            audits.Setup(item => item.GetAuditPageAsync("actual_run", null, null, 0, 50, Moq.It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TradingFlow.Domain.Audit.DecisionAuditPage(
                    [new() { RunName = "actual_run", Ticker = "MSFT" }], 1));
            var job = new TradingFlow.Web.Models.BacktestJobSnapshot(Guid.NewGuid(), "display_name",
                Path.Combine(temporary, "removed.yaml"), "complete", DateTimeOffset.UtcNow,
                null, null, null, "complete", 1, 1, [], null, ArtifactDirectory: artifact);
            await File.WriteAllTextAsync(Path.Combine(artifact, "MSFT", "sample_chart.json"),
                """{"Ticker":"MSFT","Close":123.5,"Timeframe":"1d"}""");
            var view = new PaperRunViewService(new ProjectPaths(repo), catalog, audits.Object);
            Assert.Equal(123.5m, Assert.Single(view.LoadChartData(job)).Close);
            Assert.Equal("MSFT", Assert.Single(await view.LoadRecentAuditsAsync(job, default)).Ticker);
            Assert.Empty(view.LoadChartData(job with { ArtifactDirectory = null }));
            audits.VerifyAll();
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(temporary), ignoreCase: true);
            Directory.Delete(temporary, recursive: true);
        }
    }

    [Fact]
    // Both modes must preserve their selected profile and the frozen mixed-source membership.
    public async Task RealConfigurationAdapter_PreservesFixedWindowAndMixedSourceMembership()
    {
        var repo = TestRepository.FindRoot();
        var temporary = Path.Combine(Path.GetTempPath(), "tradingflow-workflow-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var reader = new SimpleYamlReader();
            var artifacts = new StrategyArtifactCatalog(repo, Path.Combine(repo, "configs", "strategy-catalog.json"), reader);
            var store = new StrategyExperimentArtifactStore(Path.Combine(temporary, "experiments"), artifacts, reader, AtomicFileArtifactWriter.Instance);
            var authorization = new StrategyAuthorizationTestRegistry();
            var writer = new RunConfigWriter(new ProjectPaths(temporary), reader, AtomicFileArtifactWriter.Instance, artifacts, store, authorization);
            var catalog = new ConfigCatalogService(new ProjectPaths(repo), reader, artifacts, store, authorization);
            var limits = new Mock<TradingFlow.Domain.Execution.IOperationalTradingLimitStore>();
            limits.Setup(item => item.GetAsync("paper_experiment", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TradingFlow.Domain.Execution.OperationalTradingLimits("paper", 5, 5, 0, null));
            var adapter = new WorkflowRunConfiguration(catalog, writer, reader, limits.Object);
            var strategy = (await catalog.GetStrategiesAsync(StrategySelectionMode.Backtest)).First(item => item.Definition.Timeframe == "1d");
            var identity = new StrategyReference(strategy.Identity.StrategyId, strategy.Identity.SemanticVersion, strategy.Identity.ContentSha256);
            var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
            var preview = new UniversePreviewResponse(ContractVersions.VersionOne, Guid.NewGuid(), "swing", now, now.AddMinutes(5),
                new string('a', 64), [
                    new("MSFT", "watching", [new("wishlist", "watch", now, now.AddMinutes(5), new string('b',64), false)]),
                    new("AAPL", "watching", [new("finviz", "screen", now, now.AddMinutes(5), new string('c',64), false)])
                ], [], now);
            var backtest = new StartBacktestRequest(ContractVersions.VersionOne, "fixed_window", preview.UniverseSnapshotId,
                [identity], now.AddMonths(-6), now.AddDays(-1), 10000, 1, 25, 4, "use_cache", "full", "backtest-fixture",
                catalog.GetBacktestConfigs().First().FileName);
            var files = await adapter.WriteAsync(backtest, preview, "fixed_window", default);
            var parsed = reader.ReadBacktestRun(files[0]);
            Assert.Equal(backtest.TestWindowStartUtc, parsed.TimeWindow.Start);
            Assert.Equal(backtest.TestWindowEndUtc, parsed.TimeWindow.End);
            Assert.Equal(new[] { "MSFT", "AAPL" }, parsed.Tickers);
            Assert.Equal("finviz+wishlist", parsed.Universe!.ResolvedSource);
            Assert.Equal(now, parsed.Universe.ResolvedAtUtc);
            Assert.Contains(preview.UniverseSnapshotId.ToString(), await File.ReadAllTextAsync(files[0]));
            var registered = writer.RegisterPaperExperiment(strategy.Path, "1.0.0", "fixture", Guid.NewGuid(), now);
            var paper = new StartPaperRunRequest(ContractVersions.VersionOne, "mixed_sources", preview.UniverseSnapshotId,
                new(registered.Identity.StrategyId, registered.Identity.SemanticVersion, registered.Identity.ContentSha256),
                false, false, "limit", "day", "strategy_validated", "paper-fixture", "paper_experiment",
                catalog.GetPaperConfigs().First().FileName);
            files = await adapter.WriteAsync(paper, preview, "mixed_sources", default);
            parsed = reader.ReadBacktestRun(files[0]);
            Assert.Equal(new[] { "MSFT" }, parsed.Discovery!.WishlistTickers);
            Assert.Equal(5, parsed.Portfolio.MaxConcurrentPositions);
            Assert.Equal(new[] { "AAPL" }, parsed.Discovery.FinvizTickers);
            Assert.Null(parsed.Discovery.WishlistId);
            Assert.True(String.IsNullOrEmpty(parsed.Discovery.FinvizQuery));
            Assert.Contains(preview.UniverseSnapshotId.ToString(), await File.ReadAllTextAsync(files[0]));
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(temporary), ignoreCase: true);
            Directory.Delete(temporary, recursive: true);
        }
    }
}

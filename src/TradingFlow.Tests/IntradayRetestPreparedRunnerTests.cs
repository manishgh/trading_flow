using TradingFlow.Backtesting;
using TradingFlow.Backtesting.Artifacts;
using TradingFlow.Domain.Backtesting;
using TradingFlow.Domain.Strategies;
using TradingFlow.Engine.Configuration;
using TradingFlow.Web.Services;
using System.Text.Json;

namespace TradingFlow.Tests;

public sealed partial class IntradayBacktestIntegrationTests
{
    // Ordinary prepared history, registry, shared kernel and candle portfolio execution must produce a real later-opening retest fill.
    [Fact]
    public async Task RetestEntry_PreparedRunnerPublishesFilledLongWithCausalEvidenceAndFlatBook()
    {
        var root = Path.Combine(Path.GetTempPath(), "tf-retest-prepared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = TestRepository.FindRoot();
            var reader = new SimpleYamlReader();
            var catalog = new StrategyArtifactCatalog(repo, Path.Combine(repo, "configs", "strategy-catalog.json"), reader);
            var strategyPath = Path.Combine(repo, "configs", "backtest", "strategies", "zone-breakout-retest.bt-v1.yaml");
            var runtime = new StrategyRunArtifactValidator(reader, catalog).ReadAndValidateRuntimeArtifact(strategyPath, StrategySelectionMode.Backtest);
            var profile = reader.ReadBacktestRun(Path.Combine(repo, "configs", "backtest", "swing-backtest-profile.yaml"));
            var start = DateTimeOffset.Parse("2026-10-01T13:30:00Z");
            var end = DateTimeOffset.Parse("2026-10-01T20:00:00Z");
            var run = BacktestExecutionProfileResolver.Resolve(profile, [runtime.Definition]) with {
                RunName = "synthetic-retest-prepared", Provider = "synthetic-intraday", Tickers = ["ABC"], Strategies = [strategyPath],
                TimeWindow = new("fixed", 1, start, end, 40),
                RawRoot = Path.Combine(root, "raw"), NormalizedRoot = Path.Combine(root, "normalized"), ResultsRoot = Path.Combine(root, "results"),
                CachePolicy = "use_cache", Portfolio = profile.Portfolio with { MaxConcurrentPositions = 1 }, Artifacts = new("full"),
                Universe = UniverseConfig.StaticUniverse with { Mode = UniverseConfig.ResolvedSnapshotMode,
                    ResolvedSource = "synthetic_fixture", SelectedSourceTickers = ["ABC"], ResolvedAtUtc = start } };
            var provider = new SyntheticIntradayProvider(run.RawRoot, true, false, "raw", retestEntry: true);
            var request = new PreparedMarketDataRequest(1, "synthetic-retest-prepared", end,
                [new(run.RunName, run with { Strategies = [] }, [runtime.Definition])]);
            var reference = await PreparedMarketDataService.WriteRequestAsync(request, Path.Combine(root, "admission", "request.json"), default);
            var runner = new BacktestRunner(reader, catalog, marketDataProviderFactory: _ => provider);
            var result = await runner.RunAsync(run, [runtime.Definition], DateTimeOffset.UtcNow,
                Path.Combine(run.ResultsRoot, "result.json"), default, preparedDataReference: reference);
            Assert.True(result.EconomicResultsComplete);
            var trade = Assert.Single(result.CompletedTrades, item => item.Direction == "long");
            Assert.Equal(start.AddMinutes(35), trade.EntryTimestamp);
            Assert.True(trade.Fees > 0);
            Assert.True(trade.ShareQuantity > 0);
            Assert.Equal(start.AddMinutes(385), trade.ExitTimestamp);
            Assert.Equal("session_deadline", trade.ExitReason);
            var detail = Assert.Single(result.ZoneDecisions!, item => item.CandidateId == trade.CandidateId);
            Assert.Equal("breakout_retest", detail.Pattern);
            Assert.NotNull(detail.Retest);
            Assert.Equal(start.AddMinutes(30), detail.FlippedAtUtc);
            Assert.Equal(start.AddMinutes(30), detail.ConfirmationBarStartUtc);
            Assert.Equal(start.AddMinutes(35), detail.ConfirmationCompletedAtUtc);
            Assert.True(detail.Retest!.ArmTransition.CanArmRetest);
            Assert.Equal(detail.ZoneVersion, detail.Retest.ArmTransition.After.Version);
            Assert.True(detail.Retest.ConfirmationSnapshotAsOfUtc <= detail.ConfirmationBarStartUtc);
            Assert.Equal(detail.FrozenStopPrice, trade.StopLossPrice);
            Assert.Equal("source_zone", detail.StopBasis);
            Assert.NotNull(detail.TargetPlan?.PlannedQuantity);
            Assert.True(detail.TargetPlan!.EstimatedNetRewardRisk >= 2m);
            Assert.Equal("risk_derived", detail.TargetPlan.TargetOrigin);
            Assert.Empty(result.PortfolioState!.OpenPositions);
            Assert.Empty(result.PortfolioState.PendingOrders);
            Assert.All(result.PortfolioState.Fills, fill => Assert.Equal(new DateOnly(2026, 10, 1), fill.SourceSessionDate));
            await BacktestArtifactIntegrity.VerifyManifestAsync(result.ArtifactManifestPath!);
            var trace = Assert.Single(result.ArtifactReferences!, item => item.Kind == "zone_evaluations");
            var rows = await File.ReadAllLinesAsync(Path.Combine(Path.GetDirectoryName(result.ResultPath)!, trace.Path));
            Assert.Contains(rows, row => row.Contains("breakout_retest_source_consumed_at_emission", StringComparison.Ordinal));
            using var consumedRow = JsonDocument.Parse(rows.First(row => row.Contains("breakout_retest_source_consumed_at_emission", StringComparison.Ordinal)));
            var events = consumedRow.RootElement.GetProperty("Evidence").GetProperty("RetestConsumptions");
            Assert.Contains(events.EnumerateArray(), item => item.GetProperty("EmittedAtUtc").GetDateTimeOffset() == start.AddMinutes(35));
            Assert.Equal(1, provider.BarCaptures);
            Assert.Equal(21 * 78, provider.CapturedBars);
        }
        finally
        {
            // This uniquely named workspace contains only generated synthetic fixtures, never original research/provider data.
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(root), ignoreCase: true);
            Directory.Delete(root, recursive: true);
        }
    }
}

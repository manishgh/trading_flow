using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;
using Xunit;

namespace TradingFlow.Tests;

public sealed class RunConfigParsingTests
{
    private const string RetainedStrategyFile = "intraday-ross-gapgo-bullflag.v2-confirmed-entry.yaml";
    private const string RetainedBacktestFile = "finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry.yaml";
    private const string RetainedSwingBacktestFile = "swing-quality-long-overbought-short-v5-comparison-crdo-msft-app-intc-mu-nvda-180d.yaml";
    private const string SwingQualityLongStrategyFile = "swing-reversal-reclaim-bull-quality-no-news.v1.yaml";
    private const string SwingOverboughtShortStrategyFile = "swing-overbought-rollover-short-no-news.v5.yaml";

    [Fact]
    public void AlpacaPaperConfig_UsesRetainedRossStrategyAndSipFeed()
    {
        var repoRoot = FindRepositoryRoot();
        var configPath = Path.Combine(repoRoot, "configs", "paper", "alpaca-paper.yaml");

        var config = new SimpleYamlReader().ReadBacktestRun(configPath);

        Assert.Equal("alpaca", config.Provider);
        Assert.Equal("sip", config.Providers.Alpaca.DataFeed);
        Assert.True(Path.IsPathRooted(config.RawRoot));
        Assert.True(Path.IsPathRooted(config.NormalizedRoot));
        Assert.True(Path.IsPathRooted(config.ResultsRoot));
        Assert.Contains("5m", config.Intervals);
        Assert.True(config.Execution.ExtendedHours);
        Assert.Equal("summary", config.Artifacts.RetentionMode);
        Assert.Single(config.Strategies);
        Assert.Contains(config.Strategies, path => path.EndsWith(RetainedStrategyFile, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RetainedRossStrategyConfig_ParsesConfirmedEntryRules()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", RetainedStrategyFile));

        Assert.Equal("Intraday Ross Gap-Go Bull Flag V2 Confirmed Entry", strategy.StrategyName);
        Assert.Equal("ross_gap_go_bull_flag", strategy.EntryRules.SetupType);
        Assert.Equal("long", strategy.Direction);
        Assert.Equal("5m", strategy.Timeframe);
        Assert.Equal(2.00m, strategy.EntryRules.MinVolumeSpike);
        Assert.Equal(5.0m, strategy.EntryRules.MinBullFlagPoleMovePct);
        Assert.Equal(2, strategy.EntryRules.BullFlagPullbackMinBars);
        Assert.Equal(5, strategy.EntryRules.BullFlagPullbackMaxBars);
        Assert.Equal(50.0m, strategy.EntryRules.BullFlagMaxDepthPctOfPole);
        Assert.Equal(1.50m, strategy.EntryRules.BullFlagBreakoutVolumeRatioMin);
        Assert.False(strategy.EntryRules.EnablePremarketFilter);
        Assert.True(strategy.EntryRules.EnableEntryBarConfirmation);
        Assert.True(strategy.EntryRules.RejectEntryBarCloseLocationBelowMinimum);
        Assert.True(strategy.EntryRules.RejectEntryBarBreaksSignalMidpoint);
        Assert.Equal(0.55m, strategy.EntryRules.MinEntryBarCloseLocationValue);
        Assert.Equal(4.0m, strategy.EntryRules.MaxVwapExtensionPctForDirectEntry);
        Assert.Equal(0.70m, strategy.EntryRules.ExtendedVwapMinEntryBarCloseLocationValue);
        Assert.Equal(1.05m, strategy.EntryRules.MaxBollingerPositionForDirectEntry);
        Assert.Equal(0.70m, strategy.EntryRules.ExtendedBollingerMinEntryBarCloseLocationValue);
    }

    [Fact]
    public void SwingQualityLongStrategyConfig_ParsesReclaimRules()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", SwingQualityLongStrategyFile));

        Assert.Equal("Swing Reversal Reclaim Bull Quality No News V1", strategy.StrategyName);
        Assert.Equal("1d", strategy.Timeframe);
        Assert.Equal("long", strategy.Direction);
        Assert.Equal("swing_reclaim", strategy.EntryRules.SetupType);
        Assert.Equal("none", strategy.EntryRules.TrendFilter);
        Assert.False(strategy.EntryRules.RequirePositiveNews);
        Assert.True(strategy.EntryRules.RequirePriceAboveSma10);
        Assert.True(strategy.EntryRules.RequirePriceAboveSma20);
        Assert.False(strategy.EntryRules.RequirePriceAboveSma50);
        Assert.False(strategy.EntryRules.RequireSma10AboveSma20);
        Assert.False(strategy.EntryRules.RequireSma20AboveSma50);
        Assert.Equal("lookback_low", strategy.EntryRules.AnchoredVwapMode);
        Assert.False(strategy.EntryRules.RequirePriceAboveAnchoredVwap);
        Assert.False(strategy.EntryRules.EnableShort);
        Assert.Equal(20, strategy.EntryRules.ReclaimLookbackBars);
        Assert.Equal(6.0m, strategy.EntryRules.MinReclaimPullbackDepthPct);
        Assert.Equal(45.0m, strategy.EntryRules.MaxReclaimPullbackDepthPct);
        Assert.True(strategy.EntryRules.RequireReclaimCloseAboveSma10);
        Assert.True(strategy.EntryRules.RequireReclaimCloseAboveSma20);
        Assert.True(strategy.ExitRules.ExitOnSma10CrossBelowSma20);
        Assert.False(strategy.ExitRules.ExitShortOnSma10CrossAboveSma20);
    }

    [Fact]
    public void SwingOverboughtShortStrategyConfig_ParsesRolloverRules()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", SwingOverboughtShortStrategyFile));

        Assert.Equal("Swing Overbought Rollover Short No News V5", strategy.StrategyName);
        Assert.Equal("1d", strategy.Timeframe);
        Assert.Equal("short", strategy.Direction);
        Assert.True(strategy.EntryRules.EnableShort);
        Assert.Equal("swing_rollover", strategy.EntryRules.ShortSetupType);
        Assert.Equal(0.00m, strategy.EntryRules.MinVolumeSpike);
        Assert.Equal("none", strategy.EntryRules.ShortAnchoredVwapMode);
        Assert.False(strategy.EntryRules.RequirePriceBelowShortAnchoredVwap);
        Assert.Equal(55.0m, strategy.EntryRules.MinShortEntryRsi);
        Assert.Equal(90.0m, strategy.EntryRules.MaxShortEntryRsi);
        Assert.Equal(0.45m, strategy.EntryRules.MaxShortCloseLocationValue);
        Assert.Equal(20, strategy.EntryRules.RolloverLookbackBars);
        Assert.Equal(25.0m, strategy.EntryRules.MinRolloverAdvancePct);
        Assert.Equal(3.0m, strategy.EntryRules.MinRolloverDropFromHighPct);
        Assert.Equal(15.0m, strategy.EntryRules.MaxRolloverDropFromHighPct);
        Assert.True(strategy.EntryRules.RequireRolloverCloseBelowSma10);
        Assert.False(strategy.EntryRules.RequireRolloverCloseBelowSma20);
        Assert.Equal(3, strategy.EntryRules.RolloverConsecutiveLowerCloseBars);
        Assert.False(strategy.EntryRules.RequireRolloverCloseBelowPriorLow);
        Assert.False(strategy.ExitRules.ExitOnSma10CrossBelowSma20);
        Assert.True(strategy.ExitRules.ExitShortOnSma10CrossAboveSma20);
    }
    [Fact]
    public void RetainedRossBacktestConfig_ParsesSuccessfulResearchRun()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var config = reader.ReadBacktestRun(Path.Combine(repoRoot, "configs", "backtest", RetainedBacktestFile));

        Assert.Equal("finviz-reddit-ross-gapgo-bullflag-8-180d-10k-api-v2-confirmed-entry", config.RunName);
        Assert.Equal("alpaca", config.Provider);
        Assert.Equal(180, config.TimeWindow.LookbackDays);
        Assert.Equal(60, config.TimeWindow.WarmupLookbackDays);
        Assert.Equal(10000m, config.Portfolio.StartingCapital);
        Assert.Equal(2.0m, config.Portfolio.RiskPerTradePct);
        Assert.Equal(25.0m, config.Portfolio.MaxPositionValuePct);
        Assert.Equal(4, config.Portfolio.MaxConcurrentPositions);
        Assert.True(config.News.Enabled);
        Assert.Single(config.Strategies);
        Assert.Contains(config.Strategies, path => path.EndsWith(RetainedStrategyFile, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RetainedSwingBacktestConfig_UsesPromotedLongAndShortStrategies()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var config = reader.ReadBacktestRun(Path.Combine(repoRoot, "configs", "backtest", RetainedSwingBacktestFile));

        Assert.Equal("swing-quality-long-overbought-short-v5-comparison-crdo-msft-app-intc-mu-nvda-180d", config.RunName);
        Assert.Equal(180, config.TimeWindow.LookbackDays);
        Assert.Equal(60, config.TimeWindow.WarmupLookbackDays);
        Assert.Equal("alpaca", config.Provider);
        Assert.Equal("summary", config.Artifacts.RetentionMode);
        Assert.Contains("CRDO", config.Tickers);
        Assert.Contains("APP", config.Tickers);
        Assert.Equal(2, config.Strategies.Count);
        Assert.Contains(config.Strategies, path => path.EndsWith(SwingQualityLongStrategyFile, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(config.Strategies, path => path.EndsWith(SwingOverboughtShortStrategyFile, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RunConfigWriter_PreservesBaseWarmupWhenEvaluationLookbackChanges()
    {
        var repoRoot = FindRepositoryRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "trading-flow-writer-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var baseConfigPath = Path.Combine(repoRoot, "configs", "backtest", RetainedBacktestFile);
            var strategyPath = Path.Combine(repoRoot, "configs", "strategies", RetainedStrategyFile);
            var writer = new RunConfigWriter(
                new ProjectPaths(tempRoot),
                new SimpleYamlReader(),
                AtomicFileArtifactWriter.Instance);

            var generatedPath = writer.WriteBacktestConfig(new BacktestRunRequest(
                BaseConfigPath: baseConfigPath,
                RunName: "writer-warmup-test",
                LookbackDays: 365,
                Tickers: ["POET"],
                StrategyPaths: [strategyPath],
                StartingCapital: 10000m,
                RiskPerTradePct: 2m,
                MaxPositionValuePct: 25m,
                MaxConcurrentPositions: 4,
                CachePolicy: "use_cache",
                StrategyOverrides: []));

            var generatedYaml = File.ReadAllText(generatedPath);

            Assert.Contains("  lookback_days: 365", generatedYaml);
            Assert.Contains("  warmup_lookback_days: 60", generatedYaml);
            Assert.Contains("  - POET", generatedYaml);
            Assert.Contains(RetainedStrategyFile, generatedYaml);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
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

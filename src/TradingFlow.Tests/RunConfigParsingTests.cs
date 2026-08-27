using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;
using Xunit;

namespace TradingFlow.Tests;

public sealed class RunConfigParsingTests
{
    [Fact]
    public void ReadBacktestRun_NestedResearchConfigs_ResolveStorageFromSolutionRoot()
    {
        var repoRoot = FindRepositoryRoot();
        var researchRoot = Path.Combine(
            repoRoot,
            "data",
            "research",
            $"config-root-test-{Guid.NewGuid():N}");
        var nestedConfigDirectory = Path.Combine(researchRoot, "configs");
        Directory.CreateDirectory(nestedConfigDirectory);
        var sourcePath = Path.Combine(repoRoot, "configs", "backtest", "swing-backtest-profile.yaml");
        var nestedPath = Path.Combine(nestedConfigDirectory, "run.yaml");

        try
        {
            File.Copy(sourcePath, nestedPath);

            var run = new SimpleYamlReader().ReadBacktestRun(nestedPath);

            Assert.Equal(
                Path.GetFullPath(Path.Combine(repoRoot, "data", "backtest", "normalized")),
                run.NormalizedRoot);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(repoRoot, "data", "backtest", "results")),
                run.ResultsRoot);
        }
        finally
        {
            if (Directory.Exists(researchRoot))
            {
                Directory.Delete(researchRoot, recursive: true);
            }
        }
    }

    // One-brain: a strategy regenerated for paper/live via RunConfigWriter must round-trip losslessly,
    // or paper behaviour silently diverges from backtest (review finding #1).
    [Theory]
    [InlineData("minervini-trend-template-vcp.v4-trend-rider.yaml")]
    [InlineData("swing-reversal-reclaim-bull-quality-no-news.v1.yaml")]
    [InlineData("swing-mean-reversion-reclaim.v1.yaml")]
    [InlineData("swing-catalyst-drift.v5.yaml")]
    [InlineData("intraday-ema10-ema20-macd-volume.v1.yaml")]
    public void WriteStrategyYaml_RoundTrips_PreservesEntryExitAndRegime(string strategyFile)
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var original = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", strategyFile));

        var yaml = RunConfigWriter.SerializeStrategyYaml(original);

        var tempPath = Path.Combine(Path.GetTempPath(), $"roundtrip-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(tempPath, yaml);
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

    private const string RetainedStrategyFile = "intraday-ema10-ema20-macd-volume.v1.yaml";
    private const string RunnerUpIntradayStrategyFile = "intraday-ema10-ema20-macd-volume.v1.yaml";
    private const string PaperIntradayStrategyFile = "intraday-ema10-ema20-macd-volume.v1.yaml";
    private const string RetainedIntradayStrategyFile = "intraday-ema10-ema20-macd-volume.v1.yaml";
    private const string RetainedBacktestFile = "intraday-backtest-profile.yaml";
    private const string RetainedSwingBacktestFile = "swing-backtest-profile.yaml";
    private const string PaperSwingFile = "alpaca-paper-swing.yaml";
    private const string SwingQualityLongStrategyFile = "swing-reversal-reclaim-bull-quality-no-news.v1.yaml";
    private const string SwingOverboughtShortStrategyFile = "swing-overbought-rollover-short-no-news.v5.yaml";
    private const string ShannonAvwapStrategyFile = "brian_shannon_mta_avwap_strategies.yaml";
    private const string BreitsteinVwapTrapStrategyFile = "lance_breitstein_intraday_tactics.yaml";
    private const string QullamaggieEpisodicPivotStrategyFile = "kristjan_qullamaggie_stream_methodology.yaml";
    private const string MinerviniVcpStrategyFile = "minervini-trend-template-vcp.v2.yaml";
    private const string MinerviniTrendRiderResearchStrategyFile = "minervini-trend-template-vcp.v4-trend-rider.yaml";

    [Fact]
    public void ResearchIntradayExecutionStrategies_ParseExactExecutionFields()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategyRoot = Path.Combine(repoRoot, "configs", "backtest", "strategies");

        var vwapPullback = reader.ReadStrategy(Path.Combine(strategyRoot, "intraday-vwap-momentum-pullback.bt-v1.yaml"));
        Assert.Equal("vwap_pullback", vwapPullback.EntryRules.SetupType);
        Assert.Equal("vwap_minus_atr", vwapPullback.ExitRules.InitialStopMode);
        Assert.Equal("r_multiple", vwapPullback.ExitRules.ProfitTargetMode);
        Assert.Equal(2.0m, vwapPullback.EntryRules.MinVolumeSpike);
        Assert.Equal(3.0m, vwapPullback.EntryRules.MinGapUpPct);

        var compression = reader.ReadStrategy(Path.Combine(strategyRoot, "intraday-atr-compression-breakout.bt-v1.yaml"));
        Assert.Equal("atr_compression_breakout", compression.EntryRules.SetupType);
        Assert.Equal("atr_compression_breakdown", compression.EntryRules.ShortSetupType);
        Assert.Equal("inside_or_nr7", compression.EntryRules.PriorCompressionMode);
        Assert.Equal(0.10m, compression.EntryRules.OpeningRangeBreakBuffer);
        Assert.Equal("opening_range_opposite", compression.ExitRules.InitialStopMode);
        Assert.True(compression.ExitRules.EnableFailedBreakoutCircuitBreaker);
        Assert.Equal(3, compression.ExitRules.FailedBreakoutBars);

        var divergence = reader.ReadStrategy(Path.Combine(strategyRoot, "intraday-macd-divergence-fade.bt-v1.yaml"));
        Assert.Equal("macd_divergence_fade", divergence.EntryRules.SetupType);
        Assert.Equal("extreme_shadow", divergence.ExitRules.InitialStopMode);
        Assert.Equal("vwap", divergence.ExitRules.ProfitTargetMode);
        Assert.Equal(3.0m, divergence.EntryRules.MinVwapDistanceAtrForDivergence);
        Assert.Equal(20, divergence.EntryRules.DivergenceLookbackBars);
        Assert.Equal(12, divergence.EntryRules.DivergenceStartHour);
    }

    [Fact]
    public void ResearchSwingStrategies_ParseDedicatedConnorsAndPivotVcpFields()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategyRoot = Path.Combine(repoRoot, "configs", "backtest", "strategies");

        var connors = reader.ReadStrategy(
            Path.Combine(strategyRoot, "swing-connors-rsi2-oversold.bt-v3.yaml"));
        Assert.Equal("rsi2_oversold", connors.EntryRules.SetupType);
        Assert.True(connors.EntryRules.RequirePriceAboveSma200);
        Assert.Equal(5m, connors.EntryRules.MaxReversionRsi2);
        Assert.Equal(70m, connors.ExitRules.ExitOnRsi2Above);

        var vcp = reader.ReadStrategy(
            Path.Combine(strategyRoot, "minervini-trend-template-pivot-vcp.bt-v6.yaml"));
        Assert.Equal("volatility_contraction_pattern", vcp.EntryRules.SetupType);
        Assert.Equal(60, vcp.EntryRules.VolatilityContractionLookbackBars);
        Assert.Equal(2, vcp.EntryRules.VcpPivotStrengthBars);
        Assert.Equal(0.90m, vcp.EntryRules.VcpMaximumDepthRatioToPrevious);
        Assert.Equal(0.70m, vcp.EntryRules.VcpMaximumContractionToAdvanceVolumeRatio);
        Assert.True(vcp.EntryRules.VcpRequireProgressiveContractionVolume);
        Assert.Equal("swing_low", vcp.ExitRules.InitialStopMode);
    }

    [Fact]
    public void ResearchSwingHourlyConfirmation_ParsesCompletedBarExecutionContract()
    {
        var repoRoot = FindRepositoryRoot();
        var strategyPath = Path.Combine(
            repoRoot,
            "configs",
            "backtest",
            "strategies",
            "minervini-trend-template-pivot-vcp.bt-v8-hourly-confirmation.yaml");

        var strategy = new SimpleYamlReader().ReadStrategy(strategyPath);
        var confirmation = strategy.Execution.EffectiveConfirmation;

        Assert.Equal("1d", strategy.Timeframe);
        Assert.Equal("1h", strategy.Execution.Timeframe);
        Assert.True(confirmation.Enabled);
        Assert.Equal(12, confirmation.MaxBarsAfterSetup);
        Assert.Equal("close_above_setup_close", confirmation.PriceFilter);
        Assert.Equal("ema10_above_ema20", confirmation.TrendFilter);
        Assert.Equal("macd_histogram_positive", confirmation.MomentumFilter);
        Assert.Equal(0.55m, confirmation.MinCloseLocationValue);
        Assert.Null(confirmation.MaxCloseLocationValue);
    }

    [Fact]
    public void AlpacaPaperConfig_UsesConfirmedReclaimStrategyAndSipFeed()
    {
        var repoRoot = FindRepositoryRoot();
        var configPath = Path.Combine(repoRoot, "configs", "paper", "alpaca-paper.yaml");

        var config = new SimpleYamlReader().ReadBacktestRun(configPath);

        Assert.Equal("alpaca", config.Provider);
        Assert.Equal("sip", config.Providers.Alpaca.DataFeed);
        Assert.True(Path.IsPathRooted(config.RawRoot));
        Assert.True(Path.IsPathRooted(config.NormalizedRoot));
        Assert.True(Path.IsPathRooted(config.ResultsRoot));
        Assert.Equal(10, config.TimeWindow.LookbackDays);
        Assert.Equal(90, config.TimeWindow.WarmupLookbackDays);
        Assert.Contains("1m", config.Intervals);
        Assert.Contains("5m", config.Intervals);
        Assert.Equal("1m", config.DerivedTimeframes.Source);
        Assert.False(config.Execution.AllowExtendedHoursTrading);
        Assert.Equal("full", config.Artifacts.RetentionMode);
        Assert.Empty(config.Strategies);
    }

    [Fact]
    public void AlpacaPaperSwingConfig_UsesDailyLongSwingStrategyOnly()
    {
        var repoRoot = FindRepositoryRoot();
        var configPath = Path.Combine(repoRoot, "configs", "paper", PaperSwingFile);

        var config = new SimpleYamlReader().ReadBacktestRun(configPath);

        Assert.Equal("paper", config.Mode);
        Assert.Equal("alpaca", config.Provider);
        Assert.Equal("sip", config.Providers.Alpaca.DataFeed);
        Assert.Equal(260, config.TimeWindow.LookbackDays);
        Assert.Equal(400, config.TimeWindow.WarmupLookbackDays);
        Assert.Contains("1h", config.Intervals);
        Assert.Contains("1d", config.Intervals);
        Assert.Equal("1h", config.DerivedTimeframes.Source);
        Assert.True(config.News.Enabled);
        Assert.False(config.Execution.AllowExtendedHoursTrading);
        Assert.True(config.Universe?.IsUnresolvedWishlist);
        Assert.Empty(config.Strategies);
    }

    [Fact]
    public void RetainedIntradayTopStrategyConfig_ParsesThreeGateRules()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", RetainedStrategyFile));

        Assert.Equal("TOP1 Intraday - EMA10/20 MACD Volume V1", strategy.StrategyName);
        Assert.Equal("indicator_stack", strategy.EntryRules.SetupType);
        Assert.Equal("long", strategy.Direction);
        Assert.Equal("1m", strategy.Timeframe);
        Assert.Equal("1m", strategy.Execution.Timeframe);
        Assert.Equal(2.0m, strategy.EntryRules.MinVolumeSpike);
        Assert.Equal("cumulative_same_time", strategy.EntryRules.MinVolumeSpikeSource);
        Assert.Equal("hard_gate", strategy.EntryRules.VolumeConfirmationMode);
        Assert.Null(strategy.EntryRules.MinSessionRelativeVolume);
        Assert.False(strategy.EntryRules.RequirePriceAboveVwap);
        Assert.False(strategy.EntryRules.RequirePriceAboveEma10);
        Assert.False(strategy.EntryRules.RequirePriceAboveEma20);
        Assert.True(strategy.EntryRules.RequireEma10AboveEma20);
        Assert.True(strategy.EntryRules.RequireMacdHistogramPositive);
        Assert.Equal(1, strategy.EntryRules.MaxEntriesPerTickerPerDay);
        Assert.False(strategy.EntryRules.EnableEntryBarConfirmation);
        Assert.Equal(0.0m, strategy.EntryRules.MinCloseLocationValue);
        Assert.Null(strategy.EntryRules.MaxMacdHistogram);
        Assert.Equal(5, strategy.EntryRules.PriorEntryGainLookbackBars);
        Assert.Null(strategy.EntryRules.MaxPriorEntryGainPct);
        Assert.False(strategy.EntryRules.RejectWeakCloseOnHighRelativeVolume);
        Assert.False(strategy.ExitRules.EnableConfirmedVwapExit);
        Assert.False(strategy.ExitRules.EnableAtrTrailingStop);
        Assert.Equal(1.0m, strategy.ExitRules.StopAtrMultiple);
        Assert.Equal(3.0m, strategy.ExitRules.TargetRMultiple);
        Assert.False(strategy.ExitRules.ExitOnEma10CrossBelowEma20);
    }

    [Fact]
    public void PaperIntradayStrategyConfig_UsesSimplifiedIntradayStrategy()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", PaperIntradayStrategyFile));

        Assert.Equal("TOP1 Intraday - EMA10/20 MACD Volume V1", strategy.StrategyName);
        Assert.Equal("indicator_stack", strategy.EntryRules.SetupType);
        Assert.Equal(2.0m, strategy.EntryRules.MinVolumeSpike);
        Assert.Equal("cumulative_same_time", strategy.EntryRules.MinVolumeSpikeSource);
        Assert.True(strategy.EntryRules.RequireEma10AboveEma20);
        Assert.True(strategy.EntryRules.RequireMacdHistogramPositive);
        Assert.False(strategy.EntryRules.RequirePriceAboveVwap);
        Assert.Equal(1.0m, strategy.ExitRules.StopAtrMultiple);
        Assert.Equal(3.0m, strategy.ExitRules.TargetRMultiple);
    }
    [Fact]
    public void RunnerUpIntradayStrategyConfig_UsesSameSimplifiedIntradayStrategy()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", RunnerUpIntradayStrategyFile));

        Assert.Equal("TOP1 Intraday - EMA10/20 MACD Volume V1", strategy.StrategyName);
        Assert.Equal("indicator_stack", strategy.EntryRules.SetupType);
        Assert.Equal(2.0m, strategy.EntryRules.MinVolumeSpike);
        Assert.True(strategy.EntryRules.RequireEma10AboveEma20);
        Assert.True(strategy.EntryRules.RequireMacdHistogramPositive);
        Assert.Equal(1.0m, strategy.ExitRules.StopAtrMultiple);
        Assert.False(strategy.ExitRules.EnableAtrTrailingStop);
    }

    [Fact]
    public void SwingQualityLongStrategyConfig_ParsesReclaimRules()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", SwingQualityLongStrategyFile));

        Assert.Equal("TOP1 Swing Long - Reversal Reclaim Bull Quality", strategy.StrategyName);
        Assert.Equal("1d", strategy.Timeframe);
        Assert.Equal("1h", strategy.Execution.Timeframe);
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

        Assert.Equal("TOP2 Swing Short - Overbought Rollover", strategy.StrategyName);
        Assert.Equal("1d", strategy.Timeframe);
        Assert.Equal("1h", strategy.Execution.Timeframe);
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
    public void BootstrapCatalog_ParsesOnlyAdmittedResearchStrategies()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var catalog = CreateStrategyArtifactCatalog(repoRoot).GetSnapshot();
        var breitstein = Assert.Single(
                catalog.ExecutableArtifacts,
                artifact => artifact.SourcePath.EndsWith(BreitsteinVwapTrapStrategyFile, StringComparison.OrdinalIgnoreCase))
            .ResolvedStrategy;
        Assert.Equal("lance_breitstein_intraday_tactics", breitstein.Source);
        Assert.Equal("vwap_reclaim_trap", breitstein.EntryRules.SetupType);
        Assert.Equal(2.0m, breitstein.EntryRules.MinSessionRelativeVolume);
        Assert.Equal(3, breitstein.EntryRules.RequirePriorFlushBelowVwapBars);
        Assert.Equal(12, breitstein.EntryRules.VwapReclaimMaxBarsSinceFlush);
        Assert.Equal(1.50m, breitstein.EntryRules.MinReclaimVolumeRatio);
        Assert.Equal("1m", breitstein.Execution.Timeframe);
        Assert.Equal(6, catalog.ExecutableArtifacts.Count);
        Assert.Equal(15, catalog.ArchivedArtifacts.Count);
        Assert.Contains(catalog.ArchivedArtifacts, artifact =>
            artifact.SourcePath.EndsWith(ShannonAvwapStrategyFile, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(catalog.ArchivedArtifacts, artifact =>
            artifact.SourcePath.EndsWith(QullamaggieEpisodicPivotStrategyFile, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AllExecutableStrategyConfigs_AreStrictlyParsedByCatalog()
    {
        var repoRoot = FindRepositoryRoot();
        var artifacts = CreateStrategyArtifactCatalog(repoRoot).GetSnapshot().ExecutableArtifacts;
        foreach (var artifact in artifacts)
        {
            Assert.False(String.IsNullOrWhiteSpace(artifact.ResolvedStrategy.StrategyId));
            Assert.False(String.IsNullOrWhiteSpace(artifact.ResolvedStrategy.EntryRules.SetupType));
        }
    }

    [Fact]
    public void ArchivedShannonStrategy_IsUnavailableToRuntimeAutomation()
    {
        var repoRoot = FindRepositoryRoot();
        var catalog = CreateStrategyArtifactCatalog(repoRoot);
        var sourcePath = Path.Combine(repoRoot, "configs", "strategies", ShannonAvwapStrategyFile);

        Assert.Throws<InvalidOperationException>(() => catalog.RequireSourcePath(sourcePath));
    }

    [Fact]
    public void RetainedIntradayBacktestProfile_IsAnUnresolvedWishlistResearchTemplate()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var config = reader.ReadBacktestRun(Path.Combine(repoRoot, "configs", "backtest", RetainedBacktestFile));

        Assert.Equal("intraday-backtest-profile", config.RunName);
        Assert.Equal("alpaca", config.Provider);
        Assert.Equal("sip", config.Providers.Alpaca.DataFeed);
        Assert.Equal(60, config.TimeWindow.LookbackDays);
        Assert.Equal(90, config.TimeWindow.WarmupLookbackDays);
        Assert.Equal(10000m, config.Portfolio.StartingCapital);
        Assert.Equal(1.0m, config.Portfolio.AccountRiskBudgetPct);
        Assert.Equal(25.0m, config.Portfolio.MaxPositionNotionalPct);
        Assert.Equal(4, config.Portfolio.MaxConcurrentPositions);
        Assert.False(config.News.Enabled);
        Assert.Equal("1m", config.DerivedTimeframes.Source);
        Assert.Equal("wishlist", config.Validation.BiasRisk.UniverseSource);
        Assert.True(config.Universe?.IsUnresolvedWishlist);
        Assert.Empty(config.Tickers);
        Assert.Single(config.Strategies);
        Assert.Contains(config.Strategies, path => path.EndsWith(RetainedIntradayStrategyFile, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SwingBacktestProfile_IsWishlistDrivenDiagnosticAndUsesCanonicalVcpResearchStrategy()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var config = reader.ReadBacktestRun(Path.Combine(repoRoot, "configs", "backtest", RetainedSwingBacktestFile));

        Assert.Equal("swing-backtest-profile", config.RunName);
        Assert.Equal(180, config.TimeWindow.LookbackDays);
        Assert.Equal(400, config.TimeWindow.WarmupLookbackDays);
        Assert.Equal("alpaca", config.Provider);
        Assert.Equal("sip", config.Providers.Alpaca.DataFeed);
        Assert.Contains("1h", config.Intervals);
        Assert.Contains("1d", config.Intervals);
        Assert.Equal("1h", config.DerivedTimeframes.Source);
        Assert.Equal("full", config.Artifacts.RetentionMode);
        Assert.Equal("wishlist", config.Validation.BiasRisk.UniverseSource);
        Assert.True(config.Universe?.IsUnresolvedWishlist);
        Assert.True(config.Validation.OutOfSample.Enabled);
        Assert.Equal(30m, config.Validation.OutOfSample.Percent);
        Assert.True(config.Validation.WalkForward.Enabled);
        Assert.True(config.Validation.Benchmark.Enabled);
        Assert.Equal(5m, config.Portfolio.MaxBarParticipationPct);
        Assert.Equal(0.0000278m, config.Portfolio.SecFeeRate);
        Assert.Equal(0.000166m, config.Portfolio.FinraTafPerShare);
        Assert.Equal(8.30m, config.Portfolio.FinraTafCap);
        Assert.Equal(500, config.News.MaxArticlesPerTicker);
        Assert.Equal(3, config.News.SentimentTimeoutSeconds);
        Assert.Empty(config.Tickers);
        Assert.Single(config.Strategies);
        Assert.Contains(config.Strategies, path => path.EndsWith(MinerviniTrendRiderResearchStrategyFile, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResearchMeanReversionV2_UsesTradingBarAndRsi2RecoveryExits()
    {
        var repoRoot = FindRepositoryRoot();
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(
            repoRoot,
            "configs",
            "backtest",
            "strategies",
            "swing-mean-reversion-rsi2-recovery.bt-v2.yaml"));

        Assert.Equal("mean_reversion_reclaim", strategy.EntryRules.SetupType);
        Assert.Equal(5m, strategy.EntryRules.MaxReversionRsi2);
        Assert.Equal(5, strategy.ExitRules.MaxHoldBars);
        Assert.Equal(70m, strategy.ExitRules.ExitOnRsi2Above);
    }

    [Fact]
    public void ResearchMinerviniProxyV5_UsesTrendTemplateAndYearlyPositionRules()
    {
        var repoRoot = FindRepositoryRoot();
        var strategy = new SimpleYamlReader().ReadStrategy(Path.Combine(
            repoRoot,
            "configs",
            "backtest",
            "strategies",
            "minervini-trend-template-breakout-proxy.bt-v5.yaml"));

        Assert.True(strategy.EntryRules.RequirePriceAboveSma150);
        Assert.True(strategy.EntryRules.RequirePriceAboveSma200);
        Assert.True(strategy.EntryRules.RequireSma50AboveSma150);
        Assert.True(strategy.EntryRules.RequireSma150AboveSma200);
        Assert.Equal(30m, strategy.EntryRules.MinPriceVs52WeekLowPct);
        Assert.Equal(-25m, strategy.EntryRules.MaxPriceVs52WeekHighPct);
        Assert.Equal(90, strategy.ExitRules.MaxHoldBars);
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
                AtomicFileArtifactWriter.Instance,
                CreateStrategyArtifactCatalog(repoRoot),
                CreateExperimentArtifactStore(repoRoot),
                new StrategyAuthorizationTestRegistry());

            var generatedPath = writer.WriteBacktestConfig(new BacktestRunRequest(
                BaseConfigPath: baseConfigPath,
                RunName: "writer-warmup-test",
                LookbackDays: 365,
                WishlistId: Guid.Parse("220939c3-1e91-4c4e-9bca-5d61f9837ebd"),
                WishlistName: "volatile",
                Tickers: ["POET"],
                StrategyPaths: [strategyPath],
                StartingCapital: 10000m,
                AccountRiskBudgetPct: 2m,
                MaxPositionNotionalPct: 25m,
                MaxConcurrentPositions: 4,
                CachePolicy: "use_cache",
                StrategyOverrides: []));

            var generatedYaml = File.ReadAllText(generatedPath);

            Assert.Contains("  lookback_days: 365", generatedYaml);
            Assert.Contains("  warmup_lookback_days: 90", generatedYaml);
            Assert.Contains("universe:", generatedYaml);
            Assert.Contains("  mode: resolved_snapshot", generatedYaml);
            Assert.Contains("  source: wishlist", generatedYaml);
            Assert.Contains("  wishlist_name: volatile", generatedYaml);
            Assert.Contains("  - POET", generatedYaml);
            var generated = new SimpleYamlReader().ReadBacktestRun(generatedPath);
            var snapshotPath = Assert.Single(generated.Strategies);
            Assert.True(File.Exists(snapshotPath));
            Assert.True(File.Exists(snapshotPath + ".artifact.json"));
            var validator = new StrategyRunArtifactValidator(
                new SimpleYamlReader(),
                CreateStrategyArtifactCatalog(repoRoot));
            _ = validator.ReadAndValidate(
                snapshotPath,
                TradingFlow.Domain.Strategies.StrategySelectionMode.Backtest);
            Assert.Throws<InvalidDataException>(() => validator.ReadAndValidate(
                snapshotPath,
                TradingFlow.Domain.Strategies.StrategySelectionMode.RunPaperShadow));

            var spoofedStrategyDirectory = Path.Combine(tempRoot, "unrelated", "configs", "strategies");
            Directory.CreateDirectory(spoofedStrategyDirectory);
            var orphanPath = Path.Combine(spoofedStrategyDirectory, "orphan-strategy.yaml");
            File.Copy(snapshotPath, orphanPath);
            Assert.Throws<InvalidOperationException>(() => validator.ReadAndValidate(
                orphanPath,
                TradingFlow.Domain.Strategies.StrategySelectionMode.Backtest));

            var originalSnapshot = File.ReadAllText(snapshotPath);
            File.WriteAllText(
                snapshotPath,
                System.Text.RegularExpressions.Regex.Replace(
                    originalSnapshot,
                    @"(?m)^  min_volume_spike:.*$",
                    "  min_volume_spike: 9.0"));
            Assert.Throws<InvalidDataException>(() => validator.ReadAndValidate(
                snapshotPath,
                TradingFlow.Domain.Strategies.StrategySelectionMode.Backtest));
            Assert.True(generated.Universe?.IsResolvedSnapshot);
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
    public async Task RunConfigWriter_ConcurrentSameNameRunsPublishDistinctCompleteSnapshots()
    {
        var repoRoot = FindRepositoryRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "trading-flow-writer-concurrency-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var reader = new SimpleYamlReader();
            var artifactCatalog = CreateStrategyArtifactCatalog(repoRoot);
            var writer = new RunConfigWriter(
                new ProjectPaths(tempRoot),
                reader,
                AtomicFileArtifactWriter.Instance,
                artifactCatalog,
                CreateExperimentArtifactStore(repoRoot),
                new StrategyAuthorizationTestRegistry());
            var request = new BacktestRunRequest(
                Path.Combine(repoRoot, "configs", "backtest", RetainedBacktestFile),
                "same-operator-name",
                30,
                Guid.Parse("220939c3-1e91-4c4e-9bca-5d61f9837ebd"),
                "volatile",
                ["POET"],
                [Path.Combine(repoRoot, "configs", "strategies", RetainedStrategyFile)],
                10_000m,
                1m,
                25m,
                4,
                "use_cache",
                []);

            var paths = await Task.WhenAll(
                Task.Run(() => writer.WriteBacktestConfig(request)),
                Task.Run(() => writer.WriteBacktestConfig(request)));

            Assert.Equal(2, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            var validator = new StrategyRunArtifactValidator(reader, artifactCatalog);
            foreach (var path in paths)
            {
                Assert.True(File.Exists(path));
                var run = reader.ReadBacktestRun(path);
                var strategyPath = Assert.Single(run.Strategies);
                Assert.True(File.Exists(strategyPath));
                Assert.True(File.Exists(strategyPath + ".artifact.json"));
                _ = validator.ReadAndValidate(
                    strategyPath,
                    TradingFlow.Domain.Strategies.StrategySelectionMode.Backtest);
            }
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
    public void RunConfigWriter_PreservesExecutionRealismAndHistoricalNewsBudget()
    {
        var repoRoot = FindRepositoryRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "trading-flow-writer-realism-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var baseConfigPath = Path.Combine(repoRoot, "configs", "backtest", RetainedSwingBacktestFile);
            var strategyPath = Path.Combine(
                repoRoot,
                "configs",
                "strategies",
                "minervini-trend-template-vcp.v4-trend-rider.yaml");
            var writer = new RunConfigWriter(
                new ProjectPaths(tempRoot),
                new SimpleYamlReader(),
                AtomicFileArtifactWriter.Instance,
                CreateStrategyArtifactCatalog(repoRoot),
                CreateExperimentArtifactStore(repoRoot),
                new StrategyAuthorizationTestRegistry());

            var generatedPath = writer.WriteBacktestConfig(new BacktestRunRequest(
                BaseConfigPath: baseConfigPath,
                RunName: "writer-realism-test",
                LookbackDays: 180,
                WishlistId: Guid.Parse("220939c3-1e91-4c4e-9bca-5d61f9837ebd"),
                WishlistName: "mega-cap-swing",
                Tickers: ["MSFT"],
                StrategyPaths: [strategyPath],
                StartingCapital: 10000m,
                AccountRiskBudgetPct: 1m,
                MaxPositionNotionalPct: 25m,
                MaxConcurrentPositions: 4,
                CachePolicy: "use_cache",
                StrategyOverrides: []));

            var generated = new SimpleYamlReader().ReadBacktestRun(generatedPath);

            Assert.Equal(5m, generated.Portfolio.MaxBarParticipationPct);
            Assert.Equal(0.0000278m, generated.Portfolio.SecFeeRate);
            Assert.Equal(0.000166m, generated.Portfolio.FinraTafPerShare);
            Assert.Equal(8.30m, generated.Portfolio.FinraTafCap);
            Assert.True(generated.News.Enabled);
            Assert.Equal(500, generated.News.MaxArticlesPerTicker);
            Assert.Equal(3, generated.News.SentimentTimeoutSeconds);
            Assert.True(generated.Validation.OutOfSample.Enabled);
            Assert.True(generated.Validation.WalkForward.Enabled);
            Assert.True(generated.Validation.Benchmark.Enabled);
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
    public void RunConfigWriter_ParameterOverrideRequiresVersionAndRecordsImmutableProvenance()
    {
        var repoRoot = FindRepositoryRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "trading-flow-versioned-override-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var baseConfigPath = Path.Combine(repoRoot, "configs", "backtest", RetainedBacktestFile);
            var strategyPath = Path.Combine(repoRoot, "configs", "strategies", RetainedStrategyFile);
            var writer = new RunConfigWriter(
                new ProjectPaths(tempRoot),
                new SimpleYamlReader(),
                AtomicFileArtifactWriter.Instance,
                CreateStrategyArtifactCatalog(repoRoot),
                CreateExperimentArtifactStore(repoRoot),
                new StrategyAuthorizationTestRegistry());
            var unversionedOverride = new StrategyParameterOverride(
                strategyPath,
                2.5m,
                0m,
                100m,
                null,
                false,
                "1h",
                50,
                1m,
                3m,
                6m,
                false,
                1m,
                1m,
                1,
                "1m",
                25m,
                Actor: "unit-test");
            BacktestRunRequest Request(StrategyParameterOverride strategyOverride) => new(
                baseConfigPath,
                "versioned-override-test",
                30,
                Guid.Parse("220939c3-1e91-4c4e-9bca-5d61f9837ebd"),
                "volatile",
                ["POET"],
                [strategyPath],
                10000m,
                1m,
                25m,
                4,
                "use_cache",
                [strategyOverride]);

            Assert.Throws<InvalidOperationException>(() => writer.WriteBacktestConfig(Request(unversionedOverride)));

            var generatedPath = writer.WriteBacktestConfig(Request(
                unversionedOverride with { DerivedSemanticVersion = "1.1.0-research" }));
            var generated = new SimpleYamlReader().ReadBacktestRun(generatedPath);
            var snapshotPath = Assert.Single(generated.Strategies);
            var manifestJson = File.ReadAllText(snapshotPath + ".artifact.json");

            Assert.Contains("\"semanticVersion\":\"1.1.0-research\"", manifestJson, StringComparison.Ordinal);
            Assert.Contains("\"createdBy\":\"unit-test\"", manifestJson, StringComparison.Ordinal);
            Assert.Contains("entry_rules.min_volume_spike", manifestJson, StringComparison.Ordinal);
            Assert.Contains("exit_rules.enable_atr_trailing_stop", manifestJson, StringComparison.Ordinal);
            _ = new StrategyRunArtifactValidator(
                new SimpleYamlReader(),
                CreateStrategyArtifactCatalog(repoRoot)).ReadAndValidate(
                snapshotPath,
                TradingFlow.Domain.Strategies.StrategySelectionMode.Backtest);
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
    public void RunConfigWriter_AddsFinerExecutionTimeframeToDownloads()
    {
        var repoRoot = FindRepositoryRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "trading-flow-writer-timeframe-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var baseConfigPath = Path.Combine(repoRoot, "configs", "backtest", RetainedBacktestFile);
            var strategyPath = Path.Combine(repoRoot, "configs", "strategies", BreitsteinVwapTrapStrategyFile);
            var writer = new RunConfigWriter(
                new ProjectPaths(tempRoot),
                new SimpleYamlReader(),
                AtomicFileArtifactWriter.Instance,
                CreateStrategyArtifactCatalog(repoRoot),
                CreateExperimentArtifactStore(repoRoot),
                new StrategyAuthorizationTestRegistry());

            var generatedPath = writer.WriteBacktestConfig(new BacktestRunRequest(
                BaseConfigPath: baseConfigPath,
                RunName: "writer-timeframe-test",
                LookbackDays: 30,
                WishlistId: Guid.Parse("220939c3-1e91-4c4e-9bca-5d61f9837ebd"),
                WishlistName: "volatile",
                Tickers: ["PLUG"],
                StrategyPaths: [strategyPath],
                StartingCapital: 10000m,
                AccountRiskBudgetPct: 2m,
                MaxPositionNotionalPct: 25m,
                MaxConcurrentPositions: 4,
                CachePolicy: "use_cache",
                StrategyOverrides: []));

            var parsed = new SimpleYamlReader().ReadBacktestRun(generatedPath);

            Assert.Contains("1m", parsed.Intervals);
            Assert.Contains("5m", parsed.Intervals);
            Assert.Equal("1m", parsed.DerivedTimeframes.Source);
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
    public void RunConfigWriter_SaveTempConfig_WritesSingleExplicitNewsBlock()
    {
        var repoRoot = FindRepositoryRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "trading-flow-paper-writer-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var baseConfigPath = Path.Combine(repoRoot, "configs", "paper", "alpaca-paper.yaml");
            var strategyPath = Path.Combine(repoRoot, "configs", "strategies", RetainedStrategyFile);
            var artifactCatalog = CreateStrategyArtifactCatalog(repoRoot);
            var sourceArtifact = artifactCatalog.RequireSourcePath(strategyPath);
            var paperShadowArtifact = sourceArtifact with
            {
                EffectiveLifecycle = TradingFlow.Domain.Strategies.StrategyLifecycleState.PaperShadow
            };
            var writer = new RunConfigWriter(
                new ProjectPaths(tempRoot),
                new SimpleYamlReader(),
                AtomicFileArtifactWriter.Instance,
                artifactCatalog,
                CreateExperimentArtifactStore(repoRoot),
                new StrategyAuthorizationTestRegistry());

            var generatedPath = writer.SaveTempConfig(
                baseConfigPath,
                ["MU", "NVDA"],
                paperShadowArtifact,
                TradingFlow.Domain.Strategies.StrategySelectionMode.RunPaperShadow,
                orderExpiration: "day",
                entryOrderType: "limit",
                allowExtendedHoursTrading: true,
                resolvedScreenerQuery: "f=cap_large",
                runName: "paper-news-disabled-test",
                newsEnabled: false,
                selectedSourceTickers: ["MU"],
                resolvedScreenerTickers: ["NVDA"]);

            var generatedYaml = File.ReadAllText(generatedPath);
            var parsed = new SimpleYamlReader().ReadBacktestRun(generatedPath);

            Assert.Single(System.Text.RegularExpressions.Regex.Matches(generatedYaml, "^news:", System.Text.RegularExpressions.RegexOptions.Multiline));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(generatedYaml, "^universe:", System.Text.RegularExpressions.RegexOptions.Multiline));
            Assert.Contains("news:", generatedYaml);
            Assert.Contains("  enabled: false", generatedYaml);
            Assert.False(parsed.News.Enabled);
            Assert.Equal("limit", parsed.Execution.EntryOrderType);
            Assert.Equal("day", parsed.Execution.OrderExpiration);
            Assert.True(parsed.Execution.AllowExtendedHoursTrading);
            Assert.True(parsed.Universe?.IsResolvedSnapshot);
            Assert.False(parsed.Screener?.Enabled);
            Assert.Contains("resolved_screener_query: \"f=cap_large\"", generatedYaml);
            Assert.Equal(["MU"], parsed.Universe!.ResolvedSelectedSourceTickers);
            Assert.Equal(["NVDA"], parsed.Universe.ResolvedScreenerTickers);
            Assert.Equal(["MU", "NVDA"], parsed.Tickers);
            _ = new StrategyRunArtifactValidator(
                new SimpleYamlReader(),
                CreateStrategyArtifactCatalog(repoRoot)).ReadAndValidate(
                Assert.Single(parsed.Strategies),
                TradingFlow.Domain.Strategies.StrategySelectionMode.RunPaperShadow);
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
    public void RunConfigWriter_DeleteTempConfig_OnlyDeletesYamlInsideTempDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "trading-flow-delete-config-test", Guid.NewGuid().ToString("N"));
        var tempConfig = Path.Combine(tempRoot, "configs", "backtest", "temp", "generated.yaml");
        var retainedConfig = Path.Combine(tempRoot, "configs", "backtest", "intraday-backtest-profile.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(tempConfig)!);
        File.WriteAllText(tempConfig, "mode: backtest");
        File.WriteAllText(retainedConfig, "mode: backtest");

        try
        {
            var writer = new RunConfigWriter(
                new ProjectPaths(tempRoot),
                new SimpleYamlReader(),
                AtomicFileArtifactWriter.Instance,
                CreateStrategyArtifactCatalog(FindRepositoryRoot()),
                CreateExperimentArtifactStore(FindRepositoryRoot()),
                new StrategyAuthorizationTestRegistry());

            writer.DeleteTempConfig(tempConfig);
            writer.DeleteTempConfig(retainedConfig);

            Assert.False(File.Exists(tempConfig));
            Assert.True(File.Exists(retainedConfig));
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
        return TestRepository.FindRoot();
    }

    private static StrategyArtifactCatalog CreateStrategyArtifactCatalog(string repositoryRoot) =>
        new(
            repositoryRoot,
            Path.Combine(repositoryRoot, "configs", "strategy-catalog.json"),
            new SimpleYamlReader());

    private static StrategyExperimentArtifactStore CreateExperimentArtifactStore(string repositoryRoot)
    {
        var reader = new SimpleYamlReader();
        return new StrategyExperimentArtifactStore(
            Path.Combine(Path.GetTempPath(), "trading-flow-empty-experiments", Guid.NewGuid().ToString("N")),
            CreateStrategyArtifactCatalog(repositoryRoot),
            reader,
            AtomicFileArtifactWriter.Instance);
    }
}

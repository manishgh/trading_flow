using TradingFlow.Engine.Configuration;
using TradingFlow.Engine.Storage;
using TradingFlow.Web.Models;
using TradingFlow.Web.Services;
using Xunit;

namespace TradingFlow.Tests;

public sealed class RunConfigParsingTests
{
    private const string RetainedStrategyFile = "intraday-ema10-ema20-macd-volume.v1.yaml";
    private const string RunnerUpIntradayStrategyFile = "intraday-ema10-ema20-macd-volume.v1.yaml";
    private const string PaperIntradayStrategyFile = "intraday-ema10-ema20-macd-volume.v1.yaml";
    private const string RetainedIntradayStrategyFile = "intraday-ema10-ema20-macd-volume.v1.yaml";
    private const string AdditiveIntradayStrategyFile = "intraday-ema10-ema20-macd-volume.v2-additive.yaml";
    private const string RetainedBacktestFile = "intraday-backtest-profile.yaml";
    private const string RetainedSwingBacktestFile = "swing-backtest-profile.yaml";
    private const string PaperSwingFile = "alpaca-paper-swing.yaml";
    private const string SwingQualityLongStrategyFile = "swing-reversal-reclaim-bull-quality-no-news.v1.yaml";
    private const string SwingOverboughtShortStrategyFile = "swing-overbought-rollover-short-no-news.v5.yaml";
    private const string ShannonAvwapStrategyFile = "brian_shannon_mta_avwap_strategies.yaml";
    private const string BreitsteinVwapTrapStrategyFile = "lance_breitstein_intraday_tactics.yaml";
    private const string QullamaggieEpisodicPivotStrategyFile = "kristjan_qullamaggie_stream_methodology.yaml";
    private const string MinerviniVcpStrategyFile = "minervini-trend-template-vcp.v2.yaml";
    private const string CatalystConfirmationSwingStrategyFile = "swing-catalyst-confirmation-long.v2-adx-obv.yaml";
    private const string CatalystConfirmationSwingAdxObvStrategyFile = "swing-catalyst-confirmation-long.v2-adx-obv.yaml";
    private const string CatalystConfirmationSwingEventStudyStrategyFile = "swing-catalyst-confirmation-long.v3-event-study.yaml";
    private const string CatalystConfirmationSwingFreshConfirmedStrategyFile = "swing-catalyst-confirmation-long.v4-fresh-confirmed.yaml";

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
        Assert.True(config.Execution.ExtendedHours);
        Assert.Equal("full", config.Artifacts.RetentionMode);
        Assert.Single(config.Strategies);
        Assert.Contains(config.Strategies, path => path.EndsWith(PaperIntradayStrategyFile, StringComparison.OrdinalIgnoreCase));
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
        Assert.Equal(260, config.TimeWindow.WarmupLookbackDays);
        Assert.Contains("1h", config.Intervals);
        Assert.Contains("1d", config.Intervals);
        Assert.Equal("1h", config.DerivedTimeframes.Source);
        Assert.True(config.News.Enabled);
        Assert.True(config.Execution.ExtendedHours);
        Assert.Single(config.Strategies);
        Assert.Contains(config.Strategies, path => path.EndsWith(SwingQualityLongStrategyFile, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(config.Strategies, path => path.EndsWith(SwingOverboughtShortStrategyFile, StringComparison.OrdinalIgnoreCase));
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
    public void AdditiveIntradayStrategyConfig_ParsesExperimentalHistogramAndChaseGuards()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", AdditiveIntradayStrategyFile));

        Assert.Equal("TOP1 Intraday - EMA10/20 MACD Histogram Volume V2 Additive", strategy.StrategyName);
        Assert.True(strategy.EntryRules.RequireMacdHistogramPositive);
        Assert.Equal(0.50m, strategy.EntryRules.MinCloseLocationValue);
        Assert.Equal(0.30m, strategy.EntryRules.MaxMacdHistogram);
        Assert.Equal(5, strategy.EntryRules.PriorEntryGainLookbackBars);
        Assert.Equal(1.00m, strategy.EntryRules.MaxPriorEntryGainPct);
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
    public void CatalystConfirmationSwingStrategyConfig_ParsesCatalystAndGapRules()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", CatalystConfirmationSwingStrategyFile));

        Assert.Equal("Catalyst Confirmation Swing Long V2 - ADX OBV", strategy.StrategyName);
        Assert.Equal("1d", strategy.Timeframe);
        Assert.Equal("1h", strategy.Execution.Timeframe);
        Assert.Equal("long", strategy.Direction);
        Assert.Equal("catalyst_confirmation_swing", strategy.EntryRules.SetupType);
        Assert.True(strategy.EntryRules.RequirePositiveNews);
        Assert.Equal(0.10m, strategy.EntryRules.MinNewsSentiment);
        Assert.Equal(-0.40m, strategy.EntryRules.VetoNewsSentimentBelow);
        Assert.Equal(72m, strategy.EntryRules.MaxNewsAgeHours);
        Assert.Equal(3, strategy.EntryRules.MaxCatalystConfirmationBars);
        Assert.Equal(4.0m, strategy.EntryRules.GapVariantMinPct);
        Assert.Equal(0.50m, strategy.EntryRules.MinCatalystPriceMovePct);
        Assert.Equal(12.0m, strategy.EntryRules.MaxCatalystPriceMovePct);
        Assert.Equal(1.50m, strategy.EntryRules.MinVolumeSpike);
        Assert.Equal("finviz_style", strategy.EntryRules.MinVolumeSpikeSource);
        Assert.Equal(20.0m, strategy.EntryRules.MinAdx);
        Assert.True(strategy.EntryRules.RequireAdxRising);
        Assert.True(strategy.EntryRules.RequireObvRising);
        Assert.False(strategy.EntryRules.EnableShort);
        Assert.True(strategy.EntryRules.RequirePriceAboveSma20);
        Assert.True(strategy.EntryRules.RequirePriceAboveSma50);
        Assert.True(strategy.EntryRules.RequireSma20AboveSma50);
        Assert.False(strategy.EntryRules.RequireMacdHistogramPositive);
        Assert.True(strategy.ExitRules.EnableAtrTrailingStop);
        Assert.Equal(1.0m, strategy.ExitRules.TrailingActivationR);
        Assert.Equal(3.0m, strategy.ExitRules.TargetRMultiple);
        Assert.True(strategy.ExitRules.ExitOnSma10CrossBelowSma20);
    }

    [Fact]
    public void CatalystConfirmationSwingAdxObvStrategyConfig_ParsesAdxObvRules()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", CatalystConfirmationSwingAdxObvStrategyFile));

        Assert.Equal("Catalyst Confirmation Swing Long V2 - ADX OBV", strategy.StrategyName);
        Assert.Equal("1d", strategy.Timeframe);
        Assert.Equal("1h", strategy.Execution.Timeframe);
        Assert.Equal("long", strategy.Direction);
        Assert.Equal("catalyst_confirmation_swing", strategy.EntryRules.SetupType);
        Assert.True(strategy.EntryRules.RequirePositiveNews);
        Assert.Equal("finviz_style", strategy.EntryRules.MinVolumeSpikeSource);
        Assert.Equal(20.0m, strategy.EntryRules.MinAdx);
        Assert.True(strategy.EntryRules.RequireAdxRising);
        Assert.Equal(3, strategy.EntryRules.AdxRisingLookbackBars);
        Assert.True(strategy.EntryRules.RequireObvRising);
        Assert.Equal(3, strategy.EntryRules.ObvRisingLookbackBars);
        Assert.Equal(0m, strategy.EntryRules.MinObvChange);
        Assert.False(strategy.EntryRules.EnableShort);
    }
    [Fact]
    public void CatalystConfirmationSwingEventStudyStrategyConfig_UsesSoftVolumeAndWiderCatalystWindow()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategy = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", CatalystConfirmationSwingEventStudyStrategyFile));

        Assert.Equal("Research Swing Long V3 - Catalyst Confirmation Event Study", strategy.StrategyName);
        Assert.Equal("1h", strategy.Timeframe);
        Assert.Equal("1h", strategy.Execution.Timeframe);
        Assert.Equal("long", strategy.Direction);
        Assert.Equal("catalyst_confirmation_swing", strategy.EntryRules.SetupType);
        Assert.True(strategy.EntryRules.RequirePositiveNews);
        Assert.Equal(-0.05m, strategy.EntryRules.MinNewsSentiment);
        Assert.Equal(-0.40m, strategy.EntryRules.VetoNewsSentimentBelow);
        Assert.Equal(120m, strategy.EntryRules.MaxNewsAgeHours);
        Assert.Equal(48, strategy.EntryRules.MaxCatalystConfirmationBars);
        Assert.Null(strategy.EntryRules.MinCatalystPriceMovePct);
        Assert.Equal(15.0m, strategy.EntryRules.MaxCatalystPriceMovePct);
        Assert.Equal(1.00m, strategy.EntryRules.MinVolumeSpike);
        Assert.Equal("finviz_style", strategy.EntryRules.MinVolumeSpikeSource);
        Assert.Equal("soft_marker", strategy.EntryRules.VolumeConfirmationMode);
        Assert.Null(strategy.EntryRules.MinAdx);
        Assert.False(strategy.EntryRules.RequireAdxRising);
        Assert.False(strategy.EntryRules.RequireObvRising);
        Assert.True(strategy.EntryRules.RequirePriceAboveEma20);
        Assert.False(strategy.EntryRules.RequirePriceAboveSma50);
        Assert.False(strategy.EntryRules.RejectWeakCloseOnHighRelativeVolume);
        Assert.True(strategy.Session.UseExtendedHours);
        Assert.True(strategy.ExitRules.EnableAtrTrailingStop);
        Assert.Equal(3.0m, strategy.ExitRules.TargetRMultiple);
    }

    [Fact]
    public void ImportedResearchStrategies_ParseWithRuleSpecificFields()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();

        var shannon = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", ShannonAvwapStrategyFile));
        Assert.Equal("brian_shannon_mta_avwap_strategies", shannon.Source);
        Assert.Equal("avwap_pullback_bounce", shannon.EntryRules.SetupType);
        Assert.Equal("recent_gap_or_high_volume_node", shannon.EntryRules.AnchorType);
        Assert.True(shannon.EntryRules.RequirePriceAboveSma50Daily);
        Assert.True(shannon.EntryRules.RequirePriceAboveSma200Daily);
        Assert.Equal(1.5m, shannon.EntryRules.AvwapProximityPct);
        Assert.True(shannon.EntryRules.RequirePullbackVolumeDryup);
        Assert.True(shannon.EntryRules.RequirePriceAboveEma5_65m);
        Assert.Equal(1.25m, shannon.EntryRules.MinBounceVolumeRatio);
        Assert.False(shannon.Confluence.Enabled);

        var breitstein = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", BreitsteinVwapTrapStrategyFile));
        Assert.Equal("lance_breitstein_intraday_tactics", breitstein.Source);
        Assert.Equal("vwap_reclaim_trap", breitstein.EntryRules.SetupType);
        Assert.Equal(2.0m, breitstein.EntryRules.MinSessionRelativeVolume);
        Assert.Equal(3, breitstein.EntryRules.RequirePriorFlushBelowVwapBars);
        Assert.Equal(12, breitstein.EntryRules.VwapReclaimMaxBarsSinceFlush);
        Assert.Equal(1.50m, breitstein.EntryRules.MinReclaimVolumeRatio);
        Assert.Equal("1m", breitstein.Execution.Timeframe);

        var qullamaggie = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", QullamaggieEpisodicPivotStrategyFile));
        Assert.Equal("kristjan_qullamaggie_stream_methodology", qullamaggie.Source);
        Assert.Equal("episodic_pivot_gap", qullamaggie.EntryRules.SetupType);
        Assert.Equal(8.0m, qullamaggie.EntryRules.MinGapUpPct);
        Assert.Equal(3.0m, qullamaggie.EntryRules.MinSessionRelativeVolume);
        Assert.True(qullamaggie.EntryRules.RequirePositiveNews);
        Assert.Equal(24m, qullamaggie.EntryRules.MaxNewsAgeHours);

        var minervini = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", MinerviniVcpStrategyFile));
        Assert.Equal("mark_minervini_trade_like_a_stock_market_wizard", minervini.Source);
        Assert.Equal("volatility_contraction_pattern", minervini.EntryRules.SetupType);
        Assert.Equal(2, minervini.Version);
        Assert.True(minervini.EntryRules.RequirePriceAboveBollingerMiddle);
        Assert.True(minervini.EntryRules.RequireMacdHistogramPositive);
        Assert.True(minervini.EntryRules.RequirePriceAboveEma10);
        Assert.True(minervini.EntryRules.RequirePriceAboveEma20);
        Assert.True(minervini.EntryRules.RequirePriceAboveEma50);
        Assert.True(minervini.EntryRules.RequireEma10AboveEma20);
        Assert.True(minervini.EntryRules.RequireEma20AboveEma50);
        Assert.Equal(25, minervini.EntryRules.VolatilityContractionLookbackBars);
        Assert.Equal(0.55m, minervini.EntryRules.MinCloseLocationValue);
        Assert.True(minervini.ExitRules.EnableAtrTrailingStop);
        Assert.Equal(4.0m, minervini.ExitRules.TargetRMultiple);
    }

    [Fact]
    public void AllStrategyConfigs_ParseWithoutActivatingUnreferencedStrategies()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var strategyDir = Path.Combine(repoRoot, "configs", "strategies");

        foreach (var strategyPath in Directory.EnumerateFiles(strategyDir, "*.yaml"))
        {
            var strategy = reader.ReadStrategy(strategyPath);

            Assert.False(string.IsNullOrWhiteSpace(strategy.StrategyId));
            Assert.False(string.IsNullOrWhiteSpace(strategy.EntryRules.SetupType));
        }
    }

    [Fact]
    public void MobileAutomationTimeframes_UseFinestExecutionSourceForShannonSwing()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var config = reader.ReadBacktestRun(Path.Combine(repoRoot, "configs", "paper", PaperSwingFile));
        var strategy = reader.ReadStrategy(Path.Combine(repoRoot, "configs", "strategies", ShannonAvwapStrategyFile));
        var serviceType = typeof(MobileAutomationService);

        var required = (string[])serviceType
            .GetMethod("ResolveRequiredTimeframes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { strategy })!;

        var download = (string[])serviceType
            .GetMethod("ResolveDownloadTimeframesForAutomation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { config, strategy, required })!;

        var deriveFrom = (string)serviceType
            .GetMethod("ResolveDeriveFromTimeframe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { config, required, download })!;

        Assert.Contains("65m", required);
        Assert.Contains("5m", required);
        Assert.Contains("5m", download);
        Assert.Contains("1d", download);
        Assert.Equal("5m", deriveFrom);
    }

    [Fact]
    public void RetainedIntradayBacktestProfile_UsesWishlistUniverseAndTopTwoStrategies()
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
        Assert.Equal(1.0m, config.Portfolio.RiskPerTradePct);
        Assert.Equal(25.0m, config.Portfolio.MaxPositionValuePct);
        Assert.Equal(4, config.Portfolio.MaxConcurrentPositions);
        Assert.False(config.News.Enabled);
        Assert.Equal("1m", config.DerivedTimeframes.Source);
        Assert.Equal("wishlist", config.Validation.BiasRisk.UniverseSource);
        Assert.Empty(config.Tickers);
        Assert.Single(config.Strategies);
        Assert.Contains(config.Strategies, path => path.EndsWith(RetainedIntradayStrategyFile, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RetainedSwingBacktestProfile_UsesWishlistUniverseAndPromotedLongAndShortStrategies()
    {
        var repoRoot = FindRepositoryRoot();
        var reader = new SimpleYamlReader();
        var config = reader.ReadBacktestRun(Path.Combine(repoRoot, "configs", "backtest", RetainedSwingBacktestFile));

        Assert.Equal("swing-backtest-profile", config.RunName);
        Assert.Equal(180, config.TimeWindow.LookbackDays);
        Assert.Equal(260, config.TimeWindow.WarmupLookbackDays);
        Assert.Equal("alpaca", config.Provider);
        Assert.Equal("sip", config.Providers.Alpaca.DataFeed);
        Assert.Contains("1h", config.Intervals);
        Assert.Contains("1d", config.Intervals);
        Assert.Equal("1h", config.DerivedTimeframes.Source);
        Assert.Equal("full", config.Artifacts.RetentionMode);
        Assert.Equal("wishlist", config.Validation.BiasRisk.UniverseSource);
        Assert.Empty(config.Tickers);
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
                WishlistId: Guid.Parse("220939c3-1e91-4c4e-9bca-5d61f9837ebd"),
                WishlistName: "volatile",
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
            Assert.Contains("  warmup_lookback_days: 90", generatedYaml);
            Assert.Contains("universe:", generatedYaml);
            Assert.Contains("  source: wishlist", generatedYaml);
            Assert.Contains("  wishlist_name: volatile", generatedYaml);
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
                AtomicFileArtifactWriter.Instance);

            var generatedPath = writer.WriteBacktestConfig(new BacktestRunRequest(
                BaseConfigPath: baseConfigPath,
                RunName: "writer-timeframe-test",
                LookbackDays: 30,
                WishlistId: Guid.Parse("220939c3-1e91-4c4e-9bca-5d61f9837ebd"),
                WishlistName: "volatile",
                Tickers: ["PLUG"],
                StrategyPaths: [strategyPath],
                StartingCapital: 10000m,
                RiskPerTradePct: 2m,
                MaxPositionValuePct: 25m,
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
            var writer = new RunConfigWriter(
                new ProjectPaths(tempRoot),
                new SimpleYamlReader(),
                AtomicFileArtifactWriter.Instance);

            var generatedPath = writer.SaveTempConfig(
                baseConfigPath,
                ["MU", "NVDA"],
                strategyPath,
                orderExpiration: "day",
                entryOrderType: "market",
                extendedHours: true,
                screenerFilter: "",
                runName: "paper-news-disabled-test",
                newsEnabled: false);

            var generatedYaml = File.ReadAllText(generatedPath);
            var parsed = new SimpleYamlReader().ReadBacktestRun(generatedPath);

            Assert.Single(System.Text.RegularExpressions.Regex.Matches(generatedYaml, "^news:", System.Text.RegularExpressions.RegexOptions.Multiline));
            Assert.Contains("news:", generatedYaml);
            Assert.Contains("  enabled: false", generatedYaml);
            Assert.False(parsed.News.Enabled);
            Assert.Equal("market", parsed.Execution.EntryOrderType);
            Assert.Equal("day", parsed.Execution.OrderExpiration);
            Assert.True(parsed.Execution.ExtendedHours);
            Assert.Equal(["MU", "NVDA"], parsed.Tickers);
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
}







